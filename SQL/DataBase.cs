using Birko.Data.SQL.Conditions;
using Birko.Data.SQL.Connectors;
using Birko.Data.SQL.Fields;
using Birko.Data.Models;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Birko.Data.SQL
{
    public static partial class DataBase
    {
        private static Dictionary<Type, Dictionary<string, AbstractConnector>> _connectors = null;

        public static AbstractConnector GetConnector<T>(Stores.Settings settings) where T: AbstractConnector
        {
            if (_connectors == null)
            {
                _connectors = new Dictionary<Type, Dictionary<string, AbstractConnector>>();
            }
            if (!_connectors.ContainsKey(typeof(T)))
            {
                _connectors.Add(typeof(T), new Dictionary<string, AbstractConnector>());
            }
            if (!_connectors[typeof(T)].ContainsKey(settings.GetId()))
            {
                _connectors[typeof(T)].Add(settings.GetId(), (AbstractConnector)Activator.CreateInstance(typeof(T), new object[] { settings }));
            }
            return _connectors[typeof(T)][settings.GetId()];
        }

        public static string GetGeneratedQuery(DbCommand dbCommand)
        {
            var query = dbCommand.CommandText;
            foreach (DbParameter parameter in dbCommand.Parameters)
            {
                query = query.Replace(parameter.ParameterName, parameter.Value.ToString());
            }

            return query;
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
                    var key = "@Constat" + parameters.Count;
                    var f = Expression.Lambda(callExpression).Compile();
                    var value = f.DynamicInvoke();
                    parameters.Add(key, value);
                    return key;
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

        public static IEnumerable<Conditions.Condition> ParseConditionExpression(Expression expr, Conditions.Condition parent = null, Type exprType = null)
        {
            if (expr != null)
            {
                if (expr is LambdaExpression lambdaExpression)
                {
                    var type = lambdaExpression.Parameters?.FirstOrDefault()?.Type;
                    return ParseConditionExpression(lambdaExpression.Body, parent, type);
                }
                else if (expr is UnaryExpression unaryExpression)
                {
                    if (unaryExpression.NodeType == ExpressionType.Convert)
                    {
                        return ParseConditionExpression(unaryExpression.Operand, parent, exprType);
                    }
                    if (parent != null)
                    {
                        return new [] { parent };
                    }
                }
                var basecondition = new Conditions.Condition(null, null);
                if (expr is BinaryExpression binaryExpression)
                {
                    switch (expr.NodeType)
                    {
                        case ExpressionType.And:
                        case ExpressionType.AndAlso:
                            basecondition.IsOr = false;
                            break;
                        case ExpressionType.Or:
                        case ExpressionType.OrElse:
                            basecondition.IsOr = true;
                            break;
                        case ExpressionType.Equal:
                            basecondition.Type = ConditionType.Equal;
                            break;
                        case ExpressionType.NotEqual:
                            basecondition.Type = ConditionType.Equal;
                            basecondition.IsNot = true;
                            break;
                        case ExpressionType.LessThan:
                            basecondition.Type = ConditionType.Less;
                            break;
                        case ExpressionType.LessThanOrEqual:
                            basecondition.Type = ConditionType.LessAndEqual;
                            break;
                        case ExpressionType.GreaterThan:
                            basecondition.Type = ConditionType.Greather;
                            break;
                        case ExpressionType.GreaterThanOrEqual:
                            basecondition.Type = ConditionType.GreatherAndEqual;
                            break;
                    }
                    var left = ParseConditionExpression(binaryExpression.Left, basecondition, exprType);
                    var right = ParseConditionExpression(binaryExpression.Right, basecondition, exprType);
                    if (parent != null)
                    {
                        parent.SubConditions = (parent.SubConditions ?? (new Conditions.Condition[0])).Union(new[] { basecondition }).AsEnumerable();
                        return new[] { parent };
                    }
                    else
                    {
                        return new[] { basecondition };
                    }
                }
                else if (expr is MethodCallExpression methodExpression)
                {
                    var condition = parent ?? basecondition;
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
                        List<object> vals = InvokeExpression(expr);
                        if (vals != null && vals.Any())
                        {
                            parent.Values = vals.ToArray();
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
                                var value = type.InvokeMember(memberExpression.Member.Name, BindingFlags.GetField, null, constantExpression.Value, null);
                                parent.Values = new[] { value };
                            }
                            //else if (memberExpression.Expression != null)
                            //{
                            //    ParseConditionExpression(memberExpression.Expression, parent); // not resending type here
                            //}
                            else
                            {
                                List<object> vals = InvokeExpression(expr);
                                if (vals != null && vals.Any())
                                {
                                    parent.Values = vals.ToArray();
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
            return new Conditions.Condition[0];
        }

        private static List<object> InvokeExpression(Expression expr)
        {
            List<object> vals = new List<object>();
            object value = null;
            if (expr is ConstantExpression constantExpression)
            {
                value = constantExpression.Value;
            }
            else
            {
                var f = Expression.Lambda(expr).Compile();
                value = f.DynamicInvoke();
            }
            if (value != null)
            {

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
            }

            return vals;
        }
    }
}
