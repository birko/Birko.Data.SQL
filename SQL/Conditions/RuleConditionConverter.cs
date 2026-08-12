using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Birko.Rules;

namespace Birko.Data.SQL.Conditions;

/// <summary>
/// Converts Birko.Rules rule trees into Birko.Data.SQL Condition lists for dynamic SQL query building.
/// <para>
/// <b>SH-H023 (TASK-111).</b> Every overload here now checks <c>rule.Field</c> before it becomes a
/// <see cref="Condition.Name"/>, because that name is interpolated straight into <c>CommandText</c> by
/// every condition strategy. Prefer the overloads that take an entity type: they resolve the field against
/// table metadata, so a <c>[NamedField]</c>-remapped property filters on the right column and no caller
/// text can reach the statement. The type-less overloads have no metadata to resolve against and fall back
/// to requiring a bare column identifier — enough to close the injection, not enough to fix a remapping.
/// See <see cref="DataBase.ResolveRuleField"/> for the measured payloads.
/// </para>
/// </summary>
public static class RuleConditionConverter
{
    /// <summary>
    /// Converts a single rule (leaf or group) into a list of SQL conditions, resolving each
    /// <c>rule.Field</c> against <typeparamref name="T"/>'s table metadata.
    /// </summary>
    /// <exception cref="ArgumentException">A rule's field names no column of <typeparamref name="T"/>.</exception>
    public static IEnumerable<Condition> ToConditions<T>(IRule rule) => ToConditions(typeof(T), rule);

    /// <summary>
    /// Converts all rules in a RuleSet into SQL conditions (AND-joined), resolving each <c>rule.Field</c>
    /// against <typeparamref name="T"/>'s table metadata.
    /// </summary>
    /// <exception cref="ArgumentException">A rule's field names no column of <typeparamref name="T"/>.</exception>
    public static IEnumerable<Condition> ToConditions<T>(RuleSet ruleSet) => ToConditions(typeof(T), ruleSet);

    /// <summary>
    /// Converts a single rule (leaf or group) into a list of SQL conditions, resolving each
    /// <c>rule.Field</c> against <paramref name="entityType"/>'s table metadata.
    /// </summary>
    /// <exception cref="ArgumentException">A rule's field names no column of the entity.</exception>
    public static IEnumerable<Condition> ToConditions(Type entityType, IRule rule)
    {
        if (entityType == null)
            throw new ArgumentNullException(nameof(entityType));

        return Convert(rule, entityType);
    }

    /// <summary>
    /// Converts all rules in a RuleSet into SQL conditions (AND-joined), resolving each <c>rule.Field</c>
    /// against <paramref name="entityType"/>'s table metadata.
    /// </summary>
    /// <exception cref="ArgumentException">A rule's field names no column of the entity.</exception>
    public static IEnumerable<Condition> ToConditions(Type entityType, RuleSet ruleSet)
    {
        if (entityType == null)
            throw new ArgumentNullException(nameof(entityType));

        return ConvertSet(ruleSet, entityType);
    }

    /// <summary>
    /// Converts a single rule (leaf or group) into a list of SQL conditions.
    /// <para>
    /// Without an entity type the field cannot be resolved, so it must already be a bare column
    /// identifier; anything else throws. Prefer <see cref="ToConditions{T}(IRule)"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">A rule's field is not a bare column identifier.</exception>
    public static IEnumerable<Condition> ToConditions(IRule rule) => Convert(rule, entityType: null);

    /// <summary>
    /// Converts all rules in a RuleSet into SQL conditions (AND-joined).
    /// <para>
    /// Without an entity type the fields cannot be resolved, so each must already be a bare column
    /// identifier; anything else throws. Prefer <see cref="ToConditions{T}(RuleSet)"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">A rule's field is not a bare column identifier.</exception>
    public static IEnumerable<Condition> ToConditions(RuleSet ruleSet) => ConvertSet(ruleSet, entityType: null);

    private static IEnumerable<Condition> Convert(IRule rule, Type? entityType)
    {
        if (!rule.IsEnabled)
            return [];

        return rule switch
        {
            Rules.Rule leaf => [ConvertLeaf(leaf, isOr: false, entityType)],
            RuleGroup group => ConvertGroup(group, entityType),
            _ => []
        };
    }

    private static IEnumerable<Condition> ConvertSet(RuleSet ruleSet, Type? entityType)
    {
        if (!ruleSet.IsEnabled)
            return [];

        // Materialised, not lazy: the field guard must run when the caller asks for the conditions, not
        // whenever the statement builder happens to enumerate them. A deferred throw surfaces from inside
        // the connector, where it reads as a database fault rather than a bad rule.
        return ruleSet.Rules
            .Where(r => r.IsEnabled)
            .SelectMany(r => Convert(r, entityType))
            .ToList();
    }

    private static IEnumerable<Condition> ConvertGroup(RuleGroup group, Type? entityType)
    {
        if (group.Rules.Count == 0)
            return [];

        var children = group.Rules
            .Where(r => r.IsEnabled)
            .SelectMany(r => Convert(r, entityType))
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

    private static Condition ConvertLeaf(Rules.Rule rule, bool isOr, Type? entityType)
    {
        // SH-H023 (TASK-111). The single place a rule.Field becomes a Condition.Name, and therefore the
        // single place to check it — Condition.Name is interpolated raw by every strategy. With an entity
        // type this resolves against table metadata (the resolution IS the whitelist, and it fixes
        // [NamedField] remapping); without one it can only insist on a bare identifier.
        var name = entityType != null
            ? DataBase.ResolveRuleField(entityType, rule.Field)
            : DataBase.ValidateRuleFieldIdentifier(rule.Field);

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

        // Dead today — the only call site passes isOr: false and SetOr applies OR-ness afterwards — but it
        // carried the identical defect, so it takes the resolved name rather than being left as a trap for
        // whoever revives it.
        if (isOr)
        {
            return new Condition(name, values, condType, false, isNot, true);
        }

        return new Condition(name, values, condType, false, isNot);
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
