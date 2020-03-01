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

        public static AbstractField CreateAbstractField(System.Reflection.PropertyInfo property, Attributes.Field[] fields = null)
        {
            var isNullable = IsNullable(property.PropertyType);
            string name = property.Name;
            bool primary = false;
            bool unique = false;
            bool autoincrement = false;
            int? scale = null;
            int? precision = null;

            if (fields != null && fields.Any())
            {
                foreach (var field in fields.Where(x => x != null))
                {
                    if (field is Attributes.NamedField namedfield)
                    {
                        name = !string.IsNullOrEmpty(namedfield.Name) ? namedfield.Name : name;
                    }

                    if (field is Attributes.PrimaryField)
                    {
                        primary = true;
                    }
                    if (field is Attributes.UniqueField)
                    {
                        unique = true;
                    }
                    if (field is Attributes.IncrementField)
                    {
                        autoincrement = true;
                    }
                    if (field is Attributes.PrecisionField precisionField)
                    {
                        precision = precisionField.Precision;
                    }
                    if (field is Attributes.ScaleField scaleField)
                    {
                        scale = scaleField.Scale;
                    }
                }

            }

            if (property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
            {
                return (!isNullable)
                        ? (AbstractField)new BooleanField(property, name, primary, unique)
                        : (AbstractField)new NullableBooleanField(property, name, primary, unique);
            }
            if (property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?))
            {
                return (!isNullable)
                        ? (AbstractField)new DateTimeField(property, name, primary, unique)
                        : (AbstractField)new NullableDateTimeField(property, name, primary, unique);
            }

            if (property.PropertyType == typeof(decimal) || property.PropertyType == typeof(decimal?))
            {
                return (!isNullable)
                        ? (AbstractField)new DecimalField(property, name, primary, unique, autoincrement, precision, scale)
                        : (AbstractField)new NullableDecimalField(property, name, primary, unique, autoincrement, precision, scale);
            }
            if (property.PropertyType == typeof(Guid) || property.PropertyType == typeof(Guid?))
            {
                return (!isNullable)
                        ? (AbstractField)new GuidField(property, name, primary, unique)
                        : (AbstractField)new NullableGuidField(property, name, primary, unique);
            }
            if(property.PropertyType == typeof(int) || property.PropertyType == typeof(int?))
            {
                return (!isNullable)
                        ? (AbstractField)new IntegerField(property, name, primary, unique, autoincrement)
                        : (AbstractField)new NullableIntegerField(property, name, primary, unique, autoincrement);
            }
            if (property.PropertyType == typeof(char))
            {
                return new CharField(property, name, primary, unique, 1);
            }

            if (property.PropertyType == typeof(string))
            {
                if (precision != null && precision > 0)
                {
                    return new CharField(property, name, primary, unique, precision);
                }
                else
                {
                    return new StringField(property, name, primary, unique);
                }
            }

            throw new Exceptions.FieldAttributeException("No field attributes in type");
        }
    }
}
