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
            Property.SetValue(value, reader.GetInt32(index), null);
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

    public class IntegerFunction : FunctionField
    {
        public IntegerFunction(System.Reflection.PropertyInfo property, string name, object[] parameters)
            : base(property, name, parameters, DbType.Int32, true)
        {

        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetInt32(index), null);
        }
    }

    public class NullableIntegerFunction : IntegerFunction
    {
        public NullableIntegerFunction(System.Reflection.PropertyInfo property, string name, object[] parameters)
            : base(property, name, parameters)
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
