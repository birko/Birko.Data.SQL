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
        public string Name { get; set; } = null!;
        public DbType Type { get; set; } = DbType.String;
        public bool IsPrimary { get; set; } = false;
        public bool IsUnique { get; set; } = false;
        public bool IsNotNull { get; set; } = false;
        public bool IsAutoincrement { get; set; } = false;
        public bool IsAggregate { get; set; } = false;
        public System.Reflection.PropertyInfo Property { get; set; } = null!;
        public Tables.Table Table { get; set; } = null!;

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
                            string.Join(",", ((this as Fields.FunctionField)?.Parameters?.Select(x => string.Format("{0}{1}", (withName ? Table.Name + "." : string.Empty), x))) ?? new string[0]))
                        : (withName ? Table.Name + "." : string.Empty) + Name;
        }

        public virtual object? Write(object value)
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

        public static AbstractField? CreateAbstractField(System.Reflection.PropertyInfo property, Birko.Data.SQL.Attributes.Field[]? fields = null)
        {
            // Skip properties marked with [IgnoreField] or [NotMapped]
            if (fields != null && fields.Any(f => f is Birko.Data.SQL.Attributes.IgnoreField))
                return null;
            if (property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute), true).Any())
                return null;

            // An indexer (this[...]) is enumerated by GetProperties like any other public instance
            // property, but it has no single value to store and Property.GetValue(obj, null) cannot even
            // read it. It is structurally incapable of being a column, so it is skipped rather than
            // reported: the unmapped-type throw below is about types the mapper does not cover yet, and
            // firing it here would reject a model for something no mapping could ever fix.
            if (property.GetIndexParameters().Length > 0)
                return null;

            var isNullable = IsNullable(property.PropertyType);
            string name = property.Name;
            bool primary = false;
            bool unique = false;
            bool autoincrement = false;
            bool required = false;
            int? scale = null;
            int? precision = null;
            int? maxLength = null;

            // Read Birko.Data.SQL.Attributes
            if (fields != null && fields.Any())
            {
                foreach (var field in fields.Where(x => x != null))
                {
                    if (field is Birko.Data.SQL.Attributes.NamedField namedfield)
                    {
                        name = !string.IsNullOrEmpty(namedfield.Name) ? namedfield.Name : name;
                    }

                    if (field is Birko.Data.SQL.Attributes.PrimaryField)
                    {
                        primary = true;
                    }
                    if (field is Birko.Data.SQL.Attributes.UniqueField)
                    {
                        unique = true;
                    }
                    if (field is Birko.Data.SQL.Attributes.IncrementField)
                    {
                        autoincrement = true;
                    }
                    if (field is Birko.Data.SQL.Attributes.RequiredField)
                    {
                        required = true;
                    }
                    if (field is Birko.Data.SQL.Attributes.MaxLengthField maxLengthField)
                    {
                        maxLength = maxLengthField.MaxLength;
                    }
                    if (field is Birko.Data.SQL.Attributes.PrecisionField precisionField)
                    {
                        precision = precisionField.Precision;
                    }
                    if (field is Birko.Data.SQL.Attributes.ScaleField scaleField)
                    {
                        scale = scaleField.Scale;
                    }
                }
            }

            // Read System.ComponentModel.DataAnnotations attributes (alongside Birko attributes)
            // Birko attributes take precedence where both are specified
            var birkoNameOverride = fields != null && fields.Any(f => f is Birko.Data.SQL.Attributes.NamedField nf && !string.IsNullOrEmpty(nf.Name));
            var dataAnnotations = property.GetCustomAttributes(true);

            foreach (var attr in dataAnnotations)
            {
                if (!birkoNameOverride && attr is System.ComponentModel.DataAnnotations.Schema.ColumnAttribute columnAttr)
                {
                    if (!string.IsNullOrEmpty(columnAttr.Name))
                    {
                        name = columnAttr.Name;
                    }
                }
                if (attr is System.ComponentModel.DataAnnotations.KeyAttribute)
                {
                    primary = true;
                }
                if (attr is System.ComponentModel.DataAnnotations.RequiredAttribute)
                {
                    required = true;
                }
                if (attr is System.ComponentModel.DataAnnotations.MaxLengthAttribute maxLenAttr)
                {
                    if (maxLength == null)
                    {
                        maxLength = maxLenAttr.Length;
                    }
                }
                if (attr is System.ComponentModel.DataAnnotations.StringLengthAttribute strLenAttr)
                {
                    if (maxLength == null)
                    {
                        maxLength = strLenAttr.MaximumLength;
                    }
                }
                if (attr is System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedAttribute dbGenAttr)
                {
                    if (dbGenAttr.DatabaseGeneratedOption == System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Identity)
                    {
                        autoincrement = true;
                    }
                }
            }

            // [RequiredField] overrides C# nullability — forces NOT NULL even for nullable types
            var effectiveNotNull = !isNullable || required;

            if (property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new BooleanField(property, name, primary, unique)
                        : (AbstractField)new NullableBooleanField(property, name, primary, unique);
            }
            if (property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new DateTimeField(property, name, primary, unique)
                        : (AbstractField)new NullableDateTimeField(property, name, primary, unique);
            }

            // SH-H037 (TASK-197) / Symbio TASK-361 — TimeOnly was one more BCL value type with no arm here. While an
            // unmapped type was silently skipped that only lost the column; once the fallthrough started
            // throwing, it took out every route on the owning entity, because the throw happens at table
            // load rather than on the query that touches the column. Stored as fixed-width HH:mm:ss text —
            // see TimeOnlyField for why text rather than DbType.Time, and why the width has to be fixed.
            if (property.PropertyType == typeof(TimeOnly) || property.PropertyType == typeof(TimeOnly?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new TimeOnlyField(property, name, primary, unique)
                        : (AbstractField)new NullableTimeOnlyField(property, name, primary, unique);
            }

            if (property.PropertyType == typeof(decimal) || property.PropertyType == typeof(decimal?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new DecimalField(property, name, primary, unique, autoincrement, precision, scale)
                        : (AbstractField)new NullableDecimalField(property, name, primary, unique, autoincrement, precision, scale);
            }
            if (property.PropertyType == typeof(Guid) || property.PropertyType == typeof(Guid?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new GuidField(property, name, primary, unique)
                        : (AbstractField)new NullableGuidField(property, name, primary, unique);
            }
            if(property.PropertyType == typeof(int) || property.PropertyType == typeof(int?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new IntegerField(property, name, primary, unique, autoincrement)
                        : (AbstractField)new NullableIntegerField(property, name, primary, unique, autoincrement);
            }
            if (property.PropertyType == typeof(long) || property.PropertyType == typeof(long?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new LongField(property, name, primary, unique, autoincrement)
                        : (AbstractField)new NullableLongField(property, name, primary, unique, autoincrement);
            }
            if (property.PropertyType == typeof(short) || property.PropertyType == typeof(short?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new ShortField(property, name, primary, unique, autoincrement)
                        : (AbstractField)new NullableShortField(property, name, primary, unique, autoincrement);
            }
            if (property.PropertyType == typeof(double) || property.PropertyType == typeof(double?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new DoubleField(property, name, primary, unique)
                        : (AbstractField)new NullableDoubleField(property, name, primary, unique);
            }
            if (property.PropertyType == typeof(float) || property.PropertyType == typeof(float?))
            {
                return (effectiveNotNull)
                        ? (AbstractField)new FloatField(property, name, primary, unique)
                        : (AbstractField)new NullableFloatField(property, name, primary, unique);
            }
            if (property.PropertyType == typeof(byte[]))
            {
                var binaryField = new BinaryField(property, name, primary, unique);
                if (required)
                {
                    binaryField.IsNotNull = true;
                }
                return binaryField;
            }
            if (property.PropertyType == typeof(char))
            {
                return new CharField(property, name, primary, unique, 1);
            }

            if (property.PropertyType == typeof(string))
            {
                // MaxLengthField takes priority, fall back to PrecisionField for backwards compat
                var length = (maxLength != null && maxLength > 0) ? maxLength : precision;

                AbstractField stringField;
                if (length != null && length > 0)
                {
                    stringField = new CharField(property, name, primary, unique, length);
                }
                else
                {
                    stringField = new StringField(property, name, primary, unique);
                }

                if (required)
                {
                    stringField.IsNotNull = true;
                }

                return stringField;
            }

            // Unknown property type — check if it's an enum (store as int)
            var underlyingType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (underlyingType.IsEnum)
            {
                return (effectiveNotNull)
                        ? (AbstractField)new IntegerField(property, name, primary, unique, autoincrement)
                        : (AbstractField)new NullableIntegerField(property, name, primary, unique, autoincrement);
            }

            // No arm matched. Historically this returned null and LoadField turned that into an empty
            // field set, so the property got no column, was never written and was never read — silent
            // write-side data loss with no exception and no log entry (SH-H037). That silence is the
            // defect, not the missing arm: it means the NEXT unmapped type repeats the bug verbatim.
            //
            // Fail at table load instead, naming the property and its type. Silence is not a design; an
            // opt-out is, and two already exist and are honoured at the top of this method —
            // [IgnoreField] and [NotMapped].
            throw new Exceptions.FieldAttributeException(
                $"{property.DeclaringType?.FullName ?? property.ReflectedType?.FullName}.{property.Name}: "
                + $"type '{property.PropertyType.FullName}' has no SQL column mapping. "
                + "Map it to a supported type, or mark the property [IgnoreField] / [NotMapped] to exclude "
                + "it from the table deliberately.");
        }
    }
}
