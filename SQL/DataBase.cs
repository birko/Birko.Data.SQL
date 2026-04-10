using Birko.Data.SQL.Conditions;
using Birko.Data.SQL.Connectors;
using Birko.Data.SQL.Fields;
using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Collections.Concurrent;
using System.Data;
using Birko.Configuration;

namespace Birko.Data.SQL
{
    public static partial class DataBase
    {
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, AbstractConnector>> _connectors = new();
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, AbstractAsyncConnector>> _asyncConnectors = new();
        private static readonly ConcurrentDictionary<string, Func<object>> _expressionCache = new();

        /// <summary>
        /// Extension point for resolving field select names from non-table sources (e.g. views).
        /// Parameters: (Type exprType, string propertyName, bool withTableName) → field select name or null.
        /// Set by Birko.Data.SQL.View to break the dependency from SQL → View.
        /// </summary>
        public static Func<Type, string, bool, string?>? ResolveFieldSelectName { get; set; }
        private static readonly HashSet<DbType> _stringTypes = new()
        {
            DbType.Guid,
            DbType.String,
            DbType.StringFixedLength,
            DbType.AnsiString,
            DbType.AnsiStringFixedLength
        };

        public static AbstractConnector GetConnector<T>(Settings settings) where T : AbstractConnector
        {
            var connectorType = typeof(T);
            var settingsId = settings.GetId();

            var connectorDict = _connectors.GetOrAdd(connectorType, _ => new ConcurrentDictionary<string, AbstractConnector>());
            return connectorDict.GetOrAdd(settingsId, id =>
            {
                return (AbstractConnector)Activator.CreateInstance(typeof(T), new object[] { settings })!;
            });
        }

        public static AbstractAsyncConnector GetAsyncConnector<T>(Settings settings) where T : AbstractAsyncConnector
        {
            var connectorType = typeof(T);
            var settingsId = settings.GetId();

            var connectorDict = _asyncConnectors.GetOrAdd(connectorType, _ => new ConcurrentDictionary<string, AbstractAsyncConnector>());
            return connectorDict.GetOrAdd(settingsId, id =>
            {
                return (AbstractAsyncConnector)Activator.CreateInstance(typeof(T), new object[] { settings })!;
            });
        }

        private static void GetProperties(Type type, Action<PropertyInfo> action)
        {
            foreach (var field in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var accesors = field.GetAccessors();
                if (accesors.Any(x => !x.IsStatic))
                {
                    action?.Invoke(field);
                }
            }
        }

        public static string GetGeneratedQuery(DbCommand dbCommand)
        {
            var query = new StringBuilder(dbCommand.CommandText);
            foreach (DbParameter parameter in dbCommand.Parameters)
            {
                bool isString = _stringTypes.Contains(parameter.DbType);
                string? value = isString
                        ? "'" + parameter.Value?.ToString() + "'"
                        : parameter.Value?.ToString();
                query.Replace(parameter.ParameterName, value);
            }

            return query.ToString();
        }

        public static Conditions.Condition CreateCondition(AbstractField field, object value)
        {
            return new Conditions.Condition(field.Name, new[] { field.Property.GetValue(value, null) });
        }

