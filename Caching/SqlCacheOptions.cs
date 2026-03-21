using System;

namespace Birko.Data.SQL.Caching
{
    /// <summary>
    /// Configuration options for SQL query caching.
    /// </summary>
    public class SqlCacheOptions
    {
        /// <summary>
        /// Gets or sets the default cache expiration duration.
        /// Defaults to 5 minutes.
        /// </summary>
        public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets whether caching is enabled.
        /// When disabled, the cached store delegates directly to the base store.
        /// Defaults to true.
        /// </summary>
        public bool Enabled { get; set; } = true;
    }
}
