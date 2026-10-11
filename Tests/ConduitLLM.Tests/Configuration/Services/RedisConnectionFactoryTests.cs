using System.Net;

using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Configuration.Utilities;

using Microsoft.Extensions.Logging;

using Moq;

using StackExchange.Redis;

using Xunit;

namespace ConduitLLM.Tests.Configuration.Services;

public class RedisConnectionFactoryTests
{
    private const string Password = "synthetic-test-password";
    private const string User = "synthetic-test-user";

    [Theory]
    [InlineData("localhost:6379,password=synthetic-test-password,user=synthetic-test-user")]
    [InlineData("localhost:6379,PASSWORD=synthetic-test-password,USER=synthetic-test-user")]
    [InlineData("localhost:6379,PaSsWoRd=synthetic-test-password,UsEr=synthetic-test-user")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@localhost:6379")]
    [InlineData("rediss://synthetic-test-user:synthetic-test-password@localhost:6380")]
    public async Task GetConnectionAsync_Success_LogsEndpointsWithoutCredentials(string input)
    {
        var logger = new RecordingLogger();
        var connection = new Mock<IConnectionMultiplexer>();
        using var factory = new StubRedisConnectionFactory(logger, _ => Task.FromResult(connection.Object));

        var result = await factory.GetConnectionAsync(ToConnectionString(input));

        Assert.Same(connection.Object, result);
        Assert.Equal(Password, factory.Configuration.Password);
        Assert.Equal(User, factory.Configuration.User);
        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("Attempting to connect to Redis: localhost:"));
        Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("Successfully connected to Redis at localhost:"));
        AssertLogsExcludeCredentials(logger);
    }

    [Theory]
    [InlineData("localhost:6379,password=synthetic-test-password,user=synthetic-test-user")]
    [InlineData("localhost:6379,PASSWORD=synthetic-test-password,USER=synthetic-test-user")]
    [InlineData("localhost:6379,PaSsWoRd=synthetic-test-password,UsEr=synthetic-test-user")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@localhost:6379")]
    [InlineData("rediss://synthetic-test-user:synthetic-test-password@localhost:6380")]
    public async Task GetConnectionAsync_FailureAndDisposal_LogsNoCredentials(string input)
    {
        var logger = new RecordingLogger();
        var error = new RedisConnectionException(ConnectionFailureType.AuthenticationFailure, input);
        var factory = new StubRedisConnectionFactory(logger,
            _ => Task.FromException<IConnectionMultiplexer>(error));

        var thrown = await Assert.ThrowsAsync<RedisConnectionException>(
            () => factory.GetConnectionAsync(ToConnectionString(input)));
        factory.Dispose();

        Assert.Same(error, thrown);
        Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("Attempting to connect to Redis: localhost:"));
        Assert.Contains(logger.Entries, entry => entry.Message.StartsWith("Error disposing Redis connection."));
        AssertLogsExcludeCredentials(logger);
    }

    [Fact]
    public async Task GetConnectionAsync_InvalidOptionsAndDisposal_LogsNoCredentials()
    {
        var logger = new RecordingLogger();
        using var factory = new StubRedisConnectionFactory(logger,
            _ => throw new InvalidOperationException("Must not connect with invalid options"));

        await Assert.ThrowsAsync<ArgumentException>(() => factory.GetConnectionAsync(
            $"localhost:6379,password={Password},user={User},invalid-{Password}={User}"));

        Assert.Null(factory.Configuration);
        AssertLogsExcludeCredentials(logger);
    }

    [Fact]
    public async Task GetConnectionAsync_ConnectionEvents_LogsOnlySafeDiagnosticMetadata()
    {
        var logger = new RecordingLogger();
        var connection = new Mock<IConnectionMultiplexer>();
        using var factory = new StubRedisConnectionFactory(logger, _ => Task.FromResult(connection.Object));
        await factory.GetConnectionAsync($"localhost:6379,password={Password},user={User}");
        var endpoint = new DnsEndPoint("localhost", 6379);
        var error = new RedisConnectionException(ConnectionFailureType.AuthenticationFailure,
            $"Authentication rejected for {User} with {Password}");
        var failure = new ConnectionFailedEventArgs(connection.Object, endpoint, ConnectionType.Interactive,
            ConnectionFailureType.AuthenticationFailure, error, $"{User}:{Password}");

        connection.Raise(value => value.ConnectionFailed += null, failure);
        connection.Raise(value => value.ConnectionRestored += null, failure);
        connection.Raise(value => value.ErrorMessage += null,
            new RedisErrorEventArgs(connection.Object, endpoint, $"{User}:{Password}"));

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Failure: AuthenticationFailure"));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Redis connection restored. Endpoint: localhost:6379"));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Redis reported an error. Endpoint: localhost:6379"));
        AssertLogsExcludeCredentials(logger);
    }

    [Fact]
    public async Task GetConnectionAsync_EndpointWithUserInfo_LogsNoCredentials()
    {
        var logger = new RecordingLogger();
        var connection = new Mock<IConnectionMultiplexer>();
        using var factory = new StubRedisConnectionFactory(logger, _ => Task.FromResult(connection.Object));
        await factory.GetConnectionAsync($"redis://{User}:{Password}@localhost:6379");
        var endpoint = new DnsEndPoint($"redis://{User}:{Password}@localhost", 6379);
        var failure = new ConnectionFailedEventArgs(connection.Object, endpoint, ConnectionType.Interactive,
            ConnectionFailureType.UnableToConnect, null, null);

        connection.Raise(value => value.ConnectionFailed += null, failure);
        connection.Raise(value => value.ConnectionRestored += null, failure);
        connection.Raise(value => value.ErrorMessage += null,
            new RedisErrorEventArgs(connection.Object, endpoint, "synthetic error"));

        Assert.All(logger.Entries, entry => Assert.Contains("(unavailable)", entry.Message));
        AssertLogsExcludeCredentials(logger);
    }

    [Fact]
    public async Task GetConnectionAsync_MultipleEndpointsWithoutCredentials_PreservesDiagnosticsAndPooling()
    {
        var logger = new RecordingLogger();
        var connection = new Mock<IConnectionMultiplexer>();
        var calls = 0;
        using var factory = new StubRedisConnectionFactory(logger, _ =>
        {
            calls++;
            return Task.FromResult(connection.Object);
        });
        const string input = "localhost:6379,127.0.0.1:6380,[::1]:6381,name=synthetic-test-password";

        await factory.GetConnectionAsync(input);
        await factory.GetConnectionAsync(input);

        Assert.Equal(1, calls);
        Assert.All(logger.Entries, entry => Assert.Contains("localhost:6379, 127.0.0.1:6380, [::1]:6381", entry.Message));
        AssertLogsExcludeCredentials(logger);
    }

    private static string ToConnectionString(string input) =>
        input.Contains("://") ? RedisUrlParser.ParseRedisUrl(input) : input;

    private static void AssertLogsExcludeCredentials(RecordingLogger logger)
    {
        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(Password, entry.Message);
            Assert.DoesNotContain(User, entry.Message);
            Assert.Null(entry.Exception);
            Assert.All(entry.Values, value =>
            {
                Assert.DoesNotContain(Password, value);
                Assert.DoesNotContain(User, value);
            });
        });
    }

    private sealed class StubRedisConnectionFactory(
        ILogger<RedisConnectionFactory> logger,
        Func<ConfigurationOptions, Task<IConnectionMultiplexer>> connect)
        : RedisConnectionFactory(Microsoft.Extensions.Options.Options.Create(new CacheOptions()), logger)
    {
        public ConfigurationOptions Configuration { get; private set; }

        protected override Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions options)
        {
            Configuration = options;
            return connect(options);
        }
    }

    private sealed record LogEntry(string Message, string[] Values, Exception Exception);

    private sealed class RecordingLogger : ILogger<RedisConnectionFactory>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter)
        {
            var values = ((IEnumerable<KeyValuePair<string, object>>)state)
                .Select(pair => pair.Value?.ToString() ?? "").ToArray();
            Entries.Add(new LogEntry(formatter(state, exception), values, exception));
        }
    }
}
