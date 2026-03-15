using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Birko.Rules;

namespace Birko.Data.SQL.Conditions;

/// <summary>
/// Converts Birko.Rules rule trees into Birko.Data.SQL Condition lists for dynamic SQL query building.
/// </summary>
public static class RuleConditionConverter
{
    /// <summary>
    /// Converts a single rule (leaf or group) into a list of SQL conditions.
    /// </summary>
    public static IEnumerable<Condition> ToConditions(IRule rule)
    {
        if (!rule.IsEnabled)
            return [];

        return rule switch
        {
            Rules.Rule leaf => [ConvertLeaf(leaf, isOr: false)],
            RuleGroup group => ConvertGroup(group),
            _ => []
        };
    }

    /// <summary>
    /// Converts all rules in a RuleSet into SQL conditions (AND-joined).
    /// </summary>
    public static IEnumerable<Condition> ToConditions(RuleSet ruleSet)
    {
        if (!ruleSet.IsEnabled)
            return [];

        return ruleSet.Rules
            .Where(r => r.IsEnabled)
            .SelectMany(ToConditions);
    }

    private static IEnumerable<Condition> ConvertGroup(RuleGroup group)
    {
        if (group.Rules.Count == 0)
            return [];

        var children = group.Rules
            .Where(r => r.IsEnabled)
            .SelectMany(ToConditions)
            .ToList();

        if (children.Count == 0)
            return [];

        if (group.Logic == LogicOperator.Or)
        {
            // Wrap as OR sub-condition
            return [group.IsNegated
                ? Condition.AndSubCondition(SetOr(children), isNot: true)
                : Condition.AndSubCondition(SetOr(children))];
        }

        // AND group — return as sub-condition to keep grouping
        if (group.IsNegated)
            return [Condition.AndSubCondition(children, isNot: true)];

        return [Condition.AndSubCondition(children)];
    }

    private static List<Condition> SetOr(List<Condition> conditions)
    {
        // Mark all conditions except the first as OR
        for (int i = 1; i < conditions.Count; i++)
        {
            conditions[i] = new Condition(
                conditions[i].Name,
                conditions[i].Values,
                conditions[i].Type,
                conditions[i].IsField,
                conditions[i].IsNot,
                isOr: true,
                conditions[i].SubConditions
            );
        }
        return conditions;
    }

    private static Condition ConvertLeaf(Rules.Rule rule, bool isOr)
    {
        var (condType, values) = MapOperator(rule);
        var isNot = rule.IsNegated;

        // Special handling for certain operators that map to IsNot
        if (rule.Operator == ComparisonOperator.NotEqual)
        {
            condType = ConditionType.Equal;
            isNot = !rule.IsNegated; // NotEqual = Equal + IsNot (double negation if already negated)
        }
        else if (rule.Operator == ComparisonOperator.NotContains)
        {
            condType = ConditionType.Like;
            isNot = !rule.IsNegated;
        }
        else if (rule.Operator == ComparisonOperator.NotIn)
        {
            condType = ConditionType.In;
            isNot = !rule.IsNegated;
        }
        else if (rule.Operator == ComparisonOperator.IsNotNull)
        {
            condType = ConditionType.IsNull;
            isNot = !rule.IsNegated;
        }

        if (isOr)
        {
            return new Condition(rule.Field, values, condType, false, isNot, true);
        }

        return new Condition(rule.Field, values, condType, false, isNot);
    }

    private static (ConditionType type, IEnumerable? values) MapOperator(Rules.Rule rule)
    {
        return rule.Operator switch
        {
            ComparisonOperator.Equal => (ConditionType.Equal, WrapValue(rule.Value)),
            ComparisonOperator.NotEqual => (ConditionType.Equal, WrapValue(rule.Value)),
            ComparisonOperator.GreaterThan => (ConditionType.Greather, WrapValue(rule.Value)),
            ComparisonOperator.GreaterThanOrEqual => (ConditionType.GreatherAndEqual, WrapValue(rule.Value)),
            ComparisonOperator.LessThan => (ConditionType.Less, WrapValue(rule.Value)),
            ComparisonOperator.LessThanOrEqual => (ConditionType.LessAndEqual, WrapValue(rule.Value)),
            ComparisonOperator.Between => (ConditionType.GreatherAndEqual, WrapValue(rule.Value)),
            ComparisonOperator.IsNull => (ConditionType.IsNull, null),
            ComparisonOperator.IsNotNull => (ConditionType.IsNull, null),
            ComparisonOperator.Contains => (ConditionType.Like, WrapValue(rule.Value)),
            ComparisonOperator.NotContains => (ConditionType.Like, WrapValue(rule.Value)),
            ComparisonOperator.StartsWith => (ConditionType.StartsWith, WrapValue(rule.Value)),
            ComparisonOperator.EndsWith => (ConditionType.EndsWith, WrapValue(rule.Value)),
            ComparisonOperator.Like => (ConditionType.Like, WrapValue(rule.Value)),
            ComparisonOperator.In => (ConditionType.In, rule.Value as IEnumerable ?? WrapValue(rule.Value)),
            ComparisonOperator.NotIn => (ConditionType.In, rule.Value as IEnumerable ?? WrapValue(rule.Value)),
            _ => (ConditionType.Equal, WrapValue(rule.Value))
        };
    }

    private static object[]? WrapValue(object? value)
    {
        return value is null ? null : [value];
    }
}