        public static string? ParseExpression(Expression expr, IDictionary<string, object> parameters, bool withTableName = false, Type? exprType = null)
        {
            if (expr != null)
            {
                if (expr is LambdaExpression lambdaExpression)
                {
                    var type = lambdaExpression.Parameters?.FirstOrDefault()?.Type;
                    return ParseExpression(lambdaExpression.Body, parameters, withTableName, type);
                }
                else if (expr is BinaryExpression binaryExpression)
                {
                    var left = ParseExpression(binaryExpression.Left, parameters, withTableName, exprType);
                    var right = ParseExpression(binaryExpression.Right, parameters, withTableName, exprType);
                    StringBuilder result = new StringBuilder();
                    result.Append("(");
                    result.Append(left);
                    switch (binaryExpression.NodeType)
                    {
                        case ExpressionType.Add:
                        case ExpressionType.AddChecked:
                            result.Append(" + ");
                            break;
                        case ExpressionType.Subtract:
                        case ExpressionType.SubtractChecked:
                            result.Append(" - ");
                            break;
                        case ExpressionType.Multiply:
                        case ExpressionType.MultiplyChecked:
                            result.Append(" * ");
                            break;
                        case ExpressionType.Divide:
                            result.Append(" / ");
                            break;
                        case ExpressionType.Modulo:
                            result.Append(" % ");
                            break;
                        case ExpressionType.GreaterThan:
                            result.Append(" > ");
                            break;
                        case ExpressionType.GreaterThanOrEqual:
                            result.Append(" >= ");
                            break;
                        case ExpressionType.LessThan:
                            result.Append(" < ");
                            break;
                        case ExpressionType.LessThanOrEqual:
                            result.Append(" <= ");
                            break;
                        case ExpressionType.Equal:
                            result.Append(" = ");
                            break;
                        case ExpressionType.NotEqual:
                            result.Append(" <> ");
                            break;
                        case ExpressionType.And:
                        case ExpressionType.AndAlso:
                            result.Append(" AND ");
                            break;
                        case ExpressionType.Or:
                        case ExpressionType.OrElse:
                            result.Append(" OR ");
                            break;
                    }
                    result.Append(right);
                    result.Append(")");
                    return result.ToString();
                }
                else if (expr is MethodCallExpression callExpression)
                {
                    if (callExpression.Method.Name == "Replace")
                    {
                        StringBuilder result = new StringBuilder();
                        //maybe platform specific implementation
                        result.AppendFormat("REPLACE({0}", ParseExpression(callExpression.Object!, parameters, withTableName, exprType));
                        foreach (var argument in callExpression.Arguments)
                        {
                            result.AppendFormat(", {0}", ParseExpression(argument, parameters, withTableName, exprType));
                        }
                        result.Append(")");
                        return result.ToString();
                    }
                    else
                    {
                        var key = "@Const" + parameters.Count;
                        var value = EvaluateExpression(callExpression);
                        parameters.Add(key, value!);
                        return key;
                    }
                }
                else if (expr is UnaryExpression unaryExpression)
                {
                    if (unaryExpression.NodeType == ExpressionType.Convert)
                    {
                        return ParseExpression(unaryExpression.Operand, parameters, withTableName, exprType);
                    }
                }
                else if (expr is MemberExpression memberExpression)
                {
                    // Handle DateTime property accessors like x.OpenedAt.Date → DATE(column)
                    if (memberExpression.Member.Name == "Date"
                        && memberExpression.Member.DeclaringType == typeof(DateTime)
                        && memberExpression.Expression is MemberExpression innerDateMember)
                    {
                        var columnExpr = ParseExpression(innerDateMember, parameters, withTableName, exprType);
                        if (!string.IsNullOrEmpty(columnExpr) && !columnExpr.StartsWith("@"))
                        {
                            return $"DATE({columnExpr})";
                        }
                        // If inner resolved to a parameter, evaluate the whole .Date as constant
                        var key = "@Const" + parameters.Count;
                        var value = EvaluateExpression(memberExpression);
                        parameters.Add(key, value!);
                        return key;
                    }

                    string name = string.Empty;
                    if (
                        exprType != null
                        && memberExpression.Member.ReflectedType?.IsAssignableFrom(exprType) == true
                        && (memberExpression.Expression?.NodeType == ExpressionType.Parameter || memberExpression.Expression?.NodeType == ExpressionType.TypeAs)
                    )
                    {
                        var table = LoadTable(exprType);
                        if (table != null)
                        {
                            var field = table.GetFieldByPropertyName(memberExpression.Member.Name);
                            if (field != null)
                            {
                                name = field.GetSelectName(withTableName);
                            }
                        }
                        else
                        {
                            name = ResolveFieldSelectName?.Invoke(exprType, memberExpression.Member.Name, withTableName) ?? string.Empty;
                        }
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        if (memberExpression.Expression is ConstantExpression constantExpression)
                        {
                            Type type = constantExpression.Value!.GetType();
                            var value = type.InvokeMember(memberExpression.Member.Name, BindingFlags.GetField, null, constantExpression.Value, null);
                            var key = "@Const" + parameters.Count;
                            parameters.Add(key, value!);
                            return key;
                        }
                        else if (memberExpression.Expression != null)
                        {
                            return ParseExpression(memberExpression.Expression, parameters, withTableName); // not resending type here
                        }
                        else
                        {
                            var key = "@Const" + parameters.Count;
                            var value = EvaluateExpression(memberExpression);
                            parameters.Add(key, value!);
                            return key;
                        }
                    }
                    else
                    {
                        return name;
                    }
                }
                else if (expr is ConstantExpression constantExpression)
                {
                    var key = "@Const" + parameters.Count;
                    parameters.Add(key, constantExpression.Value!);
                    return key;
                }
            }
            return null;
        }

