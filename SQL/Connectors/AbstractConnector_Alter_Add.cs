using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public void AlterTableAdd(Type type, Fields.AbstractField field)
        {
            AlterTableAdd(type, new[] { field });
        }

        public void AlterTableAdd(Type type, IEnumerable<Fields.AbstractField> fields)
        {
            AlterTableAdd(DataBase.LoadTable(type), fields);
        }

        public void AlterTableAdd(Tables.Table table, Fields.AbstractField field)
        {
            AlterTableAdd(table, new[] { field });
        }

        public void AlterTableAdd(Tables.Table table, IEnumerable<Fields.AbstractField> fields)
        {
            if (table != null && fields != null && fields.Any())
            {
                AlterTableAdd(table.Name, fields);
            }
        }

        public void AlterTableAdd(string tableName, IEnumerable<Fields.AbstractField> fields)
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
