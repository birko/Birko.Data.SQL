using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Birko.Data.SQL.Connectors
{
    public abstract partial class AbstractConnector
    {
        public long SelectCount(Type type, LambdaExpression? expr)
        {
            return SelectCount(new[] { type }, expr);
        }

        public long SelectCount(IEnumerable<Type> types, LambdaExpression? expr = null)
        {
            return SelectCount(types, (expr != null) ? DataBase.ParseConditionExpression(expr) : null);
        }

        public long SelectCount(Type type, IEnumerable<Conditions.Condition>? conditions = null)
        {
            return SelectCount(new[] { type }, conditions);
        }

        public long SelectCount(IEnumerable<Type> types, IEnumerable<Conditions.Condition>? conditions = null)
        {
            return (types != null) ? SelectCount(types.Select(x => DataBase.LoadTable(x)), conditions) : 0;
        }

        public long SelectCount(IEnumerable<Tables.Table> tables, IEnumerable<Conditions.Condition>? conditions = null)
        {
            return (tables != null) ? SelectCount(tables.Where(x => x != null).Select(x => x.Name), conditions) : 0;
        }

        public long SelectCount(IEnumerable<string> tableNames, IEnumerable<Conditions.Condition>? conditions = null)
        {
            return SelectCount(tableNames, null, conditions);
        }

        public long SelectCount(IEnumerable<string> tableNames, IEnumerable<Conditions.Join>? joinconditions = null, IEnumerable<Conditions.Condition>? conditions = null)
        {
            long count = 0;
            if (tableNames != null && tableNames.Any() && tableNames.Any(x => !string.IsNullOrEmpty(x)))
            {
                try
                {
                    DoCommand((command) => {
                        command = CreateSelectCommand(
                            command,
                            tableNames.Where(x => !string.IsNullOrEmpty(x)).Distinct(),
                            new Dictionary<int, string>()
                            {
                                { 0, "count(*) as count"}
                            },
                            joinconditions, conditions);
                    }, (command) =>
                    {
                        var data = command.ExecuteScalar();
                        count = data != null ? Convert.ToInt64(data) : 0;
                    });
                }
                catch (Exception ex) when (IsMissingTableExceptionChain(ex))
                {
                    // TASK-285 — THE COUNT OF A TABLE THAT DOES NOT EXIST IS 0, exactly as the list of its
                    // rows is empty.
                    //
                    // A reader already answers this condition with `yield break` (AbstractConnector.cs:452,
                    // async :378) — IsMissingTableException exists, in its own words, so "a reader can yield
                    // an empty result instead of faulting". A COUNT is a read. Before this, it was the one
                    // read that faulted instead, so the same missing table produced an empty list on one
                    // route and a 500 on another, decided purely by the statement's shape.
                    //
                    // ⚠ SCOPED TO COUNTS, AND DELIBERATELY NOT TO EnsureSchemaAndReport. That method throws
                    // because TASK-277 measured WRITES being silently discarded — Create returning a real
                    // Guid against a table that was never created. Writes must keep reporting; widening
                    // this catch to them reopens that defect.
                    //
                    // ⚠ CHAIN, not the direct predicate: EnsureSchemaAndReport rethrows as
                    // `new Exception(commandText, ex)`, so the outer message is the SQL and a message-only
                    // check silently never matches (see IsMissingTableExceptionChain).
                    //
                    // Raised by Symbio TASK-602, where this surfaced as an intermittent 500 on a fresh
                    // deployment. It does NOT explain why the table was missing — that question stays open
                    // there — it makes the answer correct either way.
                    return 0;
                }
            }
            return count;
        }
    }
}