        public static IEnumerable<Conditions.Condition> ParseConditionExpression(Expression? expr = null, Conditions.Condition? parent = null, Type? exprType = null)
        {
            if (expr != null)
            {
                if (expr is LambdaExpression lambdaExpression)
                {
                    // Handle constant boolean body: _ => true means "no filter" (return empty),
                    // _ => false means "match nothing" (return impossible condition 1=0).
                    if (lambdaExpression.Body is ConstantExpression constBody && constBody.Value is bool boolVal)
                    {
                        if (boolVal)
                        {
                            return Array.Empty<Conditions.Condition>();
                        }
                        else
                        {
                            // false → WHERE 1=0 — represented as a condition with literal name
                            var falseCondition = parent ?? new Conditions.Condition(null, null);
                            falseCondition.Name = "1";
                            falseCondition.Values = new object[] { 0 };
                            falseCondition.Type = ConditionType.Equal;
                            return new[] { falseCondition };
                        }
                    }
                    var type = lambdaExpression.Parameters?.FirstOrDefault()?.Type;
                    var res = ParseConditionExpression(lambdaExpression.Body, parent, type);
                    return res;
                }
                else if (expr is UnaryExpression unaryExpression)
                {
                    if (unaryExpression.NodeType == ExpressionType.Convert || unaryExpression.NodeType == ExpressionType.TypeAs)
                    {
                        return ParseConditionExpression(unaryExpression.Operand, parent, exprType);
                    }
                    if (unaryExpression.NodeType == ExpressionType.Not)
                    {
                        // Not(HasValue) → IS NULL
                        if (unaryExpression.Operand is MemberExpression notMember)
                        {
                            var hasValueResult = TryParseHasValue(notMember, parent, exprType, isNegated: true);
                            if (hasValueResult != null) return hasValueResult;
                        }
                        // General Not: recurse and set IsNot on the result
                        var notCondition = parent ?? new Conditions.Condition(null, null);
                        notCondition.IsNot = !notCondition.IsNot; // toggle
                        return ParseConditionExpression(unaryExpression.Operand, notCondition, exprType);
                    }
                    if (parent != null)
                    {
                        return new[] { parent };
                    }
                }

                if (expr is BinaryExpression binaryExpression)
                {
                    bool isAnd = false;
                    bool isOR = false;
                    bool isNot = false;
                    ConditionType conditionType = ConditionType.Equal;
                    switch (expr.NodeType)
                    {
                        case ExpressionType.And:
                        case ExpressionType.AndAlso:
                            isAnd = true;
                            isOR = false;
                            break;
                        case ExpressionType.Or:
                        case ExpressionType.OrElse:
                            isAnd = false;
                            isOR = true;
                            break;
                        case ExpressionType.Equal:
                            conditionType = ConditionType.Equal;
                            break;
                        case ExpressionType.NotEqual:
                            conditionType = ConditionType.Equal;
                            isNot = true;
                            break;
                        case ExpressionType.LessThan:
                            conditionType = ConditionType.Less;
                            break;
                        case ExpressionType.LessThanOrEqual:
                            conditionType = ConditionType.LessAndEqual;
                            break;
                        case ExpressionType.GreaterThan:
                            conditionType = ConditionType.Greather;
                            break;
                        case ExpressionType.GreaterThanOrEqual:
                            conditionType = ConditionType.GreatherAndEqual;
                            break;
                    }

                    if (isAnd || isOR)
                    {
                        var basecondition = new Conditions.Condition(null, null)
                        {
                            IsOr = isOR,
                            Type = conditionType,
                            IsNot = isNot,
                        };
                        var left = new Conditions.Condition(null, null);
                        ParseConditionExpression(binaryExpression.Left, left, exprType);
                        var right = new Conditions.Condition(null, null);
                        ParseConditionExpression(binaryExpression.Right, right, exprType);

                        // Handle constant boolean operands in AND/OR:
                        //   AND: true is identity (skip), false short-circuits (only false)
                        //   OR:  true short-circuits (skip whole clause), false is identity (skip)
                        bool leftIsConst = IsConstantBoolCondition(left, out bool leftVal);
                        bool rightIsConst = IsConstantBoolCondition(right, out bool rightVal);

                        if (leftIsConst && rightIsConst)
                        {
                            bool result = isAnd ? (leftVal && rightVal) : (leftVal || rightVal);
                            return result ? Array.Empty<Conditions.Condition>() : new[] { MakeFalseCondition(parent) };
                        }
                        if (leftIsConst)
                        {
                            if (isAnd && leftVal) return ReturnSingleSubCondition(parent, right, isOR);
                            if (isAnd && !leftVal) return new[] { MakeFalseCondition(parent) };
                            if (isOR && leftVal) return Array.Empty<Conditions.Condition>();
                            if (isOR && !leftVal) return ReturnSingleSubCondition(parent, right, isOR);
                        }
                        if (rightIsConst)
                        {
                            if (isAnd && rightVal) return ReturnSingleSubCondition(parent, left, isOR);
                            if (isAnd && !rightVal) return new[] { MakeFalseCondition(parent) };
                            if (isOR && rightVal) return Array.Empty<Conditions.Condition>();
                            if (isOR && !rightVal) return ReturnSingleSubCondition(parent, left, isOR);
                        }

                        if (parent != null)
                        {
                            // Transfer the OR/AND flag to the parent so the SQL generator
                            // knows to join subconditions with OR instead of AND.
                            parent.IsOr = isOR;
                            var subs = parent.SubConditions as List<Condition> ?? new List<Condition>(parent.SubConditions ?? []);
                            subs.Add(left);
                            subs.Add(right);
                            parent.SubConditions = subs;
                            return new[] { parent };
                        }
                        else
                        {
                            basecondition.SubConditions = new[] { left, right };
                            return new[] { basecondition };
                        }
                    }
                    else
                    {
                        // If neither side references a lambda parameter, the whole comparison
                        // is a constant expression (e.g. `status == null`, `hasConfig == true`).
                        // Evaluate it at parse time and return a constant bool condition.
                        if (!ContainsParameter(binaryExpression.Left) && !ContainsParameter(binaryExpression.Right))
                        {
                            try
                            {
                                var constVal = EvaluateExpression(binaryExpression);
                                if (constVal is bool boolVal)
                                {
                                    if (boolVal)
                                        return Array.Empty<Conditions.Condition>();
                                    else
                                        return new[] { MakeFalseCondition(parent) };
                                }
                            }
                            catch { /* fall through to normal handling */ }
                        }

                        var basecondition = new Conditions.Condition(null, null)
                        {
                            IsOr = isOR,
                            Type = conditionType,
                            IsNot = isNot,
                        };
                        var left = ParseConditionExpression(binaryExpression.Left, basecondition, exprType);
                        var right = ParseConditionExpression(binaryExpression.Right, basecondition, exprType);
                        if (parent != null)
                        {
                            var subs = parent.SubConditions as List<Condition> ?? new List<Condition>(parent.SubConditions ?? []);
                            subs.Add(basecondition);
                            parent.SubConditions = subs;
                            return new[] { parent };
                        }
                        else
                        {
                            return new[] { basecondition };
                        }
                    }
                }
                else if (expr is MethodCallExpression methodExpression)
                {
                    var condition = parent ?? new Conditions.Condition(null, null);
                    if (methodExpression.Method.Name == "StartsWith")
                    {
                        condition.Type = ConditionType.StartsWith;
                    }
                    if (methodExpression.Method.Name == "EndsWith")
                    {
                        condition.Type = ConditionType.EndsWith;
                    }
                    if (methodExpression.Method.Name == "Contains")
                    {
                        //condition.Name = 
                        if (methodExpression.Method.DeclaringType?.Name == "String")
                        {
                            condition.Type = ConditionType.Like;
                        }
                        else
                        {
                            condition.Type = ConditionType.In;
                        }
                    }
                    if (methodExpression.Arguments != null && methodExpression.Arguments.Any())
                    {
                        foreach (var arg in methodExpression.Arguments)
                        {
                            ParseConditionExpression(arg, condition, exprType);
                        }
                    }
                    if (methodExpression.Object != null)
                    {
                        ParseConditionExpression(methodExpression.Object, condition, exprType);
                    }
                    return new[] { condition };
                }
                // Bare HasValue access at top level: x.NullableProp.HasValue → Prop IS NOT NULL
                if (expr is MemberExpression topMember)
                {
                    var hasValueResult = TryParseHasValue(topMember, parent, exprType, isNegated: false);
                    if (hasValueResult != null) return hasValueResult;
                }
                // Bare boolean member access: s.IsActive → "IsActive" = 1
                // The C# expression tree represents `s.IsActive` as a MemberExpression
                // without a parent comparison. Convert to an explicit `Prop = true` condition.
                if (expr is MemberExpression bareBoolMember
                    && bareBoolMember.Type == typeof(bool)
                    && exprType != null
                    && bareBoolMember.Member.ReflectedType?.IsAssignableFrom(exprType) == true
                    && (bareBoolMember.Expression?.NodeType == ExpressionType.Parameter
                        || (bareBoolMember.Expression is UnaryExpression bareBoolConvert
                            && bareBoolConvert.NodeType == ExpressionType.Convert
                            && bareBoolConvert.Operand.NodeType == ExpressionType.Parameter)))
                {
                    var condition = parent ?? new Conditions.Condition(null, null);
                    var table = LoadTable(exprType);
                    if (table != null)
                    {
                        var field = table.GetFieldByPropertyName(bareBoolMember.Member.Name);
                        if (field != null) condition.Name = field.GetSelectName(true);
                    }
                    condition.Name ??= ResolveFieldSelectName?.Invoke(exprType, bareBoolMember.Member.Name, true) ?? bareBoolMember.Member.Name;
                    condition.Type = ConditionType.Equal;
                    condition.Values = new object[] { true };
                    return new[] { condition };
                }

                if (parent != null)
                {
                    if (expr is ConstantExpression || expr is MethodCallExpression)
                    {
                        IEnumerable<object>? vals = InvokeExpression(expr);
                        if (vals?.Any(x => x != null) ?? false)
                        {
                            parent.Values = vals.Where(x => x != null);
                        }
                        else
                        {
                            parent.Type = ConditionType.IsNull;
                        }
                    }
                    else if (expr is NewArrayExpression arrayExpresion)
                    {
                        foreach (var arg in arrayExpresion.Expressions)
                        {
                            ParseConditionExpression(arg, parent, exprType);
                        }
                    }
                    else if (expr is MemberExpression memberExpression)
                    {
                        // Non-parameter member access (closure variable, e.g. configId.Value,
                        // filter.Status, local variable) — evaluate as a constant value.
                        // Must be checked BEFORE column-resolution logic to avoid treating
                        // closure fields as DB columns.
                        if (!ContainsParameter(memberExpression))
                        {
                            // Special case: HasValue on closure nullable → evaluate as bool constant
                            if (memberExpression.Member.Name == "HasValue"
                                && memberExpression.Member.ReflectedType != null
                                && Nullable.GetUnderlyingType(memberExpression.Member.ReflectedType) != null)
                            {
                                var boolVal = EvaluateExpression(memberExpression);
                                parent.Values = new[] { boolVal };
                                return new[] { parent };
                            }

                            var value = EvaluateExpression(memberExpression);
                            parent.Values = (value != null && !(value is string) && (value is IEnumerable enumerable))
                                ? enumerable
                                : new[] { value };
                            return new[] { parent };
                        }

                        // Bare HasValue access on parameter: x.NullableProp.HasValue → Prop IS NOT NULL
                        var hasValueResult = TryParseHasValue(memberExpression, parent, exprType, isNegated: false);
                        if (hasValueResult != null) return hasValueResult;

                        // Handle DateTime.Date property: x.OpenedAt.Date → DATE(column)
                        if (memberExpression.Member.Name == "Date"
                            && memberExpression.Member.DeclaringType == typeof(DateTime)
                            && memberExpression.Expression is MemberExpression innerDateMember)
                        {
                            if (ContainsParameter(innerDateMember))
                            {
                                var inner = new Condition(null, null);
                                ParseConditionExpression(innerDateMember, inner, exprType);
                                if (!string.IsNullOrEmpty(inner.Name))
                                {
                                    parent.Name = $"DATE({inner.Name})";
                                    return new[] { parent };
                                }
                            }
                            // Right-hand side: evaluate as constant
                            var dateValue = EvaluateExpression(memberExpression);
                            parent.Values = new[] { dateValue };
                            return Array.Empty<Condition>();
                        }

                        string name = string.Empty;
                        if (
                            exprType != null
                            && memberExpression.Expression != null
                            && memberExpression.Expression.NodeType == ExpressionType.MemberAccess
                            && memberExpression.Member.ReflectedType != null
                            && Nullable.GetUnderlyingType(memberExpression.Member.ReflectedType) != null
                        )
                        {
                            var member = new Condition(null, null);
                            ParseConditionExpression(memberExpression.Expression, member, exprType);
                            name = member.Name ?? string.Empty;
                        }
                        // Check for direct parameter access or Convert(param, Interface) — the C# compiler
                        // generates Convert when accessing interface properties through generic constraints
                        var isParameterAccess = memberExpression.Expression?.NodeType == ExpressionType.Parameter
                            || (memberExpression.Expression is UnaryExpression convertExpr
                                && convertExpr.NodeType == ExpressionType.Convert
                                && convertExpr.Operand.NodeType == ExpressionType.Parameter);
                        if (
                            exprType != null
                            && memberExpression.Expression != null
                            && memberExpression.Member.ReflectedType != null
                            && memberExpression.Member.ReflectedType.IsAssignableFrom(exprType)
                            && isParameterAccess
                        )
                        {
                            var table = LoadTable(exprType);
                            if (table != null)
                            {
                                var field = table.GetFieldByPropertyName(memberExpression.Member.Name);
                                if (field != null)
                                {
                                    name = field.GetSelectName(true);
                                }
                            }
                            else
                            {
                                name = ResolveFieldSelectName?.Invoke(exprType, memberExpression.Member.Name, true) ?? string.Empty;
                            }
                        }
                        if (string.IsNullOrEmpty(name))
                        {
                            if (memberExpression.Expression is ConstantExpression constantExpression)
                            {
                                Type type = constantExpression.Value!.GetType();
                                var value = type.InvokeMember(memberExpression.Member.Name, BindingFlags.GetField | BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, constantExpression.Value, null);
                                parent.Values = (!(value is string) && (value is IEnumerable)) ? (IEnumerable)value : new[] { value };
                            }
                            else if (memberExpression.Expression != null && !ContainsParameter(memberExpression))
                            {
                                // Nested member access on closure (e.g., closure.request.Login)
                                var value = EvaluateExpression(memberExpression);
                                parent.Values = (value != null && !(value is string) && (value is IEnumerable enumerable))
                                    ? enumerable
                                    : new[] { value };
                            }
                            else
                            {
                                IEnumerable<object>? vals = InvokeExpression(expr);
                                if (vals?.Any(x => x != null) ?? false)
                                {
                                    parent.Values = vals.Where(x => x != null);
                                }
                                else 
                                {
                                    parent.Type = ConditionType.IsNull;
                                }
                            }
                        }
                        else
                        {
                            parent.Name = name;
                        }
                    }
                }
            }
            return Array.Empty<Condition>();
        }

