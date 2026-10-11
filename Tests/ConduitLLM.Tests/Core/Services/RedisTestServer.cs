using System.Diagnostics;

using StackExchange.Redis;

using Xunit;

namespace ConduitLLM.Tests.Core.Services;

/// <summary>
/// Shared gate for tests that need a real Redis. Enforces the difference between
/// "no Redis here, skip" and "Redis was promised and is missing, fail" — the rate limiter's
/// Lua path had no executed coverage precisely because every Redis-less run reported green.
/// </summary>
/// <remarks>
/// <para>
/// CI supplies Redis as a service container and sets <c>TEST_REDIS_CONNECTION</c>. When that
/// variable is present, Redis is treated as mandatory and an unreachable server fails the test
/// rather than skipping it. On a developer machine with the variable unset, the tests skip.
/// </para>
/// <para>
/// <see cref="RedisRequirementTests"/> closes the remaining hole: it fails if CI itself ever
/// stops providing Redis, so the suite cannot quietly stop covering the limiter.
/// </para>
/// </remarks>
public static class RedisTestServer
{
    private const string ConnectionVariable = "TEST_REDIS_CONNECTION";
    // 127.0.0.1 rather than "localhost": on Windows that name resolves to ::1 first, and a
    // server listening only on IPv4 is then reported as unreachable.
    private const string DefaultConnection = "127.0.0.1:6379,allowAdmin=true";
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(30);
    private const int CleanupBatchSize = 128;

    private static readonly Lazy<ConnectionMultiplexer?> Connection = new(Connect, isThreadSafe: true);

    /// <summary>True when the environment declares that a Redis server must be reachable.</summary>
    public static bool IsRequired => !string.IsNullOrWhiteSpace(ConfiguredConnection);

    /// <summary>True when this process is running inside GitHub Actions.</summary>
    public static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    public static string? ConfiguredConnection => Environment.GetEnvironmentVariable(ConnectionVariable);

    public static bool IsAvailable => Connection.Value is { IsConnected: true };

