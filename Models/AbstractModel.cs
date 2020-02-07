using System;
using System.Collections.Generic;
using System.Text;
using Birko.Data.Attributes;
using Birko.Data.ViewModels;

namespace Birko.Data.Models
{
    public abstract partial class AbstractDatabaseModel : AbstractModel
    {
        [GuidField(null, true, true)]
        public override Guid? Guid { get; set; } = null;
    }
}
