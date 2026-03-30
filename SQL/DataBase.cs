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
                        var key = "@Constat" + parameters.Count;
                        var f = Expression.Lambda(callExpression).Compile();
                        var value = f.DynamicInvoke();
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
                            var key = "@Constat" + parameters.Count;
                            parameters.Add(key, value!);
                            return key;
                        }
                        else if (memberExpression.Expression != null)
                        {
                            return ParseExpression(memberExpression.Expression, parameters, withTableName); // not resending type here
                        }
                        else
                        {
                            var key = "@Constat" + parameters.Count;
                            var f = Expression.Lambda(memberExpression).Compile();
                            var value = f.DynamicInvoke();
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
                    var key = "@Constat" + parameters.Count;
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
                        // Not(MemberAccess(MemberAccess(param, Prop), HasValue)) → Prop IS NULL
                        // C# compiler generates this for `x.NullableProp == null`
                        if (unaryExpression.Operand is MemberExpression notMember
                            && notMember.Member.Name == "HasValue"
                            && notMember.Expression is MemberExpression innerMember
                            && Nullable.GetUnderlyingType(notMember.Member.ReflectedType!) != null)
                        {
                            var condition = parent ?? new Conditions.Condition(null, null);
                            condition.Type = ConditionType.IsNull;
                            // Resolve the property name from the inner member (the actual nullable property)
                            var inner = new Conditions.Condition(null, null);
                            ParseConditionExpression(innerMember, inner, exprType);
                            condition.Name = inner.Name;
                            return new[] { condition };
                        }
                        // MemberAccess(MemberAccess(param, Prop), HasValue) without Not → Prop IS NOT NULL
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
                            parent.SubConditions = (parent.SubConditions ?? []).Union(new[] { left, right });
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
                            parent.SubConditions = (parent.SubConditions ?? []).Union(new[] { basecondition }).AsEnumerable();
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
                // Must be checked before the parent != null guard because the lambda body
                // can be a bare HasValue expression with no parent.
                if (expr is MemberExpression topMember
                    && topMember.Member.Name == "HasValue"
                    && topMember.Expression is MemberExpression topHasValueInner
                    && topMember.Member.ReflectedType != null
                    && Nullable.GetUnderlyingType(topMember.Member.ReflectedType) != null)
                {
                    var condition = parent ?? new Conditions.Condition(null, null);
                    condition.Type = ConditionType.IsNull;
                    condition.IsNot = !condition.IsNot; // HasValue without Not = IS NOT NULL
                    var inner = new Conditions.Condition(null, null);
                    ParseConditionExpression(topHasValueInner, inner, exprType);
                    condition.Name = inner.Name;
                    return new[] { condition };
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
                        // Bare HasValue access: x.NullableProp.HasValue → Prop IS NOT NULL
                        if (memberExpression.Member.Name == "HasValue"
                            && memberExpression.Expression is MemberExpression hasValueInner
                            && memberExpression.Member.ReflectedType != null
                            && Nullable.GetUnderlyingType(memberExpression.Member.ReflectedType) != null)
                        {
                            var condition = parent ?? new Conditions.Condition(null, null);
                            condition.Type = ConditionType.IsNull;
                            condition.IsNot = !condition.IsNot; // HasValue without Not = IS NOT NULL
                            var inner = new Conditions.Condition(null, null);
                            ParseConditionExpression(hasValueInner, inner, exprType);
                            condition.Name = inner.Name;
                            return new[] { condition };
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
                    value = b;
                    return true;
                }
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
                    var lambda = Expression.Lambda(expr);
                    return (Func<object>)lambda.Compile();
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
            if (value == null)
            {
                return null;
            }

            List<object> vals = new List<object>();
            var valueType = value.GetType();
            if (valueType.IsPrimitive || valueType == typeof(string) || valueType == typeof(Guid))
            {
                vals.Add(value);
            }
            else if (valueType.IsArray)
            {
                foreach (var item in (Array)value)
                {
                    vals.Add(item);
                }
            }
            else
            {
                var fields = valueType.GetFields();
                if (fields.Any())
                {
                    foreach (var field in fields)
                    {
                        vals.Add(field.GetValue(value)!);
                    }
                }
            }

            return vals?.Where(x => x != null);
        }
    }
}
