using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Birko.Data.SQL.Conditions
{
    public enum JoinType
    {
        Cross,
        Inner,
        LeftOuter
    }

    public class Join
    {
        public string Left { get; set; }
        public string Right { get; set; }
        public IEnumerable<Condition> Conditions { get; set; }
        public JoinType JoinType { get; set; }

        public Join(string left, string right, JoinType joinType = JoinType.Cross,  IEnumerable<Condition> conditions = null)
        {
            Left = left;
            Right = right;
            AddConditions(conditions);
            JoinType = joinType;
        }

        public static Join Create(string left, string right, JoinType joinType = JoinType.Cross, IEnumerable <Condition> conditions = null)
        {
            return new Join(left, right, joinType, conditions);
        }

        public static Join Create(string left, string right, Condition condition, JoinType joinType = JoinType.Cross)
        {
            return new Join(left, right, joinType, new[] { condition });
        }

        public Join AddConditions(IEnumerable<Condition> conditions)
        {
            if (conditions != null && conditions.Any())
            {
                foreach(var condition in conditions)
                {
                    AddCondition(condition);
                }
            }
            return this;
        }

        public Join AddCondition(Condition condition)
        {
            if (condition != null)
            {
                Conditions = (Conditions != null) ? Conditions.Concat(new[] { condition }) : new[] { condition };
            }
            return this;
        }
    }
}
