using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Data.Constants;
using ConduitLLM.Core.Data.Interfaces;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.Core.Data
{
    /// <summary>
    /// Provides functionality for managing database connection strings including
    /// parsing, validation, and sanitization.
    /// </summary>
    public class ConnectionStringManager : IConnectionStringManager
    {
        private readonly ILogger<ConnectionStringManager>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionStringManager"/> class.
        /// </summary>
        /// <param name="logger">Optional logger for database connection operations.</param>
        public ConnectionStringManager(ILogger<ConnectionStringManager>? logger = null)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public (string ProviderName, string ConnectionStringValue) GetProviderAndConnectionString(Action<string>? logger = null)
        {
            return GetProviderAndConnectionString(null, logger);
        }

        /// <summary>
        /// Gets the database provider name and connection string based on environment configuration.
        /// </summary>
        /// <param name="serviceType">Optional service type to apply service-specific connection pool settings (e.g., "CoreAPI", "AdminAPI", "WebAdmin")</param>
        /// <param name="logger">Optional logger action for database connection operations.</param>
        /// <returns>A tuple containing the provider name and connection string.</returns>
        public (string ProviderName, string ConnectionStringValue) GetProviderAndConnectionString(string? serviceType, Action<string>? logger = null)
        {
            // Use either the provided logger action or our internal logger if available
            // lgtm [cs/cleartext-storage-of-sensitive-information]
            Action<string> logAction = logger ?? (message => _logger?.LogInformation(message));

            // Check for PostgreSQL DATABASE_URL
            var databaseUrl = Environment.GetEnvironmentVariable(DatabaseConstants.DATABASE_URL_ENV);
            if (string.IsNullOrWhiteSpace(databaseUrl))
            {
                var errorMsg = "DATABASE_URL environment variable is not set. PostgreSQL is required for Conduit.";
                logAction($"[DB] Error: {errorMsg}");
                throw new InvalidOperationException(errorMsg);
            }

            try
            {
                var connStr = ParsePostgresUrl(databaseUrl, serviceType);
                ValidateConnectionString(DatabaseConstants.POSTGRES_PROVIDER, connStr);
                logAction($"[DB] Using provider: {DatabaseConstants.POSTGRES_PROVIDER}, connection: {SanitizeConnectionString(connStr)}");
                return (DatabaseConstants.POSTGRES_PROVIDER, connStr);
            }
            catch (Exception)
            {
                const string errorMsg = "Invalid DATABASE_URL configuration. Use a PostgreSQL URI or Npgsql key/value connection string.";
                logAction($"[DB] Error: {errorMsg}");
                throw new InvalidOperationException(errorMsg);
            }
        }

        /// <inheritdoc/>
        public string ParsePostgresUrl(string postgresUrl)
        {
            return ParsePostgresUrl(postgresUrl, null);
        }

        /// <summary>
        /// Validates PostgreSQL connectivity with exponential backoff retry logic.
        /// </summary>
        /// <param name="connectionString">The PostgreSQL connection string to validate.</param>
        /// <param name="maxRetries">Maximum number of retry attempts (default: 5).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if connection is successful, false otherwise.</returns>
        public async Task<bool> ValidatePostgreSQLConnectivityAsync(
            string connectionString, 
            int maxRetries = 5, 
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger?.LogError("Connection string is null or empty");
                return false;
            }

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    
                    // Test the connection with a simple query
                    using var command = new NpgsqlCommand("SELECT 1", connection);
                    await command.ExecuteScalarAsync(cancellationToken);
                    
                    _logger?.LogInformation("PostgreSQL connection validated successfully");
                    return true;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    // Calculate exponential backoff: 1s, 2s, 4s, 8s, 16s
                    var delayMs = (int)Math.Pow(2, attempt) * 1000;
                    
                    _logger?.LogWarning(
                        "PostgreSQL connection attempt {Attempt}/{MaxRetries} failed. Retrying in {DelayMs}ms. Error: {Message}",
                        attempt + 1, maxRetries, delayMs, ex.Message);
                    
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, 
                        "PostgreSQL connection failed after {MaxRetries} attempts", 
                        maxRetries);
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Parses a PostgreSQL URI or Npgsql key/value string with service-specific defaults.
        /// </summary>
        /// <param name="postgresUrl">The PostgreSQL URL to parse (e.g., postgres://user:pass@host:port/database).</param>
        /// <param name="serviceType">Optional service type to apply service-specific connection pool settings (e.g., "CoreAPI", "AdminAPI", "WebAdmin")</param>
        /// <returns>A properly formatted PostgreSQL connection string.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public string ParsePostgresUrl(string postgresUrl, string? serviceType)
        {
            var (minPoolSize, maxPoolSize) = serviceType?.ToUpperInvariant() switch
            {
                "COREAPI" => (DatabaseConstants.CORE_API_MIN_POOL_SIZE, DatabaseConstants.CORE_API_MAX_POOL_SIZE),
                "ADMINAPI" => (DatabaseConstants.ADMIN_API_MIN_POOL_SIZE, DatabaseConstants.ADMIN_API_MAX_POOL_SIZE),
                "WEBADMIN" => (DatabaseConstants.WEBADMIN_MIN_POOL_SIZE, DatabaseConstants.WEBADMIN_MAX_POOL_SIZE),
                _ => (DatabaseConstants.MIN_POOL_SIZE, DatabaseConstants.MAX_POOL_SIZE)
            };
            var defaults = new NpgsqlConnectionStringBuilder
            {
                Pooling = DatabaseConstants.POOLING_ENABLED,
                MinPoolSize = minPoolSize,
                MaxPoolSize = maxPoolSize,
                ConnectionLifetime = DatabaseConstants.CONNECTION_LIFETIME_SECONDS,
                ConnectionIdleLifetime = DatabaseConstants.CONNECTION_IDLE_LIFETIME_SECONDS,
                IncludeErrorDetail = true
            };
            return PostgresConnectionStringParser.Parse(postgresUrl, defaults).ConnectionString;
        }

        /// <inheritdoc/>
        public void ValidateConnectionString(string providerName, string connectionStringValue)
        {
            if (string.IsNullOrEmpty(providerName))
            {
                throw new ArgumentNullException(nameof(providerName), "Provider name cannot be null or empty");
            }

            if (string.IsNullOrEmpty(connectionStringValue))
            {
                throw new ArgumentNullException(nameof(connectionStringValue), "Connection string cannot be null or empty");
            }

            if (!providerName.Equals(DatabaseConstants.POSTGRES_PROVIDER, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Only PostgreSQL is supported.",
                    nameof(providerName));
            }

            PostgresConnectionStringParser.Parse(connectionStringValue);
        }

        /// <inheritdoc/>
        public string SanitizeConnectionString(string connectionStringValue)
        {
            return PostgresConnectionStringParser.Sanitize(connectionStringValue);
        }

    }
}
