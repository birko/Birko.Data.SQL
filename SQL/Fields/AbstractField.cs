using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;

namespace Birko.Data.SQL.Fields
{
    public abstract class AbstractField
    {
        public string Name { get; set; }
        public DbType Type { get; set; } = DbType.String;
        public bool IsPrimary { get; set; } = false;
        public bool IsUnique { get; set; } = false;
        public bool IsNotNull { get; set; } = false;
        public bool IsAutoincrement { get; set; } = false;
        public bool IsAggregate { get; set; } = false;
        public System.Reflection.PropertyInfo Property { get; set; }
        public Tables.Table Table { get; set; }

        public AbstractField(System.Reflection.PropertyInfo property, string name, DbType type = DbType.String, bool primary = false, bool notNull = false, bool unique = false, bool autoincrement = false)
        {
            Name = name;
            Type = type;
            IsPrimary = primary;
            IsUnique = unique;
            IsNotNull = notNull;
            IsAutoincrement = autoincrement;
            Property = property;
        }

        public string GetSelectName(bool withName = false)
        {
            return (IsAggregate)
                        ? string.Format("{0}({1})",
                            Name,
                            string.Join(",", (this as Fields.FunctionField).Parameters?.Select(x => string.Format("{0}{1}", (withName ? Table.Name + "." : string.Empty), x)) ?? new string[0]))
                        : (withName ? Table.Name + "." : string.Empty) + Name;
        }

        public virtual object Write(object value)
        {
            return Property.GetValue(value, null);
        }

        public virtual void Read(object value, DbDataReader reader, int index)
        {
            Property.SetValue(value, reader.GetValue(index), null);
        }

        private static bool IsNullable(Type type)
        {
            if (!type.IsValueType) return true; // ref-type
            if (Nullable.GetUnderlyingType(type) != null) return true; // Nullable<T>
            return false; // value-type
        }

        public static AbstractField CreateAbstractField(System.Reflection.PropertyInfo property, Attributes.Field field)
        {
            string name = !string.IsNullOrEmpty(field.Name) ? field.Name : property.Name;
            var isNullable = IsNullable(property.PropertyType);

            if (field is Attributes.BooleanField)
            {
                return (!isNullable)
                        ? (AbstractField)new BooleanField(property, name, field.Primary, field.Unique)
                        : (AbstractField)new NullableBooleanField(property, name, field.Primary, field.Unique);
            }
            if (field is Attributes.DateTimeField)
            {
                return (!isNullable)
                        ? (AbstractField)new DateTimeField(property, name, field.Primary, field.Unique)
                        : (AbstractField)new NullableDateTimeField(property, name, field.Primary, field.Unique);
            }
            if (field is Attributes.NumericField numericField)
            {
                return (!isNullable)
                        ? (AbstractField)new DecimalField(property, name, field.Primary, field.Unique, field.AutoIncrement, numericField.Precision, numericField.Scale)
                        : (AbstractField)new NullableDecimalField(property, name, field.Primary, field.Unique, field.AutoIncrement, numericField.Precision, numericField.Scale);
            }
            if (field is Attributes.DecimalField)
            {
                return (!isNullable)
                        ? (AbstractField)new DecimalField(property, name, field.Primary, field.Unique, field.AutoIncrement)
                        : (AbstractField)new NullableDecimalField(property, name, field.Primary, field.Unique, field.AutoIncrement);
            }
            if (field is Attributes.GuidField)
            {
                return (!isNullable)
                        ? (AbstractField)new GuidField(property, name, field.Primary, field.Unique)
                        : (AbstractField)new NullableGuidField(property, name, field.Primary, field.Unique);
            }
            if(field is Attributes.IntegerField)
            {
                return (!isNullable)
                        ? (AbstractField)new IntegerField(property, name, field.Primary, field.Unique, field.AutoIncrement)
                        : (AbstractField)new NullableIntegerField(property, name, field.Primary, field.Unique, field.AutoIncrement);
            }
            if (field is Attributes.StringField)
            {
                return new StringField(property, name, field.Primary, field.Unique);
            }
            return null;
        }
    }
}
