using Birko.Data.SQL.Attributes;

namespace Birko.Models
{
    public abstract class AbstractDatabaseTree : AbstractTree
    {
        [PrecisionField(1024)]
        public override string Path { get; set; } = string.Empty;
    }
}
