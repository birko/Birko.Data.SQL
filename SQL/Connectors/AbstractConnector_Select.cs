using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            return CreateSelectCommand(command, tableNames, fields, null, conditions, null, orderFields, limit, offset);
        }

        public virtual DbCommand CreateSelectCommand(DbCommand command, Tables.View view, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            if (view == null)
            { 
                throw new ArgumentNullException(nameof(view)); 
            }

            if (view.Join == null)
            {
                throw new ArgumentNullException(nameof(view.Join));
            }

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

        public virtual DbCommand CreateSelectCommand(DbCommand command, IEnumerable<string> tableNames, IDictionary<int, string> fields, IEnumerable<Conditions.Join>? joinconditions = null, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<int, string>? groupFields = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            command.CommandText = "SELECT " + string.Join(", ", fields.Values) + " FROM ";

            Dictionary<string, List<Conditions.Join>> joins = new();
            if (joinconditions != null && joinconditions.Any())
            {
                string? prevleft = null;
                string? prevright = null;
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

        public IEnumerable<object> Select<T, P>(Type type, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var o in Select(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(false), x => x.Value), limit, offset))
            {
                yield return o;
            }
        }

        public IEnumerable<object> Select(Type type, LambdaExpression? expr = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var o in Select(type, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset))
            {
                yield return o;
            }
        }

        public IEnumerable<object> Select<T, P>(Type[] types, LambdaExpression? expr = null, IDictionary<Expression<Func<T, P>>, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var o in Select(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields?.ToDictionary(x => DataBase.GetField(x.Key).GetSelectName(true), x => x.Value), limit, offset))
            {
                yield return o;
            }
        }

        public IEnumerable<object> Select(Type[] types, LambdaExpression expr, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var item in Select(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null, orderFields, limit, offset))
            {
                yield return item;
            }
        }

        public IEnumerable<object> Select(Type type, IEnumerable<Conditions.Condition> conditions = null, IDictionary<string, bool> orderFields = null, int? limit = null, int? offset = null)
        {
            foreach (var items in Select(new[] { type }, conditions, orderFields, limit, offset))
            {
                yield return items.FirstOrDefault();
            }
        }

        public IEnumerable<IEnumerable<object>> Select(IEnumerable<Type> types, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            if (types == null)
            {
                yield break;
            }

            foreach (var set in Select(types.Select(x => DataBase.LoadTable(x)), (fields, reader) =>
            {
                var index = 0;
                List<object> objects = new();
                foreach (var type in types)
                {
                    var data = Activator.CreateInstance(type, Array.Empty<object>());
                    index = DataBase.Read(reader, fields, index);
                    objects.Add(data);
                }
                return objects.AsEnumerable();
            }, conditions, orderFields, limit, offset))
            {
                yield return set;
            }
        }

        public IEnumerable<IEnumerable<object>> Select(
            Tables.Table table,
            Func<IDictionary<int, string>, DbDataReader, IEnumerable<object>>? transformFunction = null,
            IEnumerable<Conditions.Condition>? conditions = null,
            IDictionary<string, bool>? orderFields = null,
            int? limit = null, 
            int? offset = null
        )
        {
            foreach (var item in  Select(new[] { table }, transformFunction, conditions, orderFields, limit, offset))
            {
                yield return item;
            }
        }

        public IEnumerable<IEnumerable<object>> Select(IEnumerable<Tables.Table> tables, Func<IDictionary<int, string>, DbDataReader, IEnumerable<object>>? transformFunction = null, IEnumerable<Conditions.Condition>? conditions = null, IDictionary<string, bool>? orderFields = null, int? limit = null, int? offset = null)
        {
            if (tables == null)
            {
                yield break;
            }
            Dictionary<int, string> fields = new Dictionary<int, string>();
            int i = 0;
            foreach (var table in tables.Where(x => x != null))
            {
                var tablefields = table.GetSelectFields(true);
                foreach (var kvp in tablefields)
                {
                    fields.Add(i, kvp.Value);
                    i++;
                }
            }

            foreach (var item in Select(tables.Where(x => x != null).Select(x => x.Name), fields, (reader) => transformFunction?.Invoke(fields, reader) ?? null, conditions, orderFields, limit, offset))
            {
                yield return item;
            }

        }

        public IEnumerable<IEnumerable<object>> Select(
            string tableName, IDictionary<int, string> fields,
            Func<DbDataReader, IEnumerable<object>>? transformFunction = null,
            IEnumerable<Conditions.Condition>? conditions = null,
            IDictionary<string, bool>? orderFields = null, 
            int? limit = null, int? offset = null)
        {
            foreach (var item in Select(new[] { tableName }, fields, transformFunction, conditions, orderFields, limit, offset))
            {
                yield return item;
            }
        }

        public IEnumerable<IEnumerable<object>> Select(
            IEnumerable<string> tableNames,
            IDictionary<int, string> fields,
            Func<DbDataReader, IEnumerable<object>>? transformFunction = null,
            IEnumerable<Conditions.Condition>? conditions = null, 
            IDictionary<string, bool>? orderFields = null, 
            int? limit = null, 
            int? offset = null
        )
        {
            if (!(tableNames?.Any(x => !string.IsNullOrEmpty(x)) ?? false))
            {
                yield break;
            }
            foreach(var item in RunReaderCommand((command) => {
                command = CreateSelectCommand(command, tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(), fields, conditions, orderFields, limit, offset);
            }, transformFunction))
            { 
                yield return item; 
            }
        }
    }
}
