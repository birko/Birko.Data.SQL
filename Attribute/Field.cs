using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace Birko.Data.Attribute
{
    [System.AttributeUsage(System.AttributeTargets.Property)]
    public abstract class Field : System.Attribute
    {
        public bool Primary { get; private set; } = false;
        public bool Unique { get; private set; } = false;
        public bool AutoIncrement { get; private set; } = false;

        public string Name { get; internal set; } = null;

        public Field(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false)
        {
            Primary = primary;
            Unique = unique;
            AutoIncrement = autoIncrement;
            Name = name;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class StringField : Field
    {
        public StringField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false) : base(name, primary, unique, autoIncrement) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class BooleanField : Field
    {
        public BooleanField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false) : base(name, primary, unique, autoIncrement) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class IntegerField : Field
    {
        public IntegerField(string name = null, bool primary = false, bool unique = false,  bool autoIncrement = false) : base(name, primary, unique, autoIncrement) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class GuidField : Field
    {
        public GuidField(string name = null, bool primary = false, bool unique = false,  bool autoIncrement = false) : base(name, primary, unique, autoIncrement) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class DecimalField : Field
    {
        public DecimalField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false) : base(name, primary, unique, autoIncrement) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class NumericField : DecimalField
    {
        public int Precision = 0;
        public int Scale = 0;
        public NumericField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false, int precision = 0, int scale = 0) : base(name, primary, unique, autoIncrement)
        {
            Precision = precision;
            Scale = scale;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = true)]
    public class DateTimeField : Field
    {
        public DateTimeField(string name = null, bool primary = false, bool unique = false, bool autoIncrement = false) : base(name, primary, unique, autoIncrement) { }
    }
}
