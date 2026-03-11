using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public void Delete(Type type, LambdaExpression expr)
        {
            Delete(type, DataBase.ParseConditionExpression(expr));
        }

        public void Delete(Type type, IEnumerable<Conditions.Condition> conditions = null)
        {
            Delete(DataBase.LoadTable(type), conditions);
        }

        public void Delete(Tables.Table table, IEnumerable<Conditions.Condition> conditions = null)
        {
            var tableName = table.Name;
            Delete(tableName, conditions);
        }

        private void Delete(string tableName, IEnumerable<Conditions.Condition> conditions = null)
        {
            DoCommandWithTransaction((command) => {
                command.CommandText = "DELETE FROM " + QuoteIdentifier(tableName);
                AddWhere(conditions, command);
            }, (command) => {
                command.ExecuteNonQuery();
            }, true);
        }
    }
}
