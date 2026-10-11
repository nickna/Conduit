using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;

using ConduitLLM.Core.Constants;

using Moq;

using StackExchange.Redis;

using Xunit;

namespace ConduitLLM.Tests.Core.Services;

[Trait("Category", "Unit")]
[Trait("Component", "TestInfrastructure")]
public class RedisTestServerCleanupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("test:")]
    public async Task CleanupAsync_WithoutUniqueTestPrefix_RejectsDeletion(string prefix)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => RedisTestServer.CleanupAsync(prefix));
    }

    [Fact]
    public async Task CleanupAsync_AsyncScan_BatchesOnlyLiterallyOwnedKeys()
    {
        var prefix = RedisTestServer.NewKeyPrefix("batch");
        var owned = Enumerable.Range(0, 257).Select(i => (RedisKey)$"rate:vk:{prefix}{i}").ToArray();
        var (connection, server, database) = NewConnection();
        server.Setup(s => s.KeysAsync(0, It.IsAny<RedisValue>(), 128, 0, 0, CommandFlags.None))
            .Returns(EnumerateKeys(owned.Append((RedisKey)"another-test:key")));
        var deleted = new List<RedisKey>();
        database.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None))
            .Returns((RedisKey[] batch, CommandFlags _) =>
            {
                Assert.InRange(batch.Length, 1, 128);
                deleted.AddRange(batch);
                return Task.FromResult((long)batch.Length);
            });
        var diagnostics = new List<string>();

        await RedisTestServer.CleanupAsync(connection.Object, prefix, diagnostics.Add, TimeSpan.FromSeconds(5));

        Assert.Empty(diagnostics);
        Assert.Equal(owned, deleted);
        database.Verify(db => db.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None), Times.Exactly(3));
        Assert.DoesNotContain(server.Invocations, invocation => invocation.Method.Name == "Keys");
    }

    [Fact]
    public async Task CleanupAsync_ScanExceedsBudget_ReportsTeardownDiagnosticAndReturns()
    {
        var prefix = RedisTestServer.NewKeyPrefix("budget");
        var (connection, server, _) = NewConnection();
        server.Setup(s => s.KeysAsync(0, It.IsAny<RedisValue>(), 128, 0, 0, CommandFlags.None))
            .Returns(WaitForCancellation());
        var diagnostics = new List<string>();
        var elapsed = Stopwatch.StartNew();

        await RedisTestServer.CleanupAsync(connection.Object, prefix, diagnostics.Add, TimeSpan.FromMilliseconds(50));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains(prefix, diagnostic);
        Assert.Contains("scanning", diagnostic);
        Assert.Contains("budget was exhausted", diagnostic);
    }

    [Fact]
    public async Task CleanupAsync_DeleteFails_ReportsTeardownDiagnosticAndReturns()
    {
        var prefix = RedisTestServer.NewKeyPrefix("failure");
        var (connection, server, database) = NewConnection();
        server.Setup(s => s.KeysAsync(0, It.IsAny<RedisValue>(), 128, 0, 0, CommandFlags.None))
            .Returns(EnumerateKeys(new[] { (RedisKey)(prefix + "key") }));
        database.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "synthetic failure"));
        var diagnostics = new List<string>();

        await RedisTestServer.CleanupAsync(connection.Object, prefix, diagnostics.Add, TimeSpan.FromSeconds(5));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("deleting", diagnostic);
        Assert.Contains(nameof(RedisConnectionException), diagnostic);
        Assert.Contains("deleted 0 keys", diagnostic);
    }

    [SkippableFact]
    public async Task CleanupAsync_RealRedis_RemovesWrappedKeysAndKeepsOtherTestPrefixes()
    {
        var database = RedisTestServer.Require().GetDatabase();
        var prefix = RedisTestServer.NewKeyPrefix("cleanup*[x]?\\literal");
        var otherPrefix = RedisTestServer.NewKeyPrefix("cleanup-other");
        var owned = Enumerable.Range(0, 260).Select(i => (RedisKey)(prefix + i)).Concat(new[]
        {
            (RedisKey)RedisKeys.RateLimit.VirtualKeyRpm(prefix + "window"),
            (RedisKey)RedisKeys.RateLimit.WindowSum(RedisKeys.RateLimit.VirtualKeyRpm(prefix + "window")),
            (RedisKey)RedisKeys.SignalRRateLimit.Connections(prefix + "connection")
        }).ToArray();
        RedisKey unrelated = RedisKeys.RateLimit.VirtualKeyRpm(otherPrefix + "window");
        var diagnostics = new List<string>();

        try
        {
            await Task.WhenAll(owned.Append(unrelated).Select(key =>
                database.StringSetAsync(key, "sentinel", TimeSpan.FromMinutes(1))));

            await RedisTestServer.CleanupAsync(prefix, diagnostics.Add);

            Assert.Empty(diagnostics);
            Assert.Equal(0, await database.KeyExistsAsync(owned));
            Assert.Equal("sentinel", (string)await database.StringGetAsync(unrelated));
        }
        finally
        {
            // Delete exact test-owned keys even if the cleanup regression assertion fails.
            await database.KeyDeleteAsync(owned.Append(unrelated).ToArray());
        }
    }

    private static (Mock<IConnectionMultiplexer>, Mock<IServer>, Mock<IDatabase>) NewConnection()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        var server = new Mock<IServer>();
        var database = new Mock<IDatabase>();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 6379);
        connection.Setup(c => c.GetEndPoints(false)).Returns(new EndPoint[] { endpoint });
        connection.Setup(c => c.GetServer(endpoint, null)).Returns(server.Object);
        connection.Setup(c => c.GetDatabase(-1, null)).Returns(database.Object);
        server.SetupGet(s => s.IsConnected).Returns(true);
        return (connection, server, database);
    }

    private static async IAsyncEnumerable<RedisKey> EnumerateKeys(IEnumerable<RedisKey> keys)
    {
        await Task.Yield();
        foreach (var key in keys)
        {
            yield return key;
        }
    }

    private static async IAsyncEnumerable<RedisKey> WaitForCancellation(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
