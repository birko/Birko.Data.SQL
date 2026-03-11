using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public void AlterTableDrop(Type type, IEnumerable<Fields.AbstractField> fields)
        {
            AlterTableDrop(DataBase.LoadTable(type), fields);
        }

        public void AlterTableDrop(Tables.Table table, IEnumerable<Fields.AbstractField> fields)
        {
            if (table != null && fields != null && fields.Any())
            {
                AlterTableDrop(table.Name, fields);
            }
        }

        public void AlterTableDrop(string tableName, IEnumerable<Fields.AbstractField> fields)
        {
            if (!string.IsNullOrEmpty(tableName) && fields != null && fields.Any())
            {
                foreach (var field in fields.Where(x => x != null))
                {
                    DoCommandWithTransaction((command) => {
                        command.CommandText = "ALTER TABLE "
                            + QuoteIdentifier(tableName)
                            + " DROP COLUMN "
                            + field.Name;
                    }, (command) => {
                        command.ExecuteNonQuery();
                    }, true);
                }
            }
        }
    }
}
