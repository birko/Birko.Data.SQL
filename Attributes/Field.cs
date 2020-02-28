using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace Birko.Data.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Property)]
    public class Field : System.Attribute
    {
        public bool Primary { get; private set; } = false;
        public bool Unique { get; private set; } = false;

        public string Name { get; internal set; } = null;

        public Field(string name = null, bool primary = false, bool unique = false)
        {
            Primary = primary;
            Unique = unique;
            Name = name;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class NumberField : Field
    {
        public bool AutoIncrement { get; private set; } = false;
        public NumberField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false) : base(name, primary, unique)
        {
            AutoIncrement = autoIncrement;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class PrecisionField : NumberField
    {
        public int Precision = 0;
        public PrecisionField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false, int precision = 0) : base(name, primary, unique, autoIncrement)
        {
            Precision = precision;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class ScaleField : PrecisionField
    {
        public int Scale = 0;
        public ScaleField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false, int precision = 0, int scale = 0) : base(name, primary, unique, autoIncrement, precision)
        {
            Scale = scale;
        }
    }
}
