using System;
using Birko.Configuration;
using Birko.Data.Models;

namespace Birko.Data.SQL.Stores
{
    /// <summary>
    /// SQL-specific settings for database connection.
    /// Extends RemoteSettings with command and connection timeout configuration.
    /// </summary>
    public class SqlSettings : RemoteSettings, ILoadable<SqlSettings>
    {
        /// <summary>
        /// Gets or sets the command timeout in seconds. Default is 30.
        /// </summary>
        public int CommandTimeout { get; set; } = 30;

        /// <summary>
        /// Gets or sets the connection timeout in seconds. Default is 15.
        /// </summary>
        public int ConnectionTimeout { get; set; } = 15;

        public SqlSettings() : base() { }

        public SqlSettings(string location, string name, string? username = null, string? password = null, int port = 0, bool useSecure = false)
            : base(location, name, username ?? string.Empty, password ?? string.Empty, port, useSecure) { }

        /// <summary>
        /// Gets the provider-specific connection string from the current settings.
        /// Provider subclasses (MSSql, MySQL, PostgreSQL, TimescaleDB) override this.
        /// The base throws because a connection string cannot be composed without dialect knowledge —
        /// callers holding a non-provider-specific <see cref="SqlSettings"/> (e.g. a
        /// <c>SqlMigrationSettings</c>) shouldn't invoke this.
        /// </summary>
        public virtual string GetConnectionString()
            => throw new NotSupportedException(
                $"{GetType().Name} is not a provider-specific SqlSettings subclass " +
                 "(PostgreSqlSettings, MsSqlSettings, MySqlSettings, TimescaleDBSettings) " +
                 "and cannot produce a connection string.");

        public override string GetId()
        {
            return $"{Location}:{Port}:{Name}:{UserName}";
        }

        public void LoadFrom(SqlSettings data)
        {
            if (data != null)
            {
                base.LoadFrom((RemoteSettings)data);
                CommandTimeout = data.CommandTimeout;
                ConnectionTimeout = data.ConnectionTimeout;
            }
        }

        public override void LoadFrom(Birko.Configuration.Settings data)
        {
            if (data is SqlSettings sqlData)
            {
                LoadFrom(sqlData);
            }
            else
            {
                base.LoadFrom(data);
            }
        }
    }
}
