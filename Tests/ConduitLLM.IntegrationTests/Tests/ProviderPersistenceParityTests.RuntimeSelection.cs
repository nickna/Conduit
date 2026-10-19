using AwesomeAssertions;

using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class ProviderPersistenceParityTests
{
    /// <summary>
    /// Checks one-command complete snapshots, fresh rotation and owned graphs beyond a management page.
    /// </summary>
    [Fact]
    public async Task RuntimeSelection_ReadsFreshCompleteProviderGraphInOneCommand()
    {
        var schema = $"provider_selection_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var providers = new NpgsqlProviderRepository(source);
            IProviderRuntimeSelectionReader reader = providers;
            var provider = await CreateJoinedReadProviderAsync(providers);
            using var commands = new ProviderReadCommandCounter();

            (await reader.GetByIdAsync(-1)).Should().BeNull();
            commands.TakeCount().Should().Be(1);
            (await reader.GetByIdAsync(provider.Id))!.ProviderKeyCredentials.Should().BeEmpty();
            commands.TakeCount().Should().Be(1);

            await SeedJoinedReadCredentialsAsync(source, provider.Id, 105);
            var other = await CreateJoinedReadProviderAsync(providers);
            await SeedJoinedReadCredentialsAsync(source, other.Id, 3);
            commands.TakeCount();

            var snapshot = (await reader.GetByIdAsync(provider.Id))!;
            commands.TakeCount().Should().Be(1);
            snapshot.ProviderKeyCredentials.Should().HaveCount(105);
            snapshot.ProviderKeyCredentials.Should().OnlyContain(key =>
                key.ProviderId == provider.Id && ReferenceEquals(key.Provider, snapshot));
            snapshot.ProviderKeyCredentials.First().IsPrimary.Should().BeTrue();
            snapshot.Settings.Should().BeEquivalentTo(provider.Settings);
            snapshot.TrustProviderReportedCosts.Should().BeTrue();
            snapshot.ProviderCostMarkupMultiplier.Should().Be(1.125m);
            snapshot.CreatedAt.Should().Be(provider.CreatedAt);
            var nullable = snapshot.ProviderKeyCredentials.Single(key => key.KeyName == "key-2");
            nullable.ApiKey.Should().BeNull();
            nullable.BaseUrl.Should().BeNull();
            nullable.IsEnabled.Should().BeFalse();
            snapshot.ProviderKeyCredentials.Single(key => key.KeyName == "key-3")
                .SecretSettings.Should().BeNull();
            snapshot.ProviderKeyCredentials.Should().OnlyContain(key => key.CreatedAt.Kind == DateTimeKind.Utc);

            snapshot.Settings!["literal"] = "caller mutation";
            snapshot.ProviderKeyCredentials.First().SecretSettings!["secret"] = "caller mutation";
            await using (var update = source.CreateCommand("""
                UPDATE "Providers" SET "ProviderName" = 'rotated provider',
                    "IsEnabled" = false WHERE "Id" = @providerId;
                UPDATE "ProviderKeyCredentials" SET "ApiKey" = 'rotated key',
                    "KeyName" = NULL, "IsEnabled" = true WHERE "Id" = @keyId;
                """))
            {
                update.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, provider.Id);
                update.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, nullable.Id);
                await update.ExecuteNonQueryAsync();
            }
            commands.TakeCount();

            var fresh = (await reader.GetByIdAsync(provider.Id))!;
            commands.TakeCount().Should().Be(1);
            fresh.Should().NotBeSameAs(snapshot);
            fresh.ProviderKeyCredentials.Should().HaveCount(105);
            fresh.ProviderKeyCredentials.Should().OnlyContain(key => ReferenceEquals(key.Provider, fresh));
            fresh.ProviderName.Should().Be("rotated provider");
            fresh.IsEnabled.Should().BeFalse();
            fresh.Settings!["literal"].Should().Be("001");
            fresh.ProviderKeyCredentials.First().SecretSettings!["secret"].Should().Be("enc:v1:secret");
            var rotated = fresh.ProviderKeyCredentials.Single(key => key.Id == nullable.Id);
            rotated.ApiKey.Should().Be("rotated key");
            rotated.KeyName.Should().BeNull();
            rotated.IsEnabled.Should().BeTrue();

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.GetByIdAsync(provider.Id, cancelled.Token));
            (await reader.GetByIdAsync(provider.Id))!.ProviderKeyCredentials.Should().HaveCount(105);
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }
}
