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
        private static readonly ConcurrentDictionary<Type, Func<object>> _instanceFactories = new();

        /// <summary>
        /// Returns a compiled parameterless constructor delegate for <paramref name="type"/>,
        /// cached per type. Avoids Activator.CreateInstance reflection on every row.
        /// </summary>
        internal static Func<object> GetOrCreateInstanceFactory(Type type)
        {
            return _instanceFactories.GetOrAdd(type, static t =>
            {
                var ctor = t.GetConstructor(Type.EmptyTypes)
                    ?? throw new InvalidOperationException($"Type {t.FullName} has no parameterless constructor");
                var newExpr = Expression.New(ctor);
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(newExpr, typeof(object)));
                return lambda.Compile();
            });
        }
        // Keyed by Expression reference identity — avoids ToString() traversal and prevents memory leaks via weak refs
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Expression, Func<object>> _expressionCache = new();
        // Memoizes ContainsParameter results by Expression reference to avoid repeated subtree walks
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Expression, System.Runtime.CompilerServices.StrongBox<bool>> _containsParamCache = new();

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
            // Replace longest parameter names first: names are suffixed with _{count}
            // (GenerateParameterName), so @WHEREName0_5 is a prefix of @WHEREName0_50 and @LIMIT is a
            // prefix of @LIMITxxx. A naive left-to-right Replace would corrupt the rendered SQL by
            // substituting the shorter name inside the longer one. This string is diagnostic only
            // (OnExecute log / InitException commandText), not executed.
            foreach (DbParameter parameter in dbCommand.Parameters
                .Cast<DbParameter>()
                .OrderByDescending(p => p.ParameterName.Length))
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
                    var normalizedBody = Birko.Data.Expressions.ExpressionNormalizer.Normalize(lambdaExpression.Body) ?? lambdaExpression.Body;
                    return ParseExpression(normalizedBody, parameters, withTableName, type);
                }
                else if (expr is ConditionalExpression conditionalExpression)
                {
                    // Value-position ternary (e.g. an Update SET right-hand side): render as CASE WHEN.
                    var test = ParseExpression(conditionalExpression.Test, parameters, withTableName, exprType);
                    var ifTrue = ParseExpression(conditionalExpression.IfTrue, parameters, withTableName, exprType);
                    var ifFalse = ParseExpression(conditionalExpression.IfFalse, parameters, withTableName, exprType);
                    return $"CASE WHEN {test} THEN {ifTrue} ELSE {ifFalse} END";
                }
                else if (expr is BinaryExpression binaryExpression)
                {
                    if (binaryExpression.NodeType == ExpressionType.Coalesce)
                    {
                        // Value-position null-coalescing (a ?? b) → COALESCE(a, b).
                        var coalesceLeft = ParseExpression(binaryExpression.Left, parameters, withTableName, exprType);
                        var coalesceRight = ParseExpression(binaryExpression.Right, parameters, withTableName, exprType);
                        return $"COALESCE({coalesceLeft}, {coalesceRight})";
                    }
                    if (binaryExpression.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
                        && TryGetNullComparisonOperand(binaryExpression, out var nonNullOperand))
                    {
                        // `x IS NULL` / `x IS NOT NULL` — a null constant compared with `=`/`<>` is always
                        // UNKNOWN in SQL, so it must become IS [NOT] NULL (matters for CASE WHEN tests above).
                        var operand = ParseExpression(nonNullOperand!, parameters, withTableName, exprType);
                        return binaryExpression.NodeType == ExpressionType.Equal
                            ? $"({operand} IS NULL)"
                            : $"({operand} IS NOT NULL)";
                    }
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
                    else if (callExpression.Method.Name is "ToLower" or "ToLowerInvariant")
                    {
                        var inner = ParseExpression(callExpression.Object!, parameters, withTableName, exprType);
                        return $"LOWER({inner})";
                    }
                    else if (callExpression.Method.Name is "ToUpper" or "ToUpperInvariant")
                    {
                        var inner = ParseExpression(callExpression.Object!, parameters, withTableName, exprType);
                        return $"UPPER({inner})";
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
                    var isParamAccess = memberExpression.Expression?.NodeType == ExpressionType.Parameter
                        || memberExpression.Expression?.NodeType == ExpressionType.TypeAs
                        || (memberExpression.Expression is UnaryExpression convExpr
                            && convExpr.NodeType == ExpressionType.Convert
                            && convExpr.Operand.NodeType == ExpressionType.Parameter);
                    if (
                        exprType != null
                        && memberExpression.Member.ReflectedType?.IsAssignableFrom(exprType) == true
                        && isParamAccess
                    )
                    {
                        name = ResolveColumnName(exprType, memberExpression.Member.Name, withTableName) ?? string.Empty;
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        if (memberExpression.Expression is ConstantExpression
                            || memberExpression.Expression == null)
                        {
                            var key = "@Const" + parameters.Count;
                            var value = EvaluateExpression(memberExpression);
                            parameters.Add(key, value!);
                            return key;
                        }
                        else
                        {
                            return ParseExpression(memberExpression.Expression, parameters, withTableName); // not resending type here
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

        /// <summary>
        /// True when the filter is an <b>explicit</b> "every row" predicate — the caller-facing synonym for
        /// <c>DeleteAll()</c> / <c>UpdateAll(updates)</c> (SH-H002).
        /// </summary>
        /// <remarks>
        /// <para>Deliberately a <b>one-node</b> test, not a catalogue of the shapes that mean everything.
        /// <see cref="Birko.Data.Expressions.ExpressionNormalizer"/> funcletizes every parameter-free subtree
        /// to a constant first, so <c>x =&gt; true</c>, <c>x =&gt; 1 == 1</c> and <c>x =&gt; capturedFlag</c>
        /// all arrive here as the same <see cref="ConstantExpression"/>. Anything else — including
        /// <c>x =&gt; true || x.A == 1</c>, which the parser also reduces to "everything" — is refused by the
        /// connector guard instead, with a message naming the explicit API.</para>
        /// <para>Enumerating the shapes was tried and rejected: the parser has at least four sites that
        /// legitimately reduce to "everything", so a whitelist here would rot the moment a fifth is added, and
        /// its failure mode is a refused destructive operation on working code.</para>
        /// </remarks>
        public static bool IsExplicitAllRows(LambdaExpression? expr)
        {
            if (expr == null)
            {
                return false;
            }

            var body = Birko.Data.Expressions.ExpressionNormalizer.Normalize(expr.Body) ?? expr.Body;
            return body is ConstantExpression constant && constant.Value is bool value && value;
        }

        public static IEnumerable<Conditions.Condition> ParseConditionExpression(Expression? expr = null, Conditions.Condition? parent = null, Type? exprType = null)
        {
            if (expr != null)
            {
                if (expr is LambdaExpression lambdaExpression)
                {
                    var type = lambdaExpression.Parameters?.FirstOrDefault()?.Type;
                    // Canonicalise the predicate once, at the lambda boundary: funcletize parameter-free
                    // subtrees and desugar ternary / ?? into boolean algebra the parser below understands.
                    var body = Birko.Data.Expressions.ExpressionNormalizer.Normalize(lambdaExpression.Body) ?? lambdaExpression.Body;
                    // Handle constant boolean body: _ => true means "no filter" (return empty),
                    // _ => false means "match nothing" (return impossible condition 1=0).
                    if (body is ConstantExpression constBody && constBody.Value is bool boolVal)
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
                    var res = ParseConditionExpression(body, parent, type);
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
                        // Fast-path: resolve literal/closure bools without allocating Condition objects.
                        // Only falls back to full parsing when the expression references a lambda parameter.
                        var leftBool = TryGetLiteralBool(binaryExpression.Left);
                        var rightBool = TryGetLiteralBool(binaryExpression.Right);

                        if (leftBool.HasValue && rightBool.HasValue)
                        {
                            bool r = isAnd ? (leftBool.Value && rightBool.Value) : (leftBool.Value || rightBool.Value);
                            return r ? Array.Empty<Conditions.Condition>() : new[] { MakeFalseCondition(parent) };
                        }

                        Conditions.Condition? leftCond = null, rightCond = null;
                        bool leftIsConst = leftBool.HasValue, leftVal = leftBool ?? false;
                        bool rightIsConst = rightBool.HasValue, rightVal = rightBool ?? false;

                        if (!leftIsConst)
                        {
                            leftCond = new Conditions.Condition(null, null);
                            ParseConditionExpression(binaryExpression.Left, leftCond, exprType);
                            leftIsConst = IsConstantBoolCondition(leftCond, out leftVal);
                        }
                        if (!rightIsConst)
                        {
                            rightCond = new Conditions.Condition(null, null);
                            ParseConditionExpression(binaryExpression.Right, rightCond, exprType);
                            rightIsConst = IsConstantBoolCondition(rightCond, out rightVal);
                        }

                        if (leftIsConst && rightIsConst)
                        {
                            bool r = isAnd ? (leftVal && rightVal) : (leftVal || rightVal);
                            return r ? Array.Empty<Conditions.Condition>() : new[] { MakeFalseCondition(parent) };
                        }
                        if (leftIsConst)
                        {
                            if (isAnd && leftVal) return ReturnSingleSubCondition(parent, rightCond!, isOR);
                            if (isAnd && !leftVal) return new[] { MakeFalseCondition(parent) };
                            if (isOR && leftVal) return Array.Empty<Conditions.Condition>();
                            if (isOR && !leftVal) return ReturnSingleSubCondition(parent, rightCond!, isOR);
                        }
                        if (rightIsConst)
                        {
                            if (isAnd && rightVal) return ReturnSingleSubCondition(parent, leftCond!, isOR);
                            if (isAnd && !rightVal) return new[] { MakeFalseCondition(parent) };
                            if (isOR && rightVal) return Array.Empty<Conditions.Condition>();
                            if (isOR && !rightVal) return ReturnSingleSubCondition(parent, leftCond!, isOR);
                        }

                        if (parent != null)
                        {
                            // Transfer the OR/AND flag to the parent so the SQL generator
                            // knows to join subconditions with OR instead of AND.
                            parent.IsOr = isOR;
                            var subs = parent.SubConditions as List<Condition> ?? new List<Condition>(parent.SubConditions ?? []);
                            subs.Add(leftCond!);
                            subs.Add(rightCond!);
                            parent.SubConditions = subs;
                            return new[] { parent };
                        }
                        else
                        {
                            return new[] { new Conditions.Condition(null, null)
                            {
                                IsOr = isOR,
                                Type = conditionType,
                                IsNot = isNot,
                                SubConditions = new List<Condition> { leftCond!, rightCond! },
                            }};
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

                        // Value-expression operand — column arithmetic (x.A + x.B > 5, x.Price * 2 >= 10),
                        // null-coalescing ((x.Score ?? 0) > 5) or a value-position ternary
                        // ((x.Vip ? x.Premium : x.Score) > 100, i.e. CASE in WHERE): render the value side(s)
                        // to a raw SQL fragment and compare. Without this these nodes fall through the
                        // comparison switch and are mis-parsed. (Boolean-typed ?:/?? were already desugared
                        // to AND/OR by the normalizer and never reach here.)
                        var valLeft = UnwrapConvert(binaryExpression.Left);
                        var valRight = UnwrapConvert(binaryExpression.Right);
                        if (IsValueExpressionOperand(valLeft) || IsValueExpressionOperand(valRight))
                        {
                            var valueCondition = BuildValueComparison(valLeft, valRight, conditionType, isNot, isOR, exprType);
                            if (parent != null)
                            {
                                var valueSubs = parent.SubConditions as List<Condition> ?? new List<Condition>(parent.SubConditions ?? []);
                                valueSubs.Add(valueCondition);
                                parent.SubConditions = valueSubs;
                                return new[] { parent };
                            }
                            return new[] { valueCondition };
                        }

                        // `x.Col.Date <op> <value>` — rewrite to a half-open range on the RAW column
                        // instead of comparing DATE(col) against a bound DateTime. See
                        // TryBuildDateTruncatedComparison for why the DATE() form silently matches
                        // nothing. Only fires when exactly one side is a column `.Date` and the other
                        // evaluates to a constant; column-vs-column keeps the old DATE() path.
                        var dateRange = TryBuildDateTruncatedComparison(
                            binaryExpression.Left, binaryExpression.Right, expr.NodeType, exprType);
                        if (dateRange != null)
                        {
                            if (parent != null)
                            {
                                // Nest it rather than merging into the parent: the `!=` arm carries its
                                // own IsOr=true, and ReturnSingleSubCondition would overwrite that with
                                // the enclosing node's flag — turning `col < d OR col >= d+1` into an
                                // unsatisfiable AND that matches zero rows. Same shape as the
                                // value-comparison branch above.
                                var dateSubs = parent.SubConditions as List<Condition>
                                    ?? new List<Condition>(parent.SubConditions ?? []);
                                dateSubs.Add(dateRange);
                                parent.SubConditions = dateSubs;
                                return new[] { parent };
                            }
                            return new[] { dateRange };
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
                    switch (methodExpression.Method.Name)
                    {
                        case "StartsWith":
                            condition.Type = ConditionType.StartsWith;
                            break;
                        case "EndsWith":
                            condition.Type = ConditionType.EndsWith;
                            break;
                        case "Contains":
                            condition.Type = methodExpression.Method.DeclaringType?.Name == "String"
                                ? ConditionType.Like
                                : ConditionType.In;
                            break;
                        case "ToLower":
                        case "ToLowerInvariant":
                            {
                                // Unwrap ToLower: recurse into the Object, then wrap column name with LOWER()
                                if (methodExpression.Object != null)
                                {
                                    var inner = new Conditions.Condition(null, null);
                                    ParseConditionExpression(methodExpression.Object, inner, exprType);
                                    if (!string.IsNullOrEmpty(inner.Name))
                                    {
                                        condition.Name = $"LOWER({inner.Name})";
                                    }
                                }
                                return new[] { condition };
                            }
                        case "ToUpper":
                        case "ToUpperInvariant":
                            {
                                if (methodExpression.Object != null)
                                {
                                    var inner = new Conditions.Condition(null, null);
                                    ParseConditionExpression(methodExpression.Object, inner, exprType);
                                    if (!string.IsNullOrEmpty(inner.Name))
                                    {
                                        condition.Name = $"UPPER({inner.Name})";
                                    }
                                }
                                return new[] { condition };
                            }
                    }
                    if (methodExpression.Arguments != null && methodExpression.Arguments.Any())
                    {
                        // For the string pattern methods (Contains/StartsWith/EndsWith on
                        // System.String) only the FIRST argument is the search pattern. The
                        // culture-aware overloads carry extra arguments — a StringComparison,
                        // a bool ignoreCase, a CultureInfo — that map to no SQL operand. Feeding
                        // them into the loop overwrote the pattern with the enum/flag value, so
                        // e.g. Title.Contains(query, StringComparison.OrdinalIgnoreCase) produced
                        // `Title LIKE '%5%'`. Case-insensitivity is delegated to the column's DB
                        // collation (SQLite LIKE is already case-insensitive for ASCII); the
                        // comparison argument is intentionally ignored when building the pattern.
                        var methodName = methodExpression.Method.Name;
                        var isStringPatternMethod = methodName == "StartsWith"
                            || methodName == "EndsWith"
                            || (methodName == "Contains" && methodExpression.Method.DeclaringType?.Name == "String");
                        if (isStringPatternMethod)
                        {
                            ParseConditionExpression(methodExpression.Arguments[0], condition, exprType);
                        }
                        else
                        {
                            foreach (var arg in methodExpression.Arguments)
                            {
                                // Same class of problem as the string-pattern arguments above: an
                                // overload-disambiguating argument that maps to no SQL operand must not
                                // be fed to the parser. On .NET 9+ an ARRAY `set.Contains(x.Col)` binds
                                // to MemoryExtensions.Contains(ReadOnlySpan<T>, T, IEqualityComparer<T>?)
                                // whenever T is not IEquatable<T> — true for every enum and nullable
                                // enum. That trailing `null` comparer took the constant-null path and
                                // flipped the whole condition to IS NULL, so `statuses.Contains(x.Status)`
                                // matched rows with a NULL column — none, silently. Guid/int/string sets
                                // ARE IEquatable and bind the 2-argument overload, which is why the
                                // canonical N+1 batch pattern never exposed it. Measured in a consumer
                                // (Symbio TASK-249/TASK-254): 0 rows against 21 matching.
                                if (IsNonOperandArgument(arg))
                                {
                                    continue;
                                }
                                ParseConditionExpression(arg, condition, exprType);
                            }
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
                    condition.Name = ResolveColumnName(exprType, bareBoolMember.Member.Name, true) ?? bareBoolMember.Member.Name;
                    condition.Type = ConditionType.Equal;
                    condition.Values = new object[] { true };
                    return new[] { condition };
                }

                if (parent != null)
                {
                    if (expr is ConstantExpression || expr is MethodCallExpression)
                    {
                        IEnumerable<object>? vals = InvokeExpression(expr);
                        var materialized = vals?.Where(x => x != null).ToArray();
                        if (materialized?.Length > 0)
                        {
                            parent.Values = materialized;
                        }
                        else if (parent.Type == ConditionType.In)
                        {
                            // An empty (or all-null) collection in an IN predicate means "matches nothing" —
                            // NOT "the column is null". Degrading to IsNull here returned rows with a NULL
                            // column, which is a different, wrong answer. Keep the condition an In with no
                            // values; InConditionStrategy renders that as the always-false / always-true
                            // constant (an empty NOT IN matches everything). Note `Col IN (NULL)` is also
                            // never true in SQL, so collapsing an all-null list to "matches nothing" is
                            // faithful rather than a shortcut.
                            parent.Values = System.Array.Empty<object>();
                        }
                        else
                        {
                            // Not an IN: a null/empty constant is a genuine `= null` → IS NULL.
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
                            // A closure variable that evaluates to null must become IS NULL, exactly as a
                            // literal `== null` does (see the ConstantExpression branch above). Otherwise
                            // `x.Col == nullableVar` (var == null) emits `Col = NULL`, which is always
                            // UNKNOWN in SQL → zero rows. For `!=`, IsNot is already set, so this yields
                            // IS NOT NULL — symmetric with the literal-null path.
                            if (value == null)
                            {
                                parent.Type = ConditionType.IsNull;
                                return new[] { parent };
                            }
                            parent.Values = (value is not string && value is IEnumerable enumerable)
                                ? (enumerable as object[] ?? enumerable.Cast<object>().ToArray())
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
                            name = ResolveColumnName(exprType, memberExpression.Member.Name, true) ?? string.Empty;
                        }
                        if (string.IsNullOrEmpty(name))
                        {
                            if ((memberExpression.Expression is ConstantExpression
                                || (memberExpression.Expression != null && !ContainsParameter(memberExpression))))
                            {
                                // Closure field or nested member access — evaluate as constant
                                var value = EvaluateExpression(memberExpression);
                                parent.Values = (value != null && value is not string && value is IEnumerable enumerable)
                                    ? (enumerable as object[] ?? enumerable.Cast<object>().ToArray())
                                    : new[] { value };
                            }
                            else
                            {
                                IEnumerable<object>? vals = InvokeExpression(expr);
                                var materialized = vals?.Where(x => x != null).ToArray();
                                if (materialized?.Length > 0)
                                {
                                    parent.Values = materialized;
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
        /// True for method-call arguments that carry no SQL operand — equality/ordering comparers and
        /// culture/comparison selectors. They exist only to pin down a CLR overload; handing one to the
        /// condition parser corrupts the condition (a null comparer becomes IS NULL, a StringComparison
        /// becomes the LIKE pattern). Note <see cref="IEqualityComparer{T}"/> does not implement the
        /// non-generic <see cref="IEqualityComparer"/>, so both have to be checked.
        /// <para>
        /// A non-null comparer (e.g. <c>set.Contains(x.Name, StringComparer.OrdinalIgnoreCase)</c>) is
        /// skipped too: its comparison SEMANTICS are delegated to the column's DB collation, exactly as
        /// the <c>StringComparison</c> overloads of the string pattern methods already are. Honouring the
        /// operand while ignoring the comparer is the closest correct translation available; the
        /// alternative (parsing it as a value) produced a condition that matched the wrong rows.
        /// </para>
        /// </summary>
        private static bool IsNonOperandArgument(Expression arg)
        {
            var type = arg.Type;
            if (type == typeof(StringComparison) || type == typeof(System.Globalization.CultureInfo))
                return true;
            if (typeof(IEqualityComparer).IsAssignableFrom(type) || typeof(IComparer).IsAssignableFrom(type))
                return true;
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(IEqualityComparer<>) || definition == typeof(IComparer<>))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves a C# property name to its SQL column/field select name using LoadTable + GetFieldByPropertyName,
        /// with fallback to ResolveFieldSelectName delegate (for views).
        /// Returns null if the property cannot be resolved.
        /// </summary>
        private static string? ResolveColumnName(Type exprType, string propertyName, bool withTableName)
        {
            var table = LoadTable(exprType);
            if (table != null)
            {
                var field = table.GetFieldByPropertyName(propertyName);
                if (field != null) return field.GetSelectName(withTableName);
            }
            return ResolveFieldSelectName?.Invoke(exprType, propertyName, withTableName);
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
                using var en = vals.GetEnumerator();
                if (en.MoveNext() && en.Current is bool b && !en.MoveNext())
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

        /// <summary>
        /// Returns the bool value of an expression without allocating a Condition object.
        /// Handles literal constants and parameter-free closure expressions.
        /// Returns null when the expression references a lambda parameter (must go through normal parsing).
        /// </summary>
        private static bool? TryGetLiteralBool(Expression expr)
        {
            if (expr is ConstantExpression ce && ce.Value is bool b) return b;
            if (ContainsParameter(expr)) return null;
            try { return EvaluateExpression(expr) as bool?; }
            catch { return null; }
        }

        /// <summary>
        /// True when <paramref name="expr"/> is a <c>.Date</c> truncation of a DateTime COLUMN
        /// (<c>x.OpenedAt.Date</c>, or <c>x.Nullable.Value.Date</c>), yielding the inner column
        /// expression. False for <c>local.Date</c> — that side has no lambda parameter and is a value.
        /// </summary>
        private static bool TryGetDateTruncatedColumn(Expression expr, out Expression inner)
        {
            inner = null!;
            if (UnwrapConvert(expr) is not MemberExpression m) return false;
            if (m.Member.Name != "Date" || m.Member.DeclaringType != typeof(DateTime)) return false;
            if (m.Expression is not MemberExpression innerMember) return false;
            if (!ContainsParameter(innerMember)) return false;
            inner = innerMember;
            return true;
        }

        /// <summary>
        /// Rewrites <c>x.Col.Date &lt;op&gt; value</c> into a half-open range over the RAW column, e.g.
        /// <c>x.OpenedAt.Date == d</c> becomes <c>OpenedAt &gt;= d AND OpenedAt &lt; d+1day</c>.
        /// Returns null when the shape does not apply (column-vs-column, or neither side a column
        /// <c>.Date</c>), leaving the older <c>DATE(col)</c> rendering in place for those.
        /// <para>
        /// Why this exists (Symbio TASK-355). The previous translation emitted <c>DATE(col) = @p</c> and
        /// bound the right-hand side as a <b>DateTime</b>. On SQLite a DateTime column is stored as the
        /// text <c>yyyy-MM-dd HH:mm:ss.FFFFFFF</c>, so <c>DATE(col)</c> evaluates to the 10-character
        /// <c>yyyy-MM-dd</c> while the parameter serialises to the full <c>yyyy-MM-dd 00:00:00</c> —
        /// <c>'2026-08-07' = '2026-08-07 00:00:00'</c> is <b>false for every row, always</b>. Measured
        /// against the Symbio Testing DB: 0 rows matched where 4 should have. The query runs, returns
        /// 200 and reports zero — the silent-wrong-answer shape, invisible to any test that runs against
        /// an in-memory store because that COMPILES the lambda instead of translating it.
        /// </para>
        /// <para>
        /// The range form fixes three things at once: it is correct (no text-vs-text formatting
        /// mismatch), it is <b>sargable</b> (a function on the column defeats an index), and it is
        /// dialect-agnostic — <c>DATE(x)</c> is not a function in T-SQL at all, so the old form was a
        /// hard syntax error on MSSql, the same works-on-SQLite-only trap as the empty <c>IN ()</c>.
        /// </para>
        /// </summary>
        private static Conditions.Condition? TryBuildDateTruncatedComparison(
            Expression left, Expression right, ExpressionType nodeType, Type? exprType)
        {
            var leftIsCol = TryGetDateTruncatedColumn(left, out var leftInner);
            var rightIsCol = TryGetDateTruncatedColumn(right, out var rightInner);
            if (leftIsCol == rightIsCol) return null;   // both or neither — not this shape

            var columnExpr = leftIsCol ? leftInner : rightInner;
            var valueExpr = leftIsCol ? right : left;
            if (ContainsParameter(valueExpr)) return null;

            // Mirror the operator when the column is on the right (`value == x.Col.Date`).
            var op = nodeType;
            if (!leftIsCol)
            {
                op = op switch
                {
                    ExpressionType.LessThan => ExpressionType.GreaterThan,
                    ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
                    ExpressionType.GreaterThan => ExpressionType.LessThan,
                    ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
                    _ => op,   // Equal / NotEqual are symmetric
                };
            }

            DateTime day;
            try
            {
                if (EvaluateExpression(valueExpr) is not DateTime dt) return null;
                day = dt.Date;
            }
            catch { return null; }
            var next = day.AddDays(1);

            // Resolve the column name through the normal machinery so table-name qualification,
            // nullable unwrapping and [Column] mapping all behave exactly as they do elsewhere.
            var probe = new Conditions.Condition(null, null);
            ParseConditionExpression(columnExpr, probe, exprType);
            if (string.IsNullOrEmpty(probe.Name)) return null;
            var col = probe.Name!;

            Conditions.Condition Leaf(DateTime v, ConditionType t)
                => new Conditions.Condition(col, new object[] { v }, t);

            return op switch
            {
                // date(col) == d  ⟺  d <= col < d+1
                ExpressionType.Equal => new Conditions.Condition(null, null)
                {
                    IsOr = false,
                    SubConditions = new List<Condition>
                    {
                        Leaf(day, ConditionType.GreatherAndEqual),
                        Leaf(next, ConditionType.Less),
                    },
                },
                // date(col) != d  ⟺  col < d OR col >= d+1
                ExpressionType.NotEqual => new Conditions.Condition(null, null)
                {
                    IsOr = true,
                    SubConditions = new List<Condition>
                    {
                        Leaf(day, ConditionType.Less),
                        Leaf(next, ConditionType.GreatherAndEqual),
                    },
                },
                ExpressionType.LessThan => Leaf(day, ConditionType.Less),                    // < d
                ExpressionType.LessThanOrEqual => Leaf(next, ConditionType.Less),            // < d+1
                ExpressionType.GreaterThan => Leaf(next, ConditionType.GreatherAndEqual),    // >= d+1
                ExpressionType.GreaterThanOrEqual => Leaf(day, ConditionType.GreatherAndEqual), // >= d
                _ => null,
            };
        }

        /// <summary>Strips outer Convert / ConvertChecked wrappers (nullable lifting, boxing).</summary>
        private static Expression UnwrapConvert(Expression expr)
            => expr is UnaryExpression u && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                ? UnwrapConvert(u.Operand)
                : expr;

        private static bool IsArithmeticNode(ExpressionType type)
            => type is ExpressionType.Add or ExpressionType.AddChecked
                or ExpressionType.Subtract or ExpressionType.SubtractChecked
                or ExpressionType.Multiply or ExpressionType.MultiplyChecked
                or ExpressionType.Divide or ExpressionType.Modulo;

        private static bool IsNumericType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte
                or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
        }

        /// <summary>True for a numeric arithmetic BinaryExpression (the column-arithmetic trigger).</summary>
        private static bool IsArithmeticOperand(Expression expr)
            => expr is BinaryExpression b && IsArithmeticNode(b.NodeType) && IsNumericType(b.Type);

        /// <summary>
        /// True when a comparison operand is a value-expression that must be rendered to a raw SQL
        /// fragment: numeric arithmetic, null-coalescing, or a (value-position) ternary. Boolean-typed
        /// ternary / coalesce are desugared to AND/OR by the normalizer and never reach here.
        /// </summary>
        private static bool IsValueExpressionOperand(Expression expr)
            => IsArithmeticOperand(expr)
                || (expr is BinaryExpression b && b.NodeType == ExpressionType.Coalesce)
                || expr is ConditionalExpression;

        private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

        private static bool IsNullConstant(Expression expr)
            => expr is ConstantExpression c && c.Value == null;

        private static string? ComparisonSqlOperator(ExpressionType type) => type switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            _ => null,
        };

        private static ConditionType FlipComparison(ConditionType type) => type switch
        {
            ConditionType.Less => ConditionType.Greather,
            ConditionType.Greather => ConditionType.Less,
            ConditionType.LessAndEqual => ConditionType.GreatherAndEqual,
            ConditionType.GreatherAndEqual => ConditionType.LessAndEqual,
            _ => type,
        };

        /// <summary>
        /// Renders a value-expression subtree — column references, Add/Subtract/Multiply/Divide/Modulo
        /// arithmetic, <c>COALESCE</c>, a value-position ternary (<c>CASE WHEN … END</c>), nullable
        /// <c>.Value</c> unwrap, and constants — into a raw SQL fragment such as <c>(Table.A + Table.B)</c>
        /// or <c>CASE WHEN (Table.Vip &lt;&gt; 0) THEN Table.Premium ELSE Table.Score END</c>. Because the
        /// WHERE builder binds parameters only later (via the condition strategies), constants inside a
        /// fragment cannot be parameterised and are instead inlined as portable SQL literals
        /// (see <see cref="InlineConstant"/>). Throws <see cref="NotSupportedException"/> for anything it
        /// cannot faithfully translate rather than silently dropping it.
        /// </summary>
        private static string RenderValueFragment(Expression expr, Type? exprType)
        {
            switch (expr)
            {
                case UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
                    return RenderValueFragment(u.Operand, exprType);
                case ConditionalExpression cond:
                    return $"CASE WHEN {RenderBoolFragment(cond.Test, exprType)} "
                        + $"THEN {RenderValueFragment(cond.IfTrue, exprType)} "
                        + $"ELSE {RenderValueFragment(cond.IfFalse, exprType)} END";
                case BinaryExpression b when IsArithmeticNode(b.NodeType):
                {
                    var l = RenderValueFragment(b.Left, exprType);
                    var r = RenderValueFragment(b.Right, exprType);
                    var op = b.NodeType switch
                    {
                        ExpressionType.Add or ExpressionType.AddChecked => "+",
                        ExpressionType.Subtract or ExpressionType.SubtractChecked => "-",
                        ExpressionType.Multiply or ExpressionType.MultiplyChecked => "*",
                        ExpressionType.Divide => "/",
                        ExpressionType.Modulo => "%",
                        _ => throw new NotSupportedException($"Unsupported arithmetic operator {b.NodeType}"),
                    };
                    return $"({l} {op} {r})";
                }
                case BinaryExpression b when b.NodeType == ExpressionType.Coalesce:
                    return $"COALESCE({RenderValueFragment(b.Left, exprType)}, {RenderValueFragment(b.Right, exprType)})";
                case MemberExpression m when TryResolveParameterColumn(m, exprType, out var column):
                    return column;
                case MemberExpression valueMember when valueMember.Member.Name == "Value"
                    && valueMember.Member.ReflectedType != null
                    && Nullable.GetUnderlyingType(valueMember.Member.ReflectedType) != null
                    && valueMember.Expression is MemberExpression innerNullable:
                    return RenderValueFragment(innerNullable, exprType);
                default:
                    if (!ContainsParameter(expr))
                        return InlineConstant(EvaluateExpression(expr));
                    throw new NotSupportedException(
                        $"Cannot translate operand '{expr}' inside a value-expression filter predicate to SQL.");
            }
        }

        /// <summary>
        /// Renders a boolean sub-expression — the test of a value-position ternary, i.e. a WHEN clause
        /// of a CASE emitted in a WHERE — into a raw SQL predicate fragment with any constants inlined.
        /// Supports comparisons, IS [NOT] NULL, AND/OR/NOT and bare boolean columns. Throws
        /// <see cref="NotSupportedException"/> for constructs it cannot inline (e.g. string LIKE methods),
        /// rather than silently dropping them.
        /// </summary>
        private static string RenderBoolFragment(Expression expr, Type? exprType)
        {
            expr = UnwrapConvert(expr);
            switch (expr)
            {
                case UnaryExpression u when u.NodeType == ExpressionType.Not:
                    return $"(NOT {RenderBoolFragment(u.Operand, exprType)})";
                case BinaryExpression b when b.NodeType is ExpressionType.AndAlso or ExpressionType.And:
                    return $"({RenderBoolFragment(b.Left, exprType)} AND {RenderBoolFragment(b.Right, exprType)})";
                case BinaryExpression b when b.NodeType is ExpressionType.OrElse or ExpressionType.Or:
                    return $"({RenderBoolFragment(b.Left, exprType)} OR {RenderBoolFragment(b.Right, exprType)})";
                case BinaryExpression b when b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
                        && (IsNullConstant(b.Left) || IsNullConstant(b.Right)):
                {
                    var operand = IsNullConstant(b.Right) ? b.Left : b.Right;
                    var frag = RenderValueFragment(operand, exprType);
                    return b.NodeType == ExpressionType.Equal ? $"({frag} IS NULL)" : $"({frag} IS NOT NULL)";
                }
                case BinaryExpression b when ComparisonSqlOperator(b.NodeType) is string op:
                    return $"({RenderValueFragment(b.Left, exprType)} {op} {RenderValueFragment(b.Right, exprType)})";
                case MemberExpression m when TryResolveParameterColumn(m, exprType, out var column)
                        && UnwrapNullable(m.Type) == typeof(bool):
                    return $"({column} <> 0)";
                default:
                    if (!ContainsParameter(expr) && EvaluateExpression(expr) is bool constBool)
                        return constBool ? "(1=1)" : "(1=0)";
                    throw new NotSupportedException(
                        $"Cannot translate boolean sub-expression '{expr}' inside a CASE/WHERE predicate to SQL.");
            }
        }

        /// <summary>
        /// Inlines a constant into a portable SQL literal for use inside a raw fragment (CASE/COALESCE/
        /// arithmetic in a WHERE, where parameters cannot be bound): NULL, numeric (invariant), bool → 1/0,
        /// enum → integer, string → single-quoted with <c>'</c> escaped. Throws for types whose literal form
        /// is not portable (DateTime, Guid, byte[]) so they fail loud instead of rendering wrong SQL.
        /// </summary>
        private static string InlineConstant(object? value)
        {
            switch (value)
            {
                case null:
                    return "NULL";
                case bool b:
                    return b ? "1" : "0";
                case string s:
                    return "'" + s.Replace("'", "''") + "'";
                case Enum e:
                    return Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (IsNumericType(value.GetType()))
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
            throw new NotSupportedException(
                $"Cannot inline a constant of type {value.GetType()} into a CASE/COALESCE/arithmetic SQL fragment.");
        }

        /// <summary>Resolves a direct parameter member access (or Convert(param).Member) to its column select name.</summary>
        private static bool TryResolveParameterColumn(MemberExpression member, Type? exprType, out string column)
        {
            column = string.Empty;
            if (exprType == null) return false;
            var isParamAccess = member.Expression?.NodeType == ExpressionType.Parameter
                || (member.Expression is UnaryExpression c
                    && c.NodeType == ExpressionType.Convert
                    && c.Operand.NodeType == ExpressionType.Parameter);
            if (!isParamAccess) return false;
            if (member.Member.ReflectedType?.IsAssignableFrom(exprType) != true) return false;
            var name = ResolveColumnName(exprType, member.Member.Name, true);
            if (string.IsNullOrEmpty(name)) return false;
            column = name;
            return true;
        }

        /// <summary>
        /// Builds a <see cref="Condition"/> for a comparison in which at least one side is a value-expression
        /// (arithmetic / COALESCE / CASE). The parameter/column side becomes the condition Name (raw SQL
        /// fragment); a constant side becomes a bound value; when both sides reference the parameter the
        /// right side is emitted verbatim (IsField). Flips the operator when the value is on the left.
        /// </summary>
        private static Condition BuildValueComparison(
            Expression left, Expression right, ConditionType type, bool isNot, bool isOR, Type? exprType)
        {
            var leftHasParam = ContainsParameter(left);
            var rightHasParam = ContainsParameter(right);

            if (leftHasParam && !rightHasParam)
            {
                return MakeValueCondition(RenderValueFragment(left, exprType), EvaluateExpression(right), type, isNot, isOR);
            }
            if (!leftHasParam && rightHasParam)
            {
                // Value on the left, column expression on the right — flip the operator to keep the column on the left.
                return MakeValueCondition(RenderValueFragment(right, exprType), EvaluateExpression(left), FlipComparison(type), isNot, isOR);
            }
            // Both sides reference the parameter → column expression compared to column expression.
            var leftFragment = RenderValueFragment(left, exprType);
            var rightFragment = RenderValueFragment(right, exprType);
            return new Condition(leftFragment, new object[] { rightFragment }, type, isField: true, isNot: isNot, isOr: isOR);
        }

        private static Condition MakeValueCondition(string name, object? value, ConditionType type, bool isNot, bool isOR)
        {
            // A null-valued equality must become IS NULL (see the literal `== null` path); any other
            // comparison keeps the (null) value bound so SQL yields UNKNOWN → no rows, matching C#.
            if (value == null && type == ConditionType.Equal)
                return new Condition(name, null, ConditionType.IsNull, false, isNot, isOR);
            return new Condition(name, new[] { value! }, type, false, isNot, isOR);
        }

        /// <summary>True when one operand of an equality is the literal <c>null</c>; yields the other operand.</summary>
        private static bool TryGetNullComparisonOperand(BinaryExpression binary, out Expression? nonNullOperand)
        {
            nonNullOperand = null;
            if (IsNullConstant(binary.Right)) { nonNullOperand = binary.Left; return true; }
            if (IsNullConstant(binary.Left)) { nonNullOperand = binary.Right; return true; }
            return false;
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
                var func = _expressionCache.GetOrAdd(expr, static e =>
                {
                    // Box value types (Guid, int, bool, enum, DateTime) so the compiled
                    // delegate is always Func<object>, not Func<T>.
                    var body = e.Type.IsValueType
                        ? Expression.Convert(e, typeof(object))
                        : e;
                    var lambda = Expression.Lambda<Func<object>>(body);
                    return lambda.Compile();
                });
                return func();
            }

            return null;
        }

        /// <summary>
        /// Checks whether the expression tree contains any ParameterExpression (lambda parameters).
        /// Results are memoized by expression reference so repeated calls on the same node are O(1).
        /// </summary>
        private static bool ContainsParameter(Expression expr)
        {
            return _containsParamCache
                .GetOrAdd(expr, static e => new System.Runtime.CompilerServices.StrongBox<bool>(ContainsParameterCore(e)))
                .Value;
        }

        private static bool ContainsParameterCore(Expression expr)
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
