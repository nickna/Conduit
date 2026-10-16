using System.Data.Common;
using System.Runtime.ExceptionServices;

using ConduitLLM.Core.Data.Interfaces;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Data.Health
{
    /// <summary>
    /// Health check for database connectivity.
    /// </summary>
    public class DatabaseHealthCheck : IHealthCheck
    {
        private readonly IDatabaseConnectionFactory _connectionFactory;
        private readonly ILogger<DatabaseHealthCheck> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseHealthCheck"/> class.
        /// </summary>
        /// <param name="connectionFactory">The database connection factory.</param>
        /// <param name="logger">The logger.</param>
        public DatabaseHealthCheck(
            IDatabaseConnectionFactory connectionFactory,
            ILogger<DatabaseHealthCheck> logger)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DbConnection? connection = null;
            DbCommand? command = null;
            Exception? failure = null;
            try
            {
                _logger.LogDebug("Checking database health for provider: {Provider}", _connectionFactory.ProviderName);

                // Create and open a connection to test database connectivity
                connection = await _connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Perform a simple query to verify the connection works
                command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // Always release both resources asynchronously. A cleanup failure
                // must not replace the original query error or caller cancellation.
                failure = await DisposeResourceAsync(command, failure).ConfigureAwait(false);
                failure = await DisposeResourceAsync(connection, failure).ConfigureAwait(false);
            }

            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            if (failure is not null)
            {
                _logger.LogError(failure, "Database health check failed for provider: {Provider}", _connectionFactory.ProviderName);
                return HealthCheckResult.Unhealthy(
                    $"Database connection failed. Provider: {_connectionFactory.ProviderName}",
                    failure);
            }

            return HealthCheckResult.Healthy($"Database connection is healthy. Provider: {_connectionFactory.ProviderName}");
        }

        private async ValueTask<Exception?> DisposeResourceAsync(IAsyncDisposable? resource, Exception? failure)
        {
            if (resource is not null)
            {
                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException,
                        "Database health check cleanup failed for provider: {Provider}", _connectionFactory.ProviderName);
                    failure ??= cleanupException;
                }
            }

            return failure;
        }
    }
}
