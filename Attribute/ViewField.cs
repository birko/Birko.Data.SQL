using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.Attribute
{
    [System.AttributeUsage(System.AttributeTargets.Property)]
    public class ViewField : System.Attribute
    {
        public string Name { get; internal set; } = null;
        public Type ModelType { get; private set; }
        public string ModelProperyName { get; internal set; } = null;

        public ViewField(Type modelType, string modelPropertyName, string name = null)
        {
            ModelType = modelType;
            ModelProperyName = modelPropertyName;
            Name = name;
        }
    }
}
