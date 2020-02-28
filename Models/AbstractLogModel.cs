using System;
using System.Collections.Generic;
using System.Text;
using Birko.Data.Attributes;
using Birko.Data.ViewModels;

namespace Birko.Data.Models
{
    public abstract class AbstractDatabaseLogModel : AbstractLogModel
    {
        //overide from AbstractModel
        [Field(null, true, true)]
        public override Guid? Guid { get; set; } = null;

        //override from  AbstractLogModel
        public override DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public override DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public override DateTime? PrevUpdatedAt { get; set; } = null;
    }
}
