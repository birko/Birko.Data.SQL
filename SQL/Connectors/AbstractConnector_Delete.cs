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

        public void Delete(Type type, IEnumerable<Conditions.Condition>? conditions = null)
        {
            Delete(DataBase.LoadTable(type), conditions);
        }

        public void Delete(Tables.Table table, IEnumerable<Conditions.Condition>? conditions = null)
        {
            var tableName = table.Name;
            Delete(tableName, conditions);
        }

        /// <summary>
        /// Deletes EVERY row of the table — the explicit all-rows door (SH-H002).
        /// </summary>
        /// <remarks>
        /// Emits a clean conditionless DELETE. This is the only way, together with an explicit
        /// <c>x =&gt; true</c> filter, to reach that statement: every other path throws
        /// <see cref="Data.Exceptions.WholeTableWriteException"/>, so a bare DELETE in a query log means
        /// somebody asked for it. Use <c>Destroy()</c> to drop the table instead of emptying it.
        /// </remarks>
        public void DeleteAll(Type type)
        {
            Delete(DataBase.LoadTable(type).Name, conditions: null, allowAllRows: true);
        }

        private void Delete(string tableName, IEnumerable<Conditions.Condition>? conditions = null, bool allowAllRows = false)
        {
            // SH-H002: refuse BEFORE the transaction wrapper — it re-wraps every exception from its callback
            // in a bare Exception, which no `catch (WholeTableWriteException)` could select.
            if (!allowAllRows && WouldTargetEveryRow(conditions))
            {
                throw new Data.Exceptions.WholeTableWriteException("delete", tableName);
            }

            DoCommandWithTransaction((command) => {
                command.CommandText = "DELETE FROM " + QuoteIdentifier(tableName);
                AddRequiredWhere(conditions, command, "delete", tableName, allowAllRows);
            }, (command) => {
                command.ExecuteNonQuery();
            }, true);
        }
    }
}
