using System;
using System.Collections.Generic;
using System.Text;

namespace Birko.Data.Attribute
{
    [System.AttributeUsage(System.AttributeTargets.Property)]
    public class AvgField : AggregateField
    {
        public AvgField(Type modelType, string modelPropertyName, string name = null) : base(modelType, modelPropertyName, name)
        { }
    }
}
