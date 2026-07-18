using System.Collections.Generic;

namespace Birko.Data.SQL.Tables
{
    public class IndexDefinition
    {
        public string Name { get; set; } = null!;
        /// <summary>When true, a UNIQUE index is emitted (a composite unique constraint over <see cref="Columns"/>).</summary>
        public bool Unique { get; set; }
        public List<IndexColumn> Columns { get; } = new();
    }

    public class IndexColumn
    {
        public string ColumnName { get; set; } = null!;
        public int Order { get; set; }
        public bool IsDescending { get; set; }
    }
}
