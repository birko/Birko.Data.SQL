using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public class IntegerField : AbstractField
    {
        public IntegerField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false)
            : base(property,name, DbType.Int32, primary, true, unique, autoincrement)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            var intVal = reader.GetInt32(index);
            var targetType = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
            if (targetType.IsEnum)
                Property.SetValue(value, Enum.ToObject(targetType, intVal), null);
            else
                Property.SetValue(value, intVal, null);
        }

        public override object? Write(object value)
        {
            var val = Property.GetValue(value);
            if (val == null) return null;
            var targetType = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
            if (targetType.IsEnum)
                return (int)val;
            return val;
        }
    }

    public class NullableIntegerField : IntegerField
    {
        public NullableIntegerField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false)
            : base(property, name, primary, unique, autoincrement)
        {
            IsNotNull = false;
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
            {
                Property.SetValue(value, null, null);
            }
            else
            {
                base.Read(value, reader, index);
            }
        }
    }
}
