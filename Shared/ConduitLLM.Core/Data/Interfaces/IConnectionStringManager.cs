namespace ConduitLLM.Core.Data.Interfaces
{
    /// <summary>
    /// Manages database connection strings including parsing, validation, and sanitization.
    /// </summary>
    public interface IConnectionStringManager
    {
        /// <summary>
        /// Gets the database provider and connection string based on environment configuration.
        /// </summary>
        /// <param name="logger">Optional logger action for connection details.</param>
        /// <returns>A tuple containing the provider name and connection string.</returns>
        /// <example>
        /// ```csharp
        /// var connectionManager = serviceProvider.GetRequiredService<IConnectionStringManager>();
        /// var (providerName, connectionStringValue) = connectionManager.GetProviderAndConnectionString(
        ///     logMessage => _logger.LogInformation(logMessage)
        /// );
        /// 
        /// _logger.LogInformation($"Using {providerName} database with connection: {connectionStringValue}");
        /// ```
        /// </example>
        (string ProviderName, string ConnectionStringValue) GetProviderAndConnectionString(Action<string>? logger = null);

        /// <summary>
        /// Parses a PostgreSQL URI or Npgsql key/value string into a connection string.
        /// </summary>
        /// <param name="postgresUrl">The PostgreSQL URL to parse (e.g., "postgresql://user:password@host:port/database").</param>
        /// <returns>A formatted connection string compatible with Npgsql.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        /// <example>
        /// ```csharp
        /// // Example PostgreSQL URL
        /// var url = "postgresql://myuser:mypassword@localhost:5432/mydatabase?sslmode=require";
        /// 
        /// // Convert to standard connection string
        /// var connectionString = connectionManager.ParsePostgresUrl(url);
        /// // Result: "Host=localhost;Port=5432;Database=mydatabase;Username=myuser;Password=mypassword;sslmode=require"
        /// ```
        /// </example>
        string ParsePostgresUrl(string postgresUrl);

        /// <summary>
        /// Validates a connection string for the specified provider.
        /// </summary>
        /// <param name="providerName">The database provider (e.g., "postgres", "sqlite").</param>
        /// <param name="connectionStringValue">The connection string to validate.</param>
        /// <exception cref="ArgumentNullException">Thrown when providerName or connectionStringValue is null.</exception>
        /// <exception cref="ArgumentException">Thrown when providerName is not supported.</exception>
        /// <exception cref="InvalidOperationException">Thrown when connection string validation fails.</exception>
        /// <example>
        /// ```csharp
        /// // Validate a PostgreSQL connection string
        /// try {
        ///     connectionManager.ValidateConnectionString(
        ///         "postgres", 
        ///         "Host=myserver;Database=mydb;Username=myuser;Password=mypassword"
        ///     );
        ///     // Connection string is valid
        /// }
        /// catch (InvalidOperationException ex) {
        ///     _logger.LogError(ex, "Invalid connection string");
        /// }
        /// ```
        /// </example>
        void ValidateConnectionString(string providerName, string connectionStringValue);

        /// <summary>
        /// Sanitizes a connection string by masking sensitive information.
        /// </summary>
        /// <param name="connectionStringValue">The connection string to sanitize.</param>
        /// <returns>A sanitized connection string safe for logging.</returns>
        /// <example>
        /// ```csharp
        /// var connectionString = "Host=myserver;Database=mydb;Username=myuser;Password=s3cr3t!";
        /// var sanitized = connectionManager.SanitizeConnectionString(connectionString);
        /// // Result: "Host=myserver;Database=mydb;Username=myuser;Password=*****"
        /// ```
        /// </example>
        string SanitizeConnectionString(string connectionStringValue);

        /// <summary>
        /// Validates PostgreSQL connectivity with exponential backoff retry logic.
        /// </summary>
        /// <param name="connectionString">The PostgreSQL connection string to validate.</param>
        /// <param name="maxRetries">Maximum number of retry attempts (default: 5).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if connection is successful, false otherwise.</returns>
        /// <example>
        /// ```csharp
        /// // Validate PostgreSQL connectivity with retries
        /// var isConnected = await connectionManager.ValidatePostgreSQLConnectivityAsync(
        ///     connectionString,
        ///     maxRetries: 5,
        ///     cancellationToken: cts.Token
        /// );
        /// 
        /// if (!isConnected) {
        ///     throw new InvalidOperationException("Cannot connect to PostgreSQL after multiple attempts");
        /// }
        /// ```
        /// </example>
        Task<bool> ValidatePostgreSQLConnectivityAsync(
            string connectionString, 
            int maxRetries = 5, 
            CancellationToken cancellationToken = default);
    }
}