    /// <summary>
    /// Returns a connected multiplexer, skipping the calling test when Redis is optional and
    /// absent. Throws when Redis was declared mandatory but is unreachable.
    /// </summary>
    public static IConnectionMultiplexer Require()
    {
        if (IsAvailable)
        {
            return Connection.Value!;
        }

        if (IsRequired)
        {
            throw new InvalidOperationException(
                $"{ConnectionVariable}={ConfiguredConnection} declares Redis as mandatory for this run, " +
                "but no server could be reached. The rate limiter's Lua path is not being covered.");
        }

        Skip.If(true,
            $"Redis is not available. Set {ConnectionVariable} (CI does) or run a local server to execute these tests.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Prefix for keys created by a single test, so parallel tests and repeat runs cannot
    /// observe each other's windows.
    /// </summary>
    public static string NewKeyPrefix(string name) => $"test:{name}:{Guid.NewGuid():N}:";

    /// <summary>
    /// Best-effort cleanup of one test's unique prefix, including namespace-wrapped keys and
    /// :sum companions. Cleanup diagnostics never replace the test's assertion failure.
    /// </summary>
    public static async Task CleanupAsync(string keyPrefix, Action<string>? writeDiagnostic = null)
    {
        ValidateCleanupPrefix(keyPrefix);
        if (!IsAvailable)
        {
            // Require() and RedisRequirementTests still enforce mandatory real Redis coverage.
            if (IsRequired)
            {
                (writeDiagnostic ?? Console.Error.WriteLine)(
                    $"Redis test cleanup skipped for prefix '{keyPrefix}': mandatory Redis is unavailable.");
            }
            return;
        }

        await CleanupAsync(Connection.Value!, keyPrefix,
            writeDiagnostic ?? Console.Error.WriteLine, CleanupBudget);
    }

    internal static async Task CleanupAsync(IConnectionMultiplexer multiplexer, string keyPrefix,
        Action<string> writeDiagnostic, TimeSpan budget)
    {
        ValidateCleanupPrefix(keyPrefix);
        using var cancellation = new CancellationTokenSource(budget);
        var elapsed = Stopwatch.StartNew();
        long deleted = 0;
        var stage = "finding primary endpoints";
        var database = multiplexer.GetDatabase();

        try
        {
            // Product key builders put namespaces before the test prefix (rate:vk:test:...,
            // signalr:vk:test:...). Escape Redis glob metacharacters and verify literal
            // ownership again before deletion so parallel tests cannot lose their keys.
            var pattern = $"*{EscapeGlob(keyPrefix)}*";
            foreach (var endpoint in multiplexer.GetEndPoints())
            {
                var server = multiplexer.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica)
                {
                    continue;
                }

                var batch = new List<RedisKey>(CleanupBatchSize);
                stage = $"scanning {endpoint}";
                await using var keys = server.KeysAsync(database: database.Database,
                    pattern: pattern, pageSize: CleanupBatchSize).GetAsyncEnumerator(cancellation.Token);
                while (await keys.MoveNextAsync().AsTask().WaitAsync(cancellation.Token))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!keys.Current.ToString().Contains(keyPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    batch.Add(keys.Current);
                    if (batch.Count == CleanupBatchSize)
                    {
                        stage = $"deleting a batch on {endpoint}";
                        deleted += await database.KeyDeleteAsync(batch.ToArray()).WaitAsync(cancellation.Token);
                        batch.Clear();
                        stage = $"scanning {endpoint}";
                    }
                }

                if (batch.Count > 0)
                {
                    stage = $"deleting the final batch on {endpoint}";
                    deleted += await database.KeyDeleteAsync(batch.ToArray()).WaitAsync(cancellation.Token);
                }
            }
        }
        catch (Exception exception)
        {
            var reason = cancellation.IsCancellationRequested
                ? $"the {budget.TotalSeconds:0.###}s cleanup budget was exhausted"
                : exception.GetType().Name;
            writeDiagnostic($"Redis test cleanup incomplete for prefix '{keyPrefix}' while {stage}; " +
                $"deleted {deleted} keys in {elapsed.Elapsed.TotalSeconds:0.###}s: {reason}. " +
                "This is a teardown diagnostic; product assertions and mandatory Redis checks remain authoritative.");
        }
    }

    private static void ValidateCleanupPrefix(string keyPrefix)
    {
        var parts = keyPrefix?.Split(':');
        if (parts is not { Length: >= 4 } || parts[0] != "test" || parts[^1] != string.Empty ||
            !Guid.TryParseExact(parts[^2], "N", out _))
        {
            throw new ArgumentException("Cleanup requires a unique prefix from NewKeyPrefix.", nameof(keyPrefix));
        }
    }

    private static string EscapeGlob(string value) => value.Replace("\\", "\\\\")
        .Replace("*", "\\*").Replace("?", "\\?").Replace("[", "\\[").Replace("]", "\\]");

    private static ConnectionMultiplexer? Connect()
    {
        try
        {
            var options = ConfigurationOptions.Parse(ConfiguredConnection ?? DefaultConnection);
            options.ConnectTimeout = 2000;
            options.SyncTimeout = 2000;
            // Async SCAN and batched deletes need more room under CI load than sync setup.
            // The overall cleanup budget still bounds the entire teardown operation.
            options.AsyncTimeout = 10000;
            options.AbortOnConnectFail = false;
            options.AllowAdmin = true;

            var multiplexer = ConnectionMultiplexer.Connect(options);
            multiplexer.GetDatabase().Ping();
            return multiplexer;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Guards the guard: if CI ever stops supplying Redis, this fails instead of letting every
/// Redis-backed limiter test silently skip.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "TestInfrastructure")]
public class RedisRequirementTests
{
    [Fact]
    public void ContinuousIntegration_MustProvideRedisForLimiterCoverage()
    {
        if (!RedisTestServer.IsCi)
        {
            return;
        }

        Assert.True(
            RedisTestServer.IsRequired,
            "CI must set TEST_REDIS_CONNECTION so the Redis-backed rate limiter tests execute " +
            "instead of skipping. See the redis service in .github/workflows/ci.yml.");

        Assert.True(
            RedisTestServer.IsAvailable,
            $"TEST_REDIS_CONNECTION={RedisTestServer.ConfiguredConnection} is set but no server answered.");
    }
}
