using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace Birko.Data.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Property)]
    public abstract class Field : System.Attribute
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class NamedField : Field
    {
        public string Name { get; internal set; } = null;
        public NamedField(string name = null)
        {
            Name = name;
        }
    }


    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class UniqueField : Field
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class PrimaryField : Field
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class IncrementField : Field
    {

    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class PrecisionField : Field
    {
        public int Precision = 0;
        public PrecisionField( int precision = 0) : base()
        {
            Precision = precision;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class ScaleField : Field
    {
        public int Scale = 0;
        public ScaleField(int scale = 0) : base()
        {
            Scale = scale;
        }
    }
}
