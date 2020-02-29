using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public class StringField : AbstractField
    {
        public StringField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.String, primary, false, unique)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
            {
                Property.SetValue(value, null, null);
            }
            else
            {
                Property.SetValue(value, reader.GetString(index), null);
            }
        }
    }
}
