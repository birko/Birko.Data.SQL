using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public class CharField : StringField
    {
        public int Lenght = 1;
        public CharField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, int? lenght = 1)
            : base(property, name, primary, unique)
        {
            Lenght = 1;
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

    public class CharFunction : StringFunction
    {
        public int Lenght = 1;

        public CharFunction(System.Reflection.PropertyInfo property, string name, object[] parameters, int lenght = 1)
            : base(property, name, parameters)
        {
            Lenght = lenght;
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetString(index), null);
        }
    }
}
