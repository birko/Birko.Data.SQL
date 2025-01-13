using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public void DropTable(Type type)
        {
            DropTable(new[] { type });
        }

        public void DropTable(Tables.Table table)
        {
            DropTable(new[] { table.Name });
        }

        public void DropTable(Type[] types)
        {
            DropTable(DataBase.LoadTables(types));
        }

        public void DropTable(IEnumerable<Tables.Table> tables)
        {
            if (tables != null && tables.Any() && tables.Any(x => x != null))
            {
                DropTable(tables.Where(x => x != null).Select(x => x.Name));
            }
        }

        public void DropTable(IEnumerable<string> tables)
        {
            if (tables != null && tables.Any() && tables.Any(x => !string.IsNullOrEmpty(x)))
            {
                foreach (var tableName in tables.Where(x => !string.IsNullOrEmpty(x)))
                {
                    DoCommandWithTransaction((command) => {
                        command.CommandText = "DROP TABLE IF EXISTS " + tableName;
                    }, (command) => {
                        command.ExecuteNonQuery();
                    }, true);
                }
            }
        }
    }
}
