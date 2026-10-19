using System.Diagnostics;

using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class ProviderPersistenceParityTests
{
    [Fact]
    public async Task GetByIdAsync_JoinedCredentials_UsesOneCommandAndPreservesGraphAsync()
    {
        var schema = $"providers_join_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var repository = new NpgsqlProviderRepository(source);
            var provider = await CreateJoinedReadProviderAsync(repository);
            using var commands = new ProviderReadCommandCounter();

            (await repository.GetByIdAsync(-1)).Should().BeNull();
            commands.TakeCount().Should().Be(1);
            var empty = await repository.GetByIdAsync(provider.Id);
            commands.TakeCount().Should().Be(1);
            empty.Should().NotBeNull();
            empty!.ProviderKeyCredentials.Should().BeEmpty();

            await SeedJoinedReadCredentialsAsync(source, provider.Id, 3);
            commands.TakeCount();
            var loaded = await repository.GetByIdAsync(provider.Id);
            commands.TakeCount().Should().Be(1);

            loaded.Should().NotBeNull();
            loaded!.ProviderName.Should().Be(provider.ProviderName);
            loaded.ProviderType.Should().Be(provider.ProviderType);
            loaded.Settings.Should().BeEquivalentTo(provider.Settings);
            loaded.TrustProviderReportedCosts.Should().BeTrue();
            loaded.ProviderCostMarkupMultiplier.Should().Be(1.125m);
            loaded.CreatedAt.Should().Be(provider.CreatedAt);
            loaded.UpdatedAt.Should().BeCloseTo(provider.UpdatedAt, TimeSpan.FromMicroseconds(1));
            var keys = loaded.ProviderKeyCredentials.ToList();
            keys.Should().HaveCount(3);
            keys.Should().OnlyContain(key => ReferenceEquals(key.Provider, loaded));
            keys.Select(key => key.KeyName).Should().Equal("key-1", "key-2", "key-3");
            keys[0].ApiKey.Should().Be("enc:v1:key-1");
            keys[0].SecretSettings.Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["secret"] = "enc:v1:secret",
                ["literal"] = "001"
            });
            keys[0].IsPrimary.Should().BeTrue();
            keys[1].ApiKey.Should().BeNull();
            keys[1].BaseUrl.Should().BeNull();
            keys[1].IsEnabled.Should().BeFalse();
            keys[2].SecretSettings.Should().BeNull();
            keys.Should().OnlyContain(key => key.CreatedAt.Kind == DateTimeKind.Utc);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.GetByIdAsync(provider.Id, cancelled.Token));
            (await repository.GetByIdAsync(provider.Id))!.ProviderKeyCredentials.Should().HaveCount(3);
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task GetByProviderIdPaginatedAsync_JoinedPage_UsesTwoCommandsAndPreservesCountsAsync()
    {
        var schema = $"credentials_join_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var provider = await CreateJoinedReadProviderAsync(new NpgsqlProviderRepository(source));
            await SeedJoinedReadCredentialsAsync(source, provider.Id, 105);
            var repository = new NpgsqlProviderKeyCredentialRepository(source);
            using var commands = new ProviderReadCommandCounter();

            var normalized = await repository.GetByProviderIdPaginatedAsync(provider.Id, 0, 0);
            commands.TakeCount().Should().Be(2);
            normalized.TotalCount.Should().Be(105);
            normalized.Items.Should().HaveCount(20);
            var first = await repository.GetByProviderIdPaginatedAsync(provider.Id, 1, 1000);
            commands.TakeCount().Should().Be(2);
            first.TotalCount.Should().Be(105);
            first.Items.Should().HaveCount(100);
            first.Items[0].IsPrimary.Should().BeTrue();
            first.Items.Select(key => key.ProviderAccountGroup).Skip(1).Should().BeInAscendingOrder();

            var second = await repository.GetByProviderIdPaginatedAsync(provider.Id, 2, 100);
            commands.TakeCount().Should().Be(2);
            second.TotalCount.Should().Be(105);
            second.Items.Should().HaveCount(5);
            var lastFirstGroup = first.Items[^1].ProviderAccountGroup;
            second.Items.Should().OnlyContain(key =>
                key.ProviderAccountGroup >= lastFirstGroup);
            var pageProvider = second.Items[0].Provider;
            second.Items.Should().OnlyContain(key => ReferenceEquals(key.Provider, pageProvider));
            pageProvider.ProviderKeyCredentials.Should().Equal(second.Items);
            pageProvider.ProviderKeyCredentials.Should().HaveCount(5);
            pageProvider.Settings.Should().BeEquivalentTo(provider.Settings);

            var beyond = await repository.GetByProviderIdPaginatedAsync(provider.Id, 99, 100);
            commands.TakeCount().Should().Be(2);
            beyond.Items.Should().BeEmpty();
            beyond.TotalCount.Should().Be(105);
            var missing = await repository.GetByProviderIdPaginatedAsync(-1, 1, 100);
            commands.TakeCount().Should().Be(2);
            missing.Items.Should().BeEmpty();
            missing.TotalCount.Should().Be(0);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.GetByProviderIdPaginatedAsync(provider.Id, 1, 100, cancelled.Token));
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    private static async Task<Provider> CreateJoinedReadProviderAsync(NpgsqlProviderRepository repository)
    {
        var provider = new Provider
        {
            ProviderType = ProviderType.OpenAI,
            ProviderName = "joined provider",
            Settings = new Dictionary<string, string> { ["region"] = "west", ["literal"] = "001" },
            IsEnabled = true,
            TrustProviderReportedCosts = true,
            ProviderCostMarkupMultiplier = 1.125m,
            CreatedAt = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc)
        };
        await repository.CreateAsync(provider);
        return provider;
    }

    private static async Task SeedJoinedReadCredentialsAsync(NpgsqlDataSource source, int providerId, int count)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO "ProviderKeyCredentials" (
                "ProviderId", "ProviderAccountGroup", "ApiKey", "BaseUrl", "SecretSettings",
                "KeyName", "IsPrimary", "IsEnabled", "CreatedAt", "UpdatedAt")
            SELECT @providerId, ((ordinal - 1) % 33)::smallint,
                CASE WHEN ordinal = 2 THEN NULL ELSE 'enc:v1:key-' || ordinal END,
                CASE WHEN ordinal % 2 = 0 THEN NULL ELSE 'https://example.invalid/key' END,
                CASE WHEN ordinal % 3 = 0 THEN NULL
                    ELSE '{"secret":"enc:v1:secret","literal":"001"}'::jsonb END,
                'key-' || ordinal, ordinal = 1, ordinal % 2 = 1,
                TIMESTAMPTZ '2026-10-18T12:00:00Z', TIMESTAMPTZ '2026-10-18T12:00:00Z'
            FROM generate_series(1, @count) AS allocation(ordinal)
            """);
        command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId);
        command.Parameters.AddWithValue("count", NpgsqlDbType.Integer, count);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ProviderReadCommandCounter : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity _activity;
        private readonly ActivityTraceId _traceId;
        private int _commands;

        public ProviderReadCommandCounter()
        {
            _traceId = ActivityTraceId.CreateRandom();
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name == "Npgsql" && activity.TraceId == _traceId &&
                        activity.GetTagItem("db.query.text") is string)
                    {
                        Interlocked.Increment(ref _commands);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
            _activity = new Activity("provider joined read contract")
                .SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(_traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
                .Start();
        }

        public int TakeCount() => Interlocked.Exchange(ref _commands, 0);

        public void Dispose()
        {
            _activity.Dispose();
            _listener.Dispose();
        }
    }
}
