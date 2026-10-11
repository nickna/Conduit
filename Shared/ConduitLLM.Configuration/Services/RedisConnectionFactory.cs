using System.Collections.Concurrent;
using System.Net;

using ConduitLLM.Configuration.Options;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace ConduitLLM.Configuration.Services
{
    /// <summary>
    /// Factory for creating and managing Redis connections
    /// </summary>
    /// <remarks>
    /// This class handles the creation and lifecycle of Redis connections.
    /// It provides connection pooling and handles reconnection logic.
    /// </remarks>
    public class RedisConnectionFactory : IDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<IConnectionMultiplexer>>> _connections = new();
        private readonly ILogger<RedisConnectionFactory> _logger;
        private readonly CacheOptions _options;

        /// <summary>
        /// Creates a new instance of RedisConnectionFactory
        /// </summary>
        /// <param name="options">Cache configuration options</param>
        /// <param name="logger">Logger instance</param>
        public RedisConnectionFactory(
            IOptions<CacheOptions> options,
            ILogger<RedisConnectionFactory> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets a Redis connection multiplexer for the specified connection string
        /// </summary>
        /// <param name="connectionString">Redis connection string</param>
        /// <returns>A connection multiplexer</returns>
        public virtual async Task<IConnectionMultiplexer> GetConnectionAsync(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                connectionString = _options.RedisConnectionString;
            }

            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException("Redis connection string is not configured");
            }

            var connection = _connections.GetOrAdd(connectionString, CreateConnection);
            return await connection.Value;
        }

        /// <summary>
        /// Gets the default Redis connection multiplexer
        /// </summary>
        /// <returns>A connection multiplexer</returns>
        public virtual Task<IConnectionMultiplexer> GetConnectionAsync()
        {
            return GetConnectionAsync(_options.RedisConnectionString);
        }

        /// <summary>
        /// Connects to Redis using the parsed configuration.
        /// </summary>
        protected virtual async Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions options)
        {
            return await ConnectionMultiplexer.ConnectAsync(options);
        }

        private Lazy<Task<IConnectionMultiplexer>> CreateConnection(string connectionString)
        {
            return new Lazy<Task<IConnectionMultiplexer>>(async () =>
            {
                try
                {
                    var configOptions = ConfigurationOptions.Parse(connectionString);
                    configOptions.AbortOnConnectFail = false;
                    configOptions.ConnectTimeout = 5000; // 5 second timeout
                    configOptions.SyncTimeout = 5000; // 5 second sync timeout

                    // Enable admin mode for Redis INFO command used by metrics collection
                    configOptions.AllowAdmin = true;

                    // Log only validated endpoints, never credentials or other connection options.
                    var endpoints = string.Join(", ", configOptions.EndPoints.Select(FormatEndpointForLogging));
                    _logger.LogInformation("Attempting to connect to Redis: {Endpoints}", endpoints);

                    var connection = await ConnectAsync(configOptions);

                    // Exception and server message text can contain credentials echoed by Redis.
                    // Retain endpoint and failure metadata without forwarding arbitrary text.
                    connection.ConnectionFailed += (sender, args) =>
                    {
                        _logger.LogError(
                            "Redis connection failed. Endpoint: {Endpoint}, Failure: {FailureType}, Exception type: {ExceptionType}",
                            FormatEndpointForLogging(args.EndPoint), args.FailureType,
                            args.Exception?.GetType().Name ?? "Unknown");
                    };

                    connection.ConnectionRestored += (sender, args) =>
                    {
                        _logger.LogInformation("Redis connection restored. Endpoint: {Endpoint}",
                            FormatEndpointForLogging(args.EndPoint));
                    };

                    connection.ErrorMessage += (sender, args) =>
                    {
                        _logger.LogWarning("Redis reported an error. Endpoint: {Endpoint}",
                            FormatEndpointForLogging(args.EndPoint));
                    };

                    _logger.LogInformation("Successfully connected to Redis at {Endpoints}", endpoints);

                    return connection;
                }
                catch (Exception ex)
                {
                    // Parsing and connection errors may include the original configuration.
                    _logger.LogError("Failed to create Redis connection. Exception type: {ExceptionType}",
                        ex.GetType().Name);
                    throw;
                }
            });
        }

        private static string FormatEndpointForLogging(EndPoint? endpoint)
        {
            return endpoint switch
            {
                IPEndPoint ipEndpoint => ipEndpoint.ToString(),
                DnsEndPoint dnsEndpoint when Uri.CheckHostName(dnsEndpoint.Host) != UriHostNameType.Unknown
                    => $"{dnsEndpoint.Host}:{dnsEndpoint.Port}",
                // Malformed configurations can put URI user-info in a DNS endpoint.
                // Do not fall back to EndPoint.ToString() for unrecognized values.
                _ => "(unavailable)"
            };
        }

        /// <summary>
        /// Disposes of all Redis connections
        /// </summary>
        public void Dispose()
        {
            foreach (var connection in _connections.Values)
            {
                if (connection.IsValueCreated)
                {
                    try
                    {
                        connection.Value.Result?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Error disposing Redis connection. Exception type: {ExceptionType}",
                            ex.GetType().Name);
                    }
                }
            }

            _connections.Clear();
        }
    }
}
