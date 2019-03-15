using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Birko.Data.SQL.Condition
{
    public enum ConditionType
    {
        Equal,
        Less,
        Greather,
        LessAndEqual,
        GreatherAndEqual,
        IsNull,
        Like,
        StartsWith,
        EndsWith,
        In,
    }

    public class Condition
    {
        public string Name { get; set; }
        public IEnumerable Values { get; set; }
        public bool IsField { get; set; } = false;
        public ConditionType Type { get; set; } = ConditionType.Equal;
        public IEnumerable<Condition> SubConditions { get; set; } = null;
        public bool IsOr { get; set; } = false;
        public bool IsNot { get; set; } = false;

        public Condition(string name, IEnumerable values, ConditionType type = ConditionType.Equal, bool isField = false, bool isNot = false, bool isOr = false, IEnumerable<Condition> subConditions = null)
        {
            Name = name;
            Type = type;
            Values = values;
            IsField = isField;
            IsOr = isOr;
            IsNot = isNot;
            SubConditions = subConditions;
        }

        public static Condition Create(string name, IEnumerable values, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(name, values, type, false, isNot);
        }

        public static Condition CreateValue(string name, object value, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(name, new[] { value }, type, false, isNot);
        }

        public static Condition And(string name, IEnumerable values, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return Create(name, values, type, isNot);
        }

        public static Condition AndValue(string name, object value, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return CreateValue(name, value, type, isNot);
        }

        public static Condition AndField(string name, string field, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(name, new[] { field }, type, true, isNot);
        }

        public static Condition AndSubCondition(IEnumerable<Condition> subConditions = null, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(null, null, type, false, isNot, false, subConditions);
        }

        public static Condition Or<T>(string name, IEnumerable<object> values, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(name, values, type, false, isNot, true);
        }

        public static Condition OrValue<T>(string name, object value, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(name, new[] { value }, type, false, isNot, true);
        }

        public static Condition OrField(string name, string field, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(name, new[] { field }, type, true, isNot, true);
        }

        public static Condition OrSubCondition(IEnumerable<Condition> subConditions = null, ConditionType type = ConditionType.Equal, bool isNot = false)
        {
            return new Condition(null, null, type, false, isNot, true, subConditions);
        }
    }
}
