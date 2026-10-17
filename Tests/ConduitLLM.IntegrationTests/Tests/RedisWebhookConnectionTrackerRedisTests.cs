using System.Text.Json;

using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "SignalR")]
public sealed class RedisWebhookConnectionTrackerRedisTests(
    FunctionCredentialRedisFixture fixture, ITestOutputHelper output)
    : IClassFixture<FunctionCredentialRedisFixture>
{
    [Theory]
    [InlineData("final-unsubscribe")]
    [InlineData("active-subscriptions")]
    [InlineData("empty-registration")]
    public async Task DisconnectRemovesOwnedKeysAndPreservesOtherConnections(string scenario)
    {
        var owner = Guid.NewGuid().ToString("N");
        var connection = $"issue1683-{owner}-target";
        var foreign = $"issue1683-{owner}-foreign";
        var sharedUrl = $"https://{owner}.invalid/shared";
        var privateUrl = $"https://{owner}.invalid/private";
        RedisKey connectionKey = RedisKeys.WebhookConnection.ConnectionWebhooks(connection);
        RedisKey timestampKey = RedisKeys.WebhookConnection.ConnectionTimestamp(connection);
        RedisKey foreignKey = RedisKeys.WebhookConnection.ConnectionWebhooks(foreign);
        RedisKey foreignTimestampKey = RedisKeys.WebhookConnection.ConnectionTimestamp(foreign);
        RedisKey sharedKey = ReverseKey(sharedUrl);
        RedisKey privateKey = ReverseKey(privateUrl);
        RedisKey[] keys = [connectionKey, timestampKey, foreignKey, foreignTimestampKey, sharedKey, privateKey];
        var options = ConfigurationOptions.Parse(await fixture.GetConnectionStringAsync());
        options.ConnectTimeout = options.SyncTimeout = options.AsyncTimeout = 5000;
        options.ConnectRetry = 0;
        options.AbortOnConnectFail = true;
        using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        var database = redis.GetDatabase();
        var tracker = new RedisWebhookConnectionTracker(redis, NullLogger<RedisWebhookConnectionTracker>.Instance);
        var operations = new List<Task>();
        var ownershipEstablished = false;

        await IntegrationProofCleanup.RunAsync(async () =>
        {
            // Cleanup ownership is granted only after every exact GUID-derived key is absent.
            foreach (var key in keys)
                Assert.False(await database.KeyExistsAsync(key).WaitAsync(TimeSpan.FromSeconds(10)));
            ownershipEstablished = true;
            await RunAsync(() => tracker.AddWebhooksToConnectionAsync(foreign, [sharedUrl]));
            var foreignTimestamp = await database.StringGetAsync(foreignTimestampKey).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(foreignTimestamp.IsNullOrEmpty);
            await AssertBoundedTtlAsync(foreignTimestampKey);

            await RunAsync(() => tracker.AddWebhooksToConnectionAsync(connection,
                scenario == "empty-registration" ? [] : [sharedUrl, privateUrl]));
            Assert.True(await database.KeyExistsAsync(timestampKey).WaitAsync(TimeSpan.FromSeconds(10)));
            await AssertBoundedTtlAsync(timestampKey);
            if (scenario == "empty-registration")
                Assert.False(await database.KeyExistsAsync(connectionKey).WaitAsync(TimeSpan.FromSeconds(10)));
            else
            {
                await AssertMembersAsync(connectionKey, sharedUrl, privateUrl);
                await AssertMembersAsync(sharedKey, connection, foreign);
                await AssertMembersAsync(privateKey, connection);
                if (scenario == "final-unsubscribe")
                {
                    await RunAsync(() => tracker.RemoveWebhooksFromConnectionAsync(connection, [sharedUrl, privateUrl]));
                    Assert.False(await database.KeyExistsAsync(connectionKey).WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.True(await database.KeyExistsAsync(timestampKey).WaitAsync(TimeSpan.FromSeconds(10)));
                }
            }

            var disconnectedTimestampStates = new List<bool>();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await RunAsync(() => tracker.RemoveConnectionAsync(connection));
                Assert.False(await database.KeyExistsAsync(connectionKey).WaitAsync(TimeSpan.FromSeconds(10)));
                disconnectedTimestampStates.Add(await database.KeyExistsAsync(timestampKey)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(await database.KeyExistsAsync(privateKey).WaitAsync(TimeSpan.FromSeconds(10)));
                await AssertMembersAsync(sharedKey, foreign);
                await AssertMembersAsync(foreignKey, sharedUrl);
                Assert.Equal(foreignTimestamp, await database.StringGetAsync(foreignTimestampKey)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
                await AssertBoundedTtlAsync(foreignTimestampKey);
                Assert.Empty(await tracker.GetConnectionWebhooksAsync(connection).WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(new[] { foreign }, (await tracker.GetWebhookConnectionsAsync(sharedUrl)
                    .WaitAsync(TimeSpan.FromSeconds(10))).Order(StringComparer.Ordinal).ToArray());
            }

            output.WriteLine("WEBHOOK_REDIS_DISCONNECT=" + JsonSerializer.Serialize(new
            {
                scenario, owner, keyCount = keys.Length, disconnects = 2,
                timestampExistsAfterDisconnect = disconnectedTimestampStates,
                targetSetAbsent = true, privateReverseAbsent = true,
                foreignReverseMembers = 1, foreignSetAndTimestampPreserved = true
            }));
            Assert.All(disconnectedTimestampStates, exists => Assert.False(exists,
                "Disconnect must delete the connection timestamp even when its subscription set is absent."));
        }, async () =>
        {
            // Do not race an abandoned producer with deletion. Transport operations are
            // independently bounded by the multiplexer, and all tracker tasks are joined.
            await IntegrationProofCleanup.DrainIndependentlyAsync(
                operations.Select<Task, Func<CancellationToken, Task>>(operation => token => operation.WaitAsync(token)),
                TimeSpan.FromSeconds(15), "Webhook Redis producers did not join.");
            if (!ownershipEstablished) return;
            await IntegrationProofCleanup.DrainIndependentlyAsync(keys.Select<RedisKey, Func<CancellationToken, Task>>(
                key => async token =>
                {
                    await database.KeyDeleteAsync(key).WaitAsync(token);
                    Assert.False(await database.KeyExistsAsync(key).WaitAsync(token));
                }), TimeSpan.FromSeconds(10), "Owned webhook Redis keys could not be removed.");
            output.WriteLine("WEBHOOK_REDIS_KEYS_ABSENT=" + JsonSerializer.Serialize(new { owner, keyCount = keys.Length }));
        }, "Webhook Redis regression and owned cleanup failed.");

        async Task RunAsync(Func<Task> operation)
        {
            var pending = operation();
            operations.Add(pending);
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }

        async Task AssertMembersAsync(RedisKey key, params string[] expected)
        {
            var actual = await database.SetMembersAsync(key).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Select(value => value.ToString()).Order(StringComparer.Ordinal));
        }

        async Task AssertBoundedTtlAsync(RedisKey key)
        {
            var ttl = await database.KeyTimeToLiveAsync(key).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(ttl);
            Assert.InRange(ttl.Value, TimeSpan.Zero, TimeSpan.FromHours(24));
        }
    }

    private static string ReverseKey(string url) =>
        RedisKeys.WebhookConnection.WebhookConnections(Sha256Hash.LegacyStorageBase64Url(url)[..16]);
}
