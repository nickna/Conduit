using ConduitLLM.Configuration.Services;

using Microsoft.Extensions.Logging;

using Xunit;

namespace ConduitLLM.Tests.Configuration.Services;

[Trait("Category", "Unit")]
[Trait("Component", "RedisUrlValidator")]
public class RedisUrlValidatorTests
{
    private const string Username = "synthetic-test-user";
    private const string Password = "synthetic-test-password";

    [Theory]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@localhost:bad-port")]
    [InlineData("rediss://synthetic-test-user:synthetic-test-password@localhost:bad-port")]
    [InlineData("redis://:synthetic-test-password@")]
    [InlineData("redis://synthetic-test-user@")]
    [InlineData("rediss://synthetic-test-user@")]
    [InlineData("redis://synthetic-test-user@localhost:bad-port")]
    [InlineData("rediss:/synthetic-test-user@")]
    [InlineData("rediss://synthetic-test-user:synthetic-test-password@localhost:")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@local host:6379")]
    [InlineData("rediss://synthetic-test-user:synthetic-test-password@localhost:6379/path")]
    [InlineData("synthetic-test-user:synthetic-test-password@localhost:6379")]
    [InlineData("redis://?query")]
    [InlineData("rediss://#fragment")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password%2Cssl%3Dfalse@localhost:6379")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@localhost:6379?unknownOption=synthetic-test-password")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@localhost:6379/1?defaultDatabase=2")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password%0A@localhost:6379")]
    [InlineData("localhost:6379,password=synthetic-test-password,user=synthetic-test-user,unknownOption=value")]
    public void ValidateAndLog_InvalidCredentialBearingUrl_OmitsInputFromCapturedLogs(string url)
    {
        var logger = new RecordingLogger();

        Assert.False(RedisUrlValidator.ValidateAndLog(url, logger, "SyntheticRepro"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        AssertCredentialFree(entry, url);
    }

    [Theory]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@localhost:6379")]
    [InlineData("rediss://:synthetic-test-password@localhost:6380")]
    [InlineData("redis://synthetic-test-user:synthetic-test-password@localhost:6379/2?syncTimeout=2345")]
    [InlineData("rediss://synthetic-test-user:synthetic-test-password@[::1]:6380/3?sslHost=cache.internal")]
    [InlineData("localhost:6379,user=synthetic-test-user,password=synthetic-test-password,ssl=true,defaultDatabase=2")]
    public void ValidateAndLog_ValidCredentialBearingUrl_OmitsInputFromCapturedLogs(string url)
    {
        var logger = new RecordingLogger();

        Assert.True(RedisUrlValidator.ValidateAndLog(url, logger, "SyntheticRepro"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        AssertCredentialFree(entry, url);
    }

    [Fact]
    public void ValidateAndLog_ExceptionContainingCredentials_LogsOnlyExceptionType()
    {
        const string url = "redis://synthetic-test-user:synthetic-test-password@localhost:6379";
        // The parser currently accepts this URL. Throw once from the success log to exercise
        // the validator's catch path with an exception that embeds credential-bearing input.
        var logger = new RecordingLogger(new InvalidOperationException($"Failed for {url}"));

        Assert.False(RedisUrlValidator.ValidateAndLog(url, logger, "SyntheticRepro"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        AssertCredentialFree(entry, url);
    }

    private static void AssertCredentialFree(LogEntry entry, string url)
    {
        Assert.Null(entry.Exception);
        foreach (var captured in new[] { entry.Message, entry.State })
        {
            Assert.DoesNotContain(url, captured);
            Assert.DoesNotContain(Username, captured);
            Assert.DoesNotContain(Password, captured);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, string State, Exception? Exception);

    private sealed class RecordingLogger(Exception? throwOnce = null) : ILogger
    {
        private Exception? _throwOnce = throwOnce;

        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (_throwOnce is { } error)
            {
                _throwOnce = null;
                throw error;
            }

            // Capture both rendered text and structured fields, since providers can persist
            // either representation independently of the formatted message.
            var fields = state is IEnumerable<KeyValuePair<string, object?>> structured
                ? string.Join(" | ", structured.Select(field => $"{field.Key}={field.Value}"))
                : state?.ToString() ?? string.Empty;
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), fields, exception));
        }
    }
}
