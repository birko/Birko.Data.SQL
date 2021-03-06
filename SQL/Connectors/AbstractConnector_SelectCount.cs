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
        public long SelectCount(Type type, LambdaExpression expr)
        {
            return SelectCount(new[] { type }, expr);
        }

        public long SelectCount(IEnumerable<Type> types, LambdaExpression expr)
        {
            return SelectCount(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null);
        }

        public long SelectCount(Type type, IEnumerable<Conditions.Condition> conditions = null)
        {
            return SelectCount(new[] { type }, conditions);
        }

        public long SelectCount(IEnumerable<Type> types, IEnumerable<Conditions.Condition> conditions = null)
        {
            return (types != null) ? SelectCount(types.Select(x => DataBase.LoadTable(x)), conditions) : 0;
        }

        public long SelectCount(Tables.Table table, LambdaExpression expr)
        {
            return SelectCount(new[] { table }, expr);
        }

        public long SelectCount(IEnumerable<Tables.Table> tables, LambdaExpression expr)
        {
            return SelectCount(tables, (expr != null) ? DataBase.ParseConditionExpression(expr) : null);
        }

        public long SelectCount(Tables.Table table, IEnumerable<Conditions.Condition> conditions = null)
        {
            return SelectCount(new[] { table.Name }, conditions);
        }

        public long SelectCount(IEnumerable<Tables.Table> tables, IEnumerable<Conditions.Condition> conditions = null)
        {
            return (tables != null) ? SelectCount(tables.Select(x => x.Name), conditions) : 0;
        }

        public long SelectCount(string tableName, IEnumerable<Conditions.Condition> conditions = null)
        {
            return SelectCount(new[] { tableName }, conditions);
        }

        public long SelectCount(IEnumerable<string> tableNames, IEnumerable<Conditions.Condition> conditions = null)
        {
            return SelectCount(tableNames, null, conditions);
        }

        public long SelectCount(IEnumerable<string> tableNames, IEnumerable<Conditions.Join> joinconditions = null, IEnumerable<Conditions.Condition> conditions = null)
        {
            long count = 0;
            if (tableNames != null && tableNames.Any() && tableNames.Any(x => !string.IsNullOrEmpty(x)))
            {
                DoCommand((command) => {
                    command = CreateSelectCommand(
                        command,
                        tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(),
                        new Dictionary<int, string>()
                        {
                            { 0, "count(*) as count"}
                        },
                        joinconditions, conditions);
                }, (command) =>
                {
                    var data = command.ExecuteScalar();
                    count = Convert.ToInt64(command.ExecuteScalar());
                });
            }
            return count;
        }
    }
}
