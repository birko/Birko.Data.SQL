﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            return CreateSelectCommand(command, tableNames, fields, null, conditions, null, orderFields, limit, offset);
        }

        public virtual DbCommand CreateSelectCommand(DbCommand command, Tables.View view, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            var leftTables = view.Join?.Select(x => x.Left).Distinct().Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (leftTables != null)
            {
                foreach (var tableName in view.Join.Select(x => x.Right).Distinct().Where(x => !string.IsNullOrEmpty(x)))
                {
                    leftTables.Remove(tableName);
                }
            }
            return CreateSelectCommand(command, leftTables ?? view.Tables.Select(x => x.Name), view.GetSelectFields(), view.Join, conditions, view.HasAggregateFields() ? view.GetSelectFields(true) : null, orderFields, limit, offset);
        }


        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Join> joinconditions = null, IEnumerable<Conditions.Condition> conditions = null, IDictionary<int, string> groupFields = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            command.CommandText = "SELECT " + string.Join(", ", fields.Values) + " FROM ";

            Dictionary<string, List<Conditions.Join>> joins = new Dictionary<string, List<Conditions.Join>>();
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
                            joins.Add(join.Left, new List<Conditions.Join>());
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
                        command.CommandText +=
                            joingroup.Key.JoinType switch
                            {
                                Conditions.JoinType.Inner => " INNER JOIN ",
                                Conditions.JoinType.LeftOuter => " LEFT OUTER JOIN ",
                                _ => " CROSS JOIN ",
                            };
                        command.CommandText += joingroup.Key.Right;
                        if (joingroup.Key.JoinType != Conditions.JoinType.Cross && joingroup.Value != null && joingroup.Value.Any())
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
            if(limit != null)
            {
                command.CommandText += LimitOffsetDefinition(command, limit, offset);
            }
            return command;
        }

        public void Select<T, P>(Type type, Action<object> resultAction, LambdaExpression expr, IDictionary<Expression<Func<T, P>>, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            Select(type, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(false), x => x.Value), limit, offset);
        }

        public void Select(Type type, Action<object> resultAction, LambdaExpression expr, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            Select(type, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset);
        }

        public void Select<T, P>(Type[] types, Action<IEnumerable<object>> resultAction, LambdaExpression expr, IDictionary<Expression<Func<T, P>>, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            Select(types, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(true), x => x.Value), limit, offset);
        }

        public void Select(Type[] types, Action<IEnumerable<object>> resultAction, LambdaExpression expr, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            Select(types, resultAction, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset);
        }

        public void Select(Type type, Action<object> resultAction, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            Select(new[] { type }, (objects) => {
                if (resultAction != null && objects != null && objects.Any())
                {
                    var data = objects.First();
                    resultAction(data);
                }
            }, conditions, orderFields, limit, offset);
        }

        public void Select(IEnumerable<Type> types, Action<IEnumerable<object>> resultAction, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
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
                }, conditions, orderFields, limit, offset);
            }
        }

        public void Select(Tables.Table table, Action<IDictionary<int, string>, DbDataReader> readAction, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            Select(new[] { table }, readAction, conditions, orderFields, limit, offset);
        }

        public void Select(IEnumerable<Tables.Table> tables, Action<IDictionary<int, string>, DbDataReader> readAction, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
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
                Select(tables.Where(x => x != null).Select(x=>x.Name), fields, readAction, conditions, orderFields, limit, offset);
            }
        }

        public void Select(string tableName, IDictionary<int, string> fields, Action<IDictionary<int, string>, DbDataReader> readAction = null, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            Select(new[] { tableName }, fields, readAction, conditions, orderFields, limit, offset);
        }

        public void Select(IEnumerable<string> tableNames, IDictionary<int, string> fields, Action<IDictionary<int, string>, DbDataReader> readAction = null, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            if(tableNames != null && tableNames.Any() && tableNames.Any(x=>!string.IsNullOrEmpty(x)))
            {
                DoCommand((command) => {
                    command = CreateSelectCommand(command, tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(), fields, conditions, orderFields, limit, offset);
                }, (command) => {
                    using var reader = command.ExecuteReader();
                    try
                    {

                        if (reader.HasRows)
                        {
                            bool isNext = reader.Read();
                            while (isNext)
                            {
                                readAction?.Invoke(fields, reader);
                                isNext = reader.Read();
                            }
                        }
                        reader.Close();
                    }
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                    finally
                    {
                        reader.Close();
                    }
                });
            }
        }
    }
}
