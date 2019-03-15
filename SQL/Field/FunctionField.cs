using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace Birko.Data.SQL.Field
{
    public class FunctionField : AbstractField
    {
        public object[] Parameters { get; set; }
        public FunctionField(System.Reflection.PropertyInfo property, string name, object[] parameters, DbType type = DbType.String, bool notNull = false)
            : base(property, name, type, false, notNull, false, false)
        {
            Parameters = parameters;
        }

        public static FunctionField CreateFunctionAggregateField(System.Reflection.PropertyInfo property, Attribute.AggregateField field, AbstractField tablefield)
        {
            FunctionField functionField = null;
            if (field is Attribute.AvgField)
            {
                functionField = (tablefield.IsNotNull)
                    ? new DecimalFunction(property, "AVG", new[] { tablefield.Name })
                    : new NullableDecimalFunction(property, "AVG", new[] { tablefield.Name });
            }
            if (field is Attribute.CountField)
            {
                functionField = (tablefield.IsNotNull)
                    ? new IntegerFunction(property, "COUNT", new[] { tablefield.Name })
                    : new NullableIntegerFunction(property, "COUNT", new[] { tablefield.Name });
            }
            else if (field is Attribute.MaxField)
            {
                if (tablefield is IntegerField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new IntegerFunction(property, "MAX", new[] { tablefield.Name })
                    : new NullableIntegerFunction(property, "MAX", new[] { tablefield.Name });
                }
                else if (tablefield is DecimalField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new DecimalFunction(property, "MAX", new[] { tablefield.Name })
                    : new NullableDecimalFunction(property, "MAX", new[] { tablefield.Name });
                }
                else if (tablefield is DecimalField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new BooleanFunction(property, "MAX", new[] { tablefield.Name })
                    : new NullableBooleanFunction(property, "MAX", new[] { tablefield.Name });
                }
                else if (tablefield is DecimalField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new DateTimeFunction(property, "MAX", new[] { tablefield.Name })
                    : new NullableDateTimeFunction(property, "MAX", new[] { tablefield.Name });
                }
                else if (tablefield is GuidField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new GuidFunction(property, "MAX", new[] { tablefield.Name })
                    : new NullableGuidFunction(property, "MAX", new[] { tablefield.Name });
                }
                else
                {
                    functionField = new StringFunction(property, "MAX", new[] { tablefield.Name });
                }
            }
            else if (field is Attribute.MinField)
            {
                if (tablefield is IntegerField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new IntegerFunction(property, "MIN", new[] { tablefield.Name })
                    : new NullableIntegerFunction(property, "MIN", new[] { tablefield.Name });
                }
                else if (tablefield is DecimalField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new DecimalFunction(property, "MIN", new[] { tablefield.Name })
                    : new NullableDecimalFunction(property, "MIN", new[] { tablefield.Name });
                }
                else if (tablefield is DecimalField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new BooleanFunction(property, "MIN", new[] { tablefield.Name })
                    : new NullableBooleanFunction(property, "MIN", new[] { tablefield.Name });
                }
                else if (tablefield is DecimalField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new DateTimeFunction(property, "MIN", new[] { tablefield.Name })
                    : new NullableDateTimeFunction(property, "MIN", new[] { tablefield.Name });
                }
                else if (tablefield is GuidField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new GuidFunction(property, "MIN", new[] { tablefield.Name })
                    : new NullableGuidFunction(property, "MIN", new[] { tablefield.Name });
                }
                else
                {
                    functionField = new StringFunction(property, "MIN", new[] { tablefield.Name });
                }
            }
            else if (field is Attribute.SumField)
            {
                if (tablefield is IntegerField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new IntegerFunction(property, "SUM", new[] { tablefield.Name })
                    : new NullableIntegerFunction(property, "SUM", new[] { tablefield.Name });
                }
                else if (tablefield is DecimalField)
                {
                    functionField = (tablefield.IsNotNull)
                    ? new DecimalFunction(property, "SUM", new[] { tablefield.Name })
                    : new NullableDecimalFunction(property, "SUM", new[] { tablefield.Name });
                }
            }
            if (functionField != null)
            {
                functionField.IsAggregate = true;
            }
            return functionField;
        }
    }
}