        /// <summary>
        /// Tries to parse a HasValue member access on a nullable property into an IS NULL / IS NOT NULL condition.
        /// Returns null if the expression is not a HasValue access.
        /// </summary>
        /// <param name="member">The member expression to check.</param>
        /// <param name="parent">Parent condition to populate, or null to create a new one.</param>
        /// <param name="exprType">The lambda parameter type for column resolution.</param>
        /// <param name="isNegated">True when inside a Not() wrapper (HasValue negated → IS NULL), false for bare HasValue (→ IS NOT NULL).</param>
        private static IEnumerable<Condition>? TryParseHasValue(MemberExpression member, Condition? parent, Type? exprType, bool isNegated)
        {
            if (member.Member.Name != "HasValue"
                || member.Member.ReflectedType == null
                || Nullable.GetUnderlyingType(member.Member.ReflectedType) == null
                || member.Expression is not MemberExpression innerMember)
                return null;

            var condition = parent ?? new Condition(null, null);
            condition.Type = ConditionType.IsNull;
            if (!isNegated)
                condition.IsNot = !condition.IsNot;
            var inner = new Condition(null, null);
            ParseConditionExpression(innerMember, inner, exprType);
            condition.Name = inner.Name;
            return new[] { condition };
        }

        /// <summary>
        /// Checks whether a parsed condition represents a bare constant boolean
        /// (Name is null/empty, single boolean value, default Equal type).
        /// Produced when the expression tree contains a literal true/false operand
        /// such as in <c>_ =&gt; true &amp;&amp; _.DeletedAt == null</c>.
        /// </summary>
        private static bool IsConstantBoolCondition(Conditions.Condition condition, out bool value)
        {
            value = false;
            if (!string.IsNullOrEmpty(condition.Name) || condition.SubConditions?.Any() == true)
                return false;
            if (condition.Values is IEnumerable<object> vals)
            {
                var list = vals.ToList();
                if (list.Count == 1 && list[0] is bool b)
                {
                    value = condition.IsNot ? !b : b;
                    return true;
                }
            }
            // An empty condition (no Name, no SubConditions, no Values) means the
            // sub-expression returned Array.Empty (constant true) and the parent
            // was never populated. Treat it as constant true.
            if (condition.Values == null)
            {
                value = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Creates a WHERE 1=0 condition (always-false) for short-circuiting AND with false.
        /// </summary>
        private static Conditions.Condition MakeFalseCondition(Conditions.Condition? parent)
        {
            var c = parent ?? new Conditions.Condition(null, null);
            c.Name = "1";
            c.Values = new object[] { 0 };
            c.Type = ConditionType.Equal;
            return c;
        }

        /// <summary>
        /// Unwraps a single surviving subcondition when the other operand of AND/OR was a constant.
        /// Transfers its content to the parent if present, or returns it standalone.
        /// </summary>
        private static IEnumerable<Conditions.Condition> ReturnSingleSubCondition(Conditions.Condition? parent, Conditions.Condition surviving, bool isOr)
        {
            if (parent != null)
            {
                parent.IsOr = isOr;
                parent.Name = surviving.Name;
                parent.Values = surviving.Values;
                parent.Type = surviving.Type;
                parent.IsNot = surviving.IsNot;
                parent.SubConditions = surviving.SubConditions;
                return new[] { parent };
            }
            return new[] { surviving };
        }

        private static object? EvaluateExpression(Expression expr)
        {
            if (expr is ConstantExpression c)
                return c.Value;

            if (expr is MemberExpression m)
            {
                object? container = null;
                if (m.Expression != null)
                {
                    container = EvaluateExpression(m.Expression);
                }

                try
                {
                    if (m.Member is FieldInfo fi)
                        return fi.GetValue(container);
                    if (m.Member is PropertyInfo pi)
                        return pi.GetValue(container);
                }
                catch (TargetException)
                {
                    // Non-static member on null container — fall through to lambda compilation
                }
            }

            // Method calls on evaluated objects (e.g., value.ToLowerInvariant(), list.Contains(x))
            if (expr is MethodCallExpression mce)
            {
                object? instance = mce.Object != null ? EvaluateExpression(mce.Object) : null;
                var args = new object?[mce.Arguments.Count];
                for (int i = 0; i < mce.Arguments.Count; i++)
                    args[i] = EvaluateExpression(mce.Arguments[i]);
                return mce.Method.Invoke(instance, args);
            }

            // Unary conversions (e.g., (object)value)
            if (expr is UnaryExpression ue && ue.NodeType == ExpressionType.Convert)
            {
                return EvaluateExpression(ue.Operand);
            }

            if (expr is NewArrayExpression na && na.NodeType == ExpressionType.NewArrayInit)
            {
                var elementType = na.Type.GetElementType();
                if (elementType == null)
                    return null;

                var list = Array.CreateInstance(elementType, na.Expressions.Count);
                for (int i = 0; i < na.Expressions.Count; i++)
                {
                    list.SetValue(EvaluateExpression(na.Expressions[i]), i);
                }
                return list;
            }

            // Fallback: compile as parameterless lambda — but only if the expression
            // does not reference any unbound ParameterExpressions (lambda parameters like 'u').
            if (!ContainsParameter(expr))
            {
                var cacheKey = expr.ToString();
                var func = _expressionCache.GetOrAdd(cacheKey, _ =>
                {
                    // Box value types (Guid, int, bool, enum, DateTime) so the compiled
                    // delegate is always Func<object>, not Func<T>.
                    var body = expr.Type.IsValueType
                        ? Expression.Convert(expr, typeof(object))
                        : expr;
                    var lambda = Expression.Lambda<Func<object>>(body);
                    return lambda.Compile();
                });
                return func();
            }

            return null;
        }

        /// <summary>
        /// Checks whether the expression tree contains any ParameterExpression (lambda parameters).
        /// Expressions with unbound parameters cannot be compiled as parameterless lambdas.
        /// </summary>
        private static bool ContainsParameter(Expression expr)
        {
            if (expr is LambdaExpression lambda)
                return lambda.Parameters.Count > 0;
            if (expr is ParameterExpression)
                return true;
            if (expr is MemberExpression me)
                return me.Expression != null && ContainsParameter(me.Expression);
            if (expr is MethodCallExpression mc)
            {
                if (mc.Object != null && ContainsParameter(mc.Object))
                    return true;
                return mc.Arguments.Any(ContainsParameter);
            }
            if (expr is UnaryExpression ue)
                return ContainsParameter(ue.Operand);
            if (expr is BinaryExpression be)
                return ContainsParameter(be.Left) || ContainsParameter(be.Right);
            if (expr is ConditionalExpression ce)
                return ContainsParameter(ce.Test) || ContainsParameter(ce.IfTrue) || ContainsParameter(ce.IfFalse);
            return false;
        }

        private static IEnumerable<object>? InvokeExpression(Expression expr)
        {
            object? value = EvaluateExpression(expr);
            if (value == null) return null;

            if (value is string)
                return new object[] { value };
            if (value is IEnumerable enumerable)
                return enumerable.Cast<object>().Where(x => x != null);

            return new object[] { value };
        }
    }
}
