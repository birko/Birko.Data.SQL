using System;
using System.Collections.Generic;
using System.Text;
using Birko.Data.Attribute;
using Birko.Data.ViewModel;

namespace Birko.Data.Model
{
    public abstract partial class AbstractDatabaseModel : AbstractModel
    {
        [GuidField(null, true, true)]
        public override Guid? Guid { get; set; } = null;
    }
}
