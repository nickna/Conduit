using System.Data.Common;

namespace ConduitLLM.Core.Data.Interfaces
{
    /// <summary>
    /// Provides factory methods for creating database connections.
    /// </summary>
    public interface IDatabaseConnectionFactory
    {
        /// <summary>
        /// Gets the provider name for the current database.
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Gets the connection string for the current database.
        /// </summary>
        /// <returns>A sanitized connection string (passwords masked).</returns>
        string GetSanitizedConnectionString();

        /// <summary>
        /// Creates a new database connection.
        /// </summary>
        /// <returns>A DbConnection instance appropriate for the current provider.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the connection cannot be created.</exception>
        /// <example>
        /// ```csharp
        /// public async Task<int> GetDatabaseSize()
        /// {
        ///     var factory = serviceProvider.GetRequiredService<IDatabaseConnectionFactory>();
        ///     
        ///     using var connection = factory.CreateConnection();
        ///     await connection.OpenAsync();
        ///     
        ///     using var command = connection.CreateCommand();
        ///     if (factory.ProviderName == "postgres")
        ///     {
        ///         command.CommandText = "SELECT pg_database_size(current_database());";
        ///     }
        ///     else if (factory.ProviderName == "sqlite")
        ///     {
        ///         command.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();";
        ///     }
        ///     else
        ///     {
        ///         throw new NotSupportedException($"Provider {factory.ProviderName} not supported for size calculation");
        ///     }
        ///     
        ///     var result = await command.ExecuteScalarAsync();
        ///     return Convert.ToInt32(result);
        /// }
        /// ```
        /// </example>
        DbConnection CreateConnection();

        /// <summary>
        /// Creates and opens a new database connection asynchronously.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task containing an open connection owned by the caller, which must dispose it after use.</returns>
        /// <remarks>If opening fails or is canceled, the factory disposes the connection before propagating the failure.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the connection cannot be created.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the caller cancels the operation.</exception>
        Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
    }
}
