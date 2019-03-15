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
        public long SelectCount(Type type, LambdaExpression expr)
        {
            return SelectCount(new[] { type }, expr);
        }

        public long SelectCount(IEnumerable<Type> types, LambdaExpression expr)
        {
            return SelectCount(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null);
        }

        public long SelectCount(Type type, IEnumerable<Condition.Condition> conditions = null)
        {
            return SelectCount(new[] { type }, conditions);
        }

        public long SelectCount(IEnumerable<Type> types, IEnumerable<Condition.Condition> conditions = null)
        {
            return (types != null) ? SelectCount(types.Select(x => DataBase.LoadTable(x)), conditions) : 0;
        }

        public long SelectCount(Table.Table table, LambdaExpression expr)
        {
            return SelectCount(new[] { table }, expr);
        }

        public long SelectCount(IEnumerable<Table.Table> tables, LambdaExpression expr)
        {
            return SelectCount(tables, (expr != null) ? DataBase.ParseConditionExpression(expr) : null);
        }

        public long SelectCount(Table.Table table, IEnumerable<Condition.Condition> conditions = null)
        {
            return SelectCount(new[] { table.Name }, conditions);
        }

        public long SelectCount(IEnumerable<Table.Table> tables, IEnumerable<Condition.Condition> conditions = null)
        {
            return (tables != null) ? SelectCount(tables.Select(x => x.Name), conditions) : 0;
        }

        public long SelectCount(string tableName, IEnumerable<Condition.Condition> conditions = null)
        {
            return SelectCount(new[] { tableName }, conditions);
        }

        public long SelectCount(IEnumerable<string> tableNames, IEnumerable<Condition.Condition> conditions = null)
        {
            return SelectCount(tableNames, null, conditions);
        }

        public long SelectCount(IEnumerable<string> tableNames, IEnumerable<Condition.Join> joinconditions = null, IEnumerable<Condition.Condition> conditions = null)
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
                    count = (long)command.ExecuteScalar();
                });
            }
            return count;
        }
    }
}
