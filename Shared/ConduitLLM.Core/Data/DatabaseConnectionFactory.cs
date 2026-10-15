using System.Data.Common;

using ConduitLLM.Core.Data.Constants;
using ConduitLLM.Core.Data.Interfaces;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.Core.Data
{
    /// <summary>
    /// Factory for creating database connections with proper pooling.
    /// </summary>
    public class DatabaseConnectionFactory : IDatabaseConnectionFactory
    {
        private readonly IConnectionStringManager _connectionStringManager;
        private readonly ILogger<DatabaseConnectionFactory> _logger;
        private readonly string _providerName;
        private readonly string _connectionString;
        private readonly NpgsqlDataSource? _dataSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseConnectionFactory"/> class.
        /// </summary>
        /// <param name="connectionStringManager">The connection string manager.</param>
        /// <param name="logger">The logger.</param>
        public DatabaseConnectionFactory(
            IConnectionStringManager connectionStringManager,
            ILogger<DatabaseConnectionFactory> logger)
        {
            _connectionStringManager = connectionStringManager ?? throw new ArgumentNullException(nameof(connectionStringManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Get provider and connection string once at initialization
            (_providerName, _connectionString) = _connectionStringManager.GetProviderAndConnectionString(
                // lgtm [cs/cleartext-storage-of-sensitive-information]
                msg => _logger.LogDebug(msg));

            // Validate the connection string
            _connectionStringManager.ValidateConnectionString(_providerName, _connectionString);
        }

        private DatabaseConnectionFactory(
            NpgsqlDataSource dataSource,
            IConnectionStringManager connectionStringManager,
            ILogger<DatabaseConnectionFactory> logger)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _connectionStringManager = connectionStringManager ?? throw new ArgumentNullException(nameof(connectionStringManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _providerName = DatabaseConstants.POSTGRES_PROVIDER;
            // The host already resolved and validated its service-specific configuration.
            // Re-resolving DATABASE_URL here would select the generic pool defaults.
            _connectionString = dataSource.ConnectionString;
        }

        /// <summary>
        /// Creates a factory that borrows a host-owned PostgreSQL data source.
        /// Disposing returned connections releases leases without disposing the shared pool.
        /// </summary>
        public static DatabaseConnectionFactory FromDataSource(
            NpgsqlDataSource dataSource,
            IConnectionStringManager connectionStringManager,
            ILogger<DatabaseConnectionFactory> logger) =>
            new(dataSource, connectionStringManager, logger);

        /// <inheritdoc/>
        public string ProviderName => _providerName;

        /// <inheritdoc/>
        public string GetSanitizedConnectionString()
        {
            return _connectionStringManager.SanitizeConnectionString(_connectionString);
        }

        /// <inheritdoc/>
        public DbConnection CreateConnection()
        {
            if (_providerName.ToLowerInvariant() != DatabaseConstants.POSTGRES_PROVIDER)
            {
                throw new InvalidOperationException($"Only PostgreSQL is supported. Invalid provider: {_providerName}");
            }

            var connection = _dataSource is null
                ? new NpgsqlConnection(_connectionString)
                : _dataSource.CreateConnection();
            _logger.LogDebug("Created PostgreSQL connection");
            return connection;
        }

        /// <inheritdoc/>
        public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var connection = CreateConnection();
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create connection for provider: {Provider}", _providerName);
                throw new InvalidOperationException($"Failed to create connection for provider: {_providerName}", ex);
            }
        }
    }
}
