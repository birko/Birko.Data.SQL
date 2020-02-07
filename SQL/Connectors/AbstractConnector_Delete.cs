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

        public void Delete(Tables.Table table, LambdaExpression expr)
        {
            Delete(table, DataBase.ParseConditionExpression(expr));
        }

        public void Delete(Tables.Table table, IEnumerable<Conditions.Condition> conditions = null)
        {
            var tableName = table.Name;
            Delete(tableName, conditions);
        }

        public void Delete(string tableName, LambdaExpression expr)
        {
            Delete(tableName, DataBase.ParseConditionExpression(expr));
        }

        private void Delete(string tableName, IEnumerable<Conditions.Condition> conditions = null)
        {
            DoCommand((command) => {
                command.CommandText = "DELETE FROM " + tableName;
                AddWhere(conditions, command);
            }, (command) => {
                command.ExecuteNonQuery();
            }, true);
        }
    }
}
