using System.Collections.Generic;

namespace Birko.Data.SQL.Tables
{
    public class IndexDefinition
    {
        public string Name { get; set; } = null!;
        public List<IndexColumn> Columns { get; } = new();
    }

    public class IndexColumn
    {
        public string ColumnName { get; set; } = null!;
        public int Order { get; set; }
        public bool IsDescending { get; set; }
    }
}
