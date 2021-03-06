﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public class DateTimeField : AbstractField
    {
        public DateTimeField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.DateTime, primary, true, unique)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetDateTime(index), null);
        }
    }

    public class NullableDateTimeField : DateTimeField
    {
        public NullableDateTimeField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
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
