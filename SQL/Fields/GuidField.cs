using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public class GuidField : AbstractField
    {
        public GuidField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
            : base(property, name, DbType.Guid, primary, true, unique)
        {
        }

        public override void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetGuid(index), null);
        }
    }

    public class NullableGuidField : GuidField
    {
        public NullableGuidField(System.Reflection.PropertyInfo property, string name, bool primary = false, bool unique = false)
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
