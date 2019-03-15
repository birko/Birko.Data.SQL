using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connector
{
    public abstract partial class AbstractConnector
    {
        public void AlterTableAdd(Type type, Field.AbstractField field)
        {
            AlterTableAdd(type, new[] { field });
        }

        public void AlterTableAdd(Type type, IEnumerable<Field.AbstractField> fields)
        {
            AlterTableAdd(DataBase.LoadTable(type), fields);
        }

        public void AlterTableAdd(Table.Table table, Field.AbstractField field)
        {
            AlterTableAdd(table, new[] { field });
        }

        public void AlterTableAdd(Table.Table table, IEnumerable<Field.AbstractField> fields)
        {
            if (table != null && fields != null && fields.Any())
            {
                AlterTableAdd(table.Name, fields);
            }
        }

        public void AlterTableAdd(string tableName, IEnumerable<Field.AbstractField> fields)
        {
            if (!string.IsNullOrEmpty(tableName) && fields != null && fields.Any())
            {
                foreach (var field in fields.Where(x => x != null))
                {
                    DoCommand((command) => {
                        command.CommandText = "ALTER TABLE "
                            + tableName
                            + " ADD COLUMN "
                            + FieldDefinition(field);
                    },  (command) => {
                        command.ExecuteNonQuery();
                    }, true);
                }
            }
        }
    }
}
