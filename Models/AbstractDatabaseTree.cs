using Birko.Data.Attributes;

namespace Birko.Models
{
    public abstract class AbstractDatabaseTree : AbstractTree
    {
        [PrecisionField(1024)]
        public override string Path { get; set; }
    }
}
