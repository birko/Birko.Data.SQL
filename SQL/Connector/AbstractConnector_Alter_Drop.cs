using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connector
{
    public abstract partial class AbstractConnector
    {
        public void AlterTableDrop(Type type, Field.AbstractField field)
        {
            AlterTableDrop(type, new[] { field });
        }

        public void AlterTableDrop(Type type, IEnumerable<Field.AbstractField> fields)
        {
            AlterTableDrop(DataBase.LoadTable(type), fields);
        }

        public void AlterTableDrop(Table.Table table, Field.AbstractField field)
        {
            AlterTableDrop(table, new[] { field });
        }

        public void AlterTableDrop(Table.Table table, IEnumerable<Field.AbstractField> fields)
        {
            if (table != null && fields != null && fields.Any())
            {
                AlterTableDrop(table.Name, fields);
            }
        }

        public void AlterTableDrop(string tableName, IEnumerable<Field.AbstractField> fields)
        {
            if (!string.IsNullOrEmpty(tableName) && fields != null && fields.Any())
            {
                foreach (var field in fields.Where(x => x != null))
                {
                    DoCommand((command) => {
                        command.CommandText = "ALTER TABLE "
                            + tableName
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
