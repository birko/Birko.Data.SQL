using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public class BooleanField : AbstractField
    {
        public BooleanField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.Boolean, primary, true, unique)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetBoolean(index), null);
        }
    }

    public class NullableBooleanField : BooleanField
    {
        public NullableBooleanField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, primary, unique)
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
