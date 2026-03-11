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
using Settings = Birko.Data.Stores.Settings;

namespace Birko.Data.SQL
{
    public static partial class DataBase
    {
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, AbstractConnector>> _connectors = new();
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, AbstractAsyncConnector>> _asyncConnectors = new();
        private static readonly ConcurrentDictionary<string, Func<object>> _expressionCache = new();
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
                return (AbstractConnector)Activator.CreateInstance(typeof(T), new object[] { settings });
            });
        }

        public static AbstractAsyncConnector GetAsyncConnector<T>(Settings settings) where T : AbstractAsyncConnector
        {
            var connectorType = typeof(T);
            var settingsId = settings.GetId();

            var connectorDict = _asyncConnectors.GetOrAdd(connectorType, _ => new ConcurrentDictionary<string, AbstractAsyncConnector>());
            return connectorDict.GetOrAdd(settingsId, id =>
            {
                return (AbstractAsyncConnector)Activator.CreateInstance(typeof(T), new object[] { settings });
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
                string value = isString
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

        public static string ParseExpression(Expression expr, IDictionary<string, object> parameters, bool withTableName = false, Type exprType = null)
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
                        result.AppendFormat("REPLACE({0}", ParseExpression(callExpression.Object, parameters, withTableName, exprType));
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
                        parameters.Add(key, value);
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
                        && memberExpression.Member.ReflectedType.IsAssignableFrom(exprType)
                        && (memberExpression.Expression.NodeType == ExpressionType.Parameter || memberExpression.Expression.NodeType == ExpressionType.TypeAs)
                    )
                    {
                        var table = LoadTable(exprType);
                        if (table != null)
                        {
                            var field = table.GetFieldByPropertyName(memberExpression.Member.Name);
                            if (field != null)
                            {
                                name = field?.GetSelectName(withTableName);
                            }
                        }
                        else
                        {
                            var view = LoadView(exprType);
                            if (view != null)
                            {
                                var field = view.GetTableFields().FirstOrDefault(x => x.Property.Name == memberExpression.Member.Name);
                                if (field != null)
                                {
                                    name = field?.GetSelectName(withTableName);
                                }
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        if (memberExpression.Expression is ConstantExpression constantExpression)
                        {
                            Type type = constantExpression.Value.GetType();
                            var value = type.InvokeMember(memberExpression.Member.Name, BindingFlags.GetField, null, constantExpression.Value, null);
                            var key = "@Constat" + parameters.Count;
                            parameters.Add(key, value);
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
                            parameters.Add(key, value);
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
                    parameters.Add(key, constantExpression.Value);
                    return key;
                }
            }
            return null;
        }

        public static IEnumerable<Conditions.Condition> ParseConditionExpression(Expression? expr = null, Conditions.Condition parent = null, Type exprType = null)
        {
            if (expr != null)
            {
                if (expr is LambdaExpression lambdaExpression)
                {
                    var type = lambdaExpression.Parameters?.FirstOrDefault()?.Type;
                    var res = ParseConditionExpression(lambdaExpression.Body, parent, type);
                    return res;
                }
                else if (expr is UnaryExpression unaryExpression)
                {
                    if (unaryExpression.NodeType == ExpressionType.Convert)
                    {
                        return ParseConditionExpression(unaryExpression.Operand, parent, exprType);
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
                        if (parent != null)
                        {
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
                        if (methodExpression.Method.DeclaringType.Name == "String")
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
                if (parent != null)
                {
                    if (expr is ConstantExpression || expr is MethodCallExpression)
                    {
                        IEnumerable<object> vals = InvokeExpression(expr);
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
                        string name = string.Empty;
                        if (
                            exprType != null
                            && memberExpression.Expression.NodeType == ExpressionType.MemberAccess
                            && memberExpression.Member.ReflectedType != null
                            && Nullable.GetUnderlyingType(memberExpression.Member.ReflectedType) != null
                        )
                        {
                            var member = new Condition(null, null);
                            ParseConditionExpression(memberExpression.Expression, member, exprType);
                            name = member.Name;
                        }
                        if (
                            exprType != null
                            && memberExpression.Member.ReflectedType != null
                            && memberExpression.Member.ReflectedType.IsAssignableFrom(exprType)
                            && memberExpression.Expression.NodeType == ExpressionType.Parameter
                        )
                        {
                            var table = LoadTable(exprType);
                            if (table != null)
                            {
                                var field = table.GetFieldByPropertyName(memberExpression.Member.Name);
                                if (field != null)
                                {
                                    name = field?.GetSelectName(true);
                                }
                            }
                            else
                            {
                                var view = LoadView(exprType);
                                if (view != null)
                                {
                                    var field = view.GetTableFields().FirstOrDefault(x => x.Property.Name == memberExpression.Member.Name);
                                    if (field != null)
                                    {
                                        name = field?.GetSelectName(true);
                                    }
                                }

                            }
                        }
                        if (string.IsNullOrEmpty(name))
                        {
                            if (memberExpression.Expression is ConstantExpression constantExpression)
                            {
                                Type type = constantExpression.Value.GetType();
                                var value = type.InvokeMember(memberExpression.Member.Name, BindingFlags.GetField | BindingFlags.GetProperty, null, constantExpression.Value, null);
                                parent.Values = (!(value is string) && (value is IEnumerable)) ? (IEnumerable)value : new[] { value };
                            }
                            //else if (memberExpression.Expression != null)
                            //{
                            //    ParseConditionExpression(memberExpression.Expression, parent); // not resending type here
                            //}
                            else
                            {
                                IEnumerable<object> vals = InvokeExpression(expr);
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

                if (m.Member is FieldInfo fi)
                    return fi.GetValue(container);
                if (m.Member is PropertyInfo pi)
                    return pi.GetValue(container);
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

            // Use expression string as cache key (Expression doesn't implement GetHashCode/Equals)
            var cacheKey = expr.ToString();
            var func = _expressionCache.GetOrAdd(cacheKey, _ =>
            {
                var lambda = Expression.Lambda(expr);
                return (Func<object>)lambda.Compile();
            });

            return func();
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
                        vals.Add(field.GetValue(value));
                    }
                }
            }

            return vals?.Where(x => x != null);
        }
    }
}
