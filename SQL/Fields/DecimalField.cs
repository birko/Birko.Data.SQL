﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public class DecimalField : AbstractField
    {
        public int? Precision = null;
        public int? Scale = null;
        public DecimalField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false, int? precision = null, int? scale = null)
            : base(property, name, DbType.Decimal, primary, true, unique, autoincrement)
        {
            Precision = precision;
            Scale = scale;
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetDecimal(index), null);
        }
    }

    public class NullableDecimalField : DecimalField
    {
        public NullableDecimalField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false, bool autoincrement = false, int? precision = null, int? scale = null)
            : base(property, name, primary, unique, autoincrement, precision, scale)
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
