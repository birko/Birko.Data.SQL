using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connector
{
    public abstract partial class AbstractConnector
    {
        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            return CreateSelectCommand(command, tableNames, fields, null, conditions, null, orderFields);
        }

        public virtual DbCommand CreateSelectCommand(DbCommand command, Table.View view, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            var leftTables = view.Join?.Select(x => x.Left).Distinct().Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (leftTables != null)
            {
                foreach (var tableName in view.Join.Select(x => x.Right).Distinct().Where(x => !string.IsNullOrEmpty(x)))
                {
                    leftTables.Remove(tableName);
                }
            }
            return CreateSelectCommand(command, leftTables ?? view.Tables.Select(x => x.Name), view.GetSelectFields(), view.Join, conditions, view.HasAggregateFields() ? view.GetSelectFields(true) : null, orderFields);
        }


        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Condition.Join> joinconditions = null, IEnumerable<Condition.Condition> conditions = null, IDictionary<int, string> groupFields = null, IDictionary<string, bool> orderFields = null)
        {
            command.CommandText = "SELECT " + string.Join(", ", fields.Values) + " FROM ";

            Dictionary<string, List<Condition.Join>> joins = new Dictionary<string, List<Condition.Join>>();
            if (joinconditions != null && joinconditions.Any())
            {
                string prevleft = null;
                string prevright = null;
                foreach (var join in joinconditions)
                {
                    if (!string.IsNullOrEmpty(prevleft) && !string.IsNullOrEmpty(prevright) && !joins.ContainsKey(join.Left) && prevright == join.Left && joins.ContainsKey(prevleft))
                    {
                        joins[prevleft].Add(join);
                    }
                    else
                    {
                        if (!joins.ContainsKey(join.Left))
                        {
                            joins.Add(join.Left, new List<Condition.Join>());
                        }
                        joins[join.Left].Add(join);
                        prevleft = join.Left;
                    }
                    prevright = join.Right;
                }
            }

            int i = 0;
            foreach (var table in tableNames.Distinct())
            {
                if (i > 0)
                {
                    command.CommandText += ", ";
                }
                command.CommandText += table;
                if (joins != null && joins.ContainsKey(table))
                {
                    var joingroups = joins[table].GroupBy(x => new { x.Right, x.JoinType }).ToDictionary(x => x.Key, x => x.SelectMany(y => y.Conditions).Where(z => z != null));
                    foreach (var joingroup in joingroups.Where(x=>x.Value.Any()))
                    {
                        switch (joingroup.Key.JoinType)
                        {
                            case Condition.JoinType.Inner:
                                command.CommandText += " INNER JOIN ";
                                break;
                            case Condition.JoinType.LeftOuter:
                                command.CommandText += " LEFT OUTER JOIN ";
                                break;
                            case Condition.JoinType.Cross:
                            default:
                                command.CommandText += " CROSS JOIN ";
                                break;
                        }
                        command.CommandText += joingroup.Key.Right;
                        if (joingroup.Key.JoinType != Condition.JoinType.Cross && joingroup.Value != null && joingroup.Value.Any())
                        {
                            command.CommandText += " ON (";
                            command.CommandText += ConditionDefinition(joingroup.Value, command);
                            command.CommandText += ")";
                        }
                    }
                }
            }
            AddWhere(conditions, command);
            if (groupFields != null && groupFields.Any())
            {
                command.CommandText += " GROUP BY " + string.Join(", ", groupFields.Values);
            }
            if (orderFields != null && orderFields.Any())
            {
                command.CommandText += " ORDER BY " + string.Join(", ", orderFields.Select(kvp => string.Format("{0} {1}", kvp.Key, kvp.Value ? "ASC" : "DESC")));
            }
            return command;
        }

        public void Select<T, P>(Type type, Action<object> resultAction, LambdaExpression expr, IDictionary<Expression<Func<T, P>>, bool> orderFields = null)
        {
            Select(type, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(false), x => x.Value));
        }

        public void Select(Type type, Action<object> resultAction, LambdaExpression expr, IDictionary<string, bool> orderFields = null)
        {
            Select(type, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields);
        }

        public void Select<T, P>(Type[] types, Action<IEnumerable<object>> resultAction, LambdaExpression expr, IDictionary<Expression<Func<T, P>>, bool> orderFields = null)
        {
            Select(types, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(true), x => x.Value));
        }

        public void Select(Type[] types, Action<IEnumerable<object>> resultAction, LambdaExpression expr, IDictionary<string, bool> orderFields = null)
        {
            Select(types, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields);
        }

        public void Select(Type type, Action<object> resultAction, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            Select(new[] { type }, (objects) => {
                if (resultAction != null && objects != null && objects.Any())
                {
                    var data = objects.First();
                    resultAction(data);
                }
            }, conditions, orderFields);
        }

        public void Select(IEnumerable<Type> types, Action<IEnumerable<object>> resultAction, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            if (types != null)
            {
                Select(types.Select(x=> DataBase.LoadTable(x)), (fields, reader) => {
                    if (resultAction != null)
                    {
                        var index = 0;
                        List<object> objects = new List<object>();
                        foreach (var type in types)
                        {
                            var data = Activator.CreateInstance(type, new object[0]);
                            index = DataBase.Read(reader, data, index);
                            objects.Add(data);
                        }
                        resultAction(objects.ToArray());
                    }
                }, conditions, orderFields);
            }
        }

        public void Select(Table.Table table, Action<IDictionary<int, string>, DbDataReader> readAction, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            Select(new[] { table }, readAction, conditions, orderFields);
        }

        public void Select(IEnumerable<Table.Table> tables, Action<IDictionary<int, string>, DbDataReader> readAction, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            if (tables != null)
            {
                Dictionary<int, string> fields = new Dictionary<int, string>();
                int i = 0;
                foreach(var table in tables.Where(x=> x != null))
                {
                    var tablefields = table.GetSelectFields(true);
                    foreach (var kvp in tablefields)
                    {
                        fields.Add(i, kvp.Value);
                        i++;
                    }
                }
                Select(tables.Where(x => x != null).Select(x=>x.Name), fields, readAction, conditions, orderFields);
            }
        }

        public void Select(string tableName, IDictionary<int, string> fields, Action<IDictionary<int, string>, DbDataReader> readAction = null, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            Select(new[] { tableName }, fields, readAction, conditions, orderFields);
        }

        public void Select(IEnumerable<string> tableNames, IDictionary<int, string> fields, Action<IDictionary<int, string>, DbDataReader> readAction = null, IEnumerable<Condition.Condition> conditions = null, IDictionary<string, bool> orderFields = null)
        {
            if(tableNames != null && tableNames.Any() && tableNames.Any(x=>!string.IsNullOrEmpty(x)))
            {
                DoCommand((command) => {
                    command = CreateSelectCommand(command, tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(), fields, conditions, orderFields);
                }, (command) => {
                    var reader = command.ExecuteReader();
                    if (reader.HasRows)
                    {
                        bool isNext = reader.Read();
                        while (isNext)
                        {
                            readAction?.Invoke(fields, reader);
                            isNext = reader.Read();
                        }
                    }
                });
            }
        }
    }
}
