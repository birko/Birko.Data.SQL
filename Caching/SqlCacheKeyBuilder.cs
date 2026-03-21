using System;
using System.Security.Cryptography;
using System.Text;

namespace Birko.Data.SQL.Caching
{
    /// <summary>
    /// Builds deterministic cache keys for SQL store queries.
    /// Keys follow the format: sql:{table}:{filterHash}:{orderHash}:{limit}:{offset}
    /// </summary>
    public static class SqlCacheKeyBuilder
    {
        private const string Prefix = "sql";

        /// <summary>
        /// Builds a cache key from the query components.
        /// </summary>
        /// <param name="tableName">The SQL table name.</param>
        /// <param name="filterString">String representation of the filter expression, or null.</param>
        /// <param name="orderString">String representation of the order clause, or null.</param>
        /// <param name="limit">Optional limit value.</param>
        /// <param name="offset">Optional offset value.</param>
        /// <returns>A deterministic cache key string.</returns>
        public static string BuildKey(string tableName, string? filterString, string? orderString, int? limit, int? offset)
        {
            var filterHash = string.IsNullOrEmpty(filterString) ? "_" : ComputeHash(filterString!);
            var orderHash = string.IsNullOrEmpty(orderString) ? "_" : ComputeHash(orderString!);
            var limitPart = limit?.ToString() ?? "_";
            var offsetPart = offset?.ToString() ?? "_";

            return $"{Prefix}:{tableName}:{filterHash}:{orderHash}:{limitPart}:{offsetPart}";
        }

        /// <summary>
        /// Gets the table-level cache key prefix for invalidation.
        /// All cache keys for a given table start with this prefix.
        /// </summary>
        /// <param name="tableName">The SQL table name.</param>
        /// <returns>The prefix string used for bulk invalidation.</returns>
        public static string GetTablePrefix(string tableName)
        {
            return $"{Prefix}:{tableName}:";
        }

        private static string ComputeHash(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            // Use first 12 bytes (16 hex chars) for a compact but collision-resistant key
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
