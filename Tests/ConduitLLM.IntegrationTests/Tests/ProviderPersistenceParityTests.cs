using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs the provider/credential consistency contract against EF and typed
/// Npgsql on real PostgreSQL.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class ProviderPersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public ProviderPersistenceParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlImplementTheSameProviderCredentialContract()
    {
        var efSchema = $"providers_ef_{Guid.NewGuid():N}";
        var npgsqlSchema = $"providers_npgsql_{Guid.NewGuid():N}";

        try
        {
            await CreateSchemaAsync(efSchema);
            await CreateSchemaAsync(npgsqlSchema);

            var efConnectionString = WithSearchPath(efSchema);
            await ExerciseContractAsync(
                new ProviderRepository(
                    new TestDbContextFactory(efConnectionString),
                    NullLogger<ProviderRepository>.Instance),
                new ProviderKeyCredentialRepository(
                    new TestDbContextFactory(efConnectionString),
                    NullLogger<ProviderKeyCredentialRepository>.Instance));

            var npgsqlConnectionString = WithSearchPath(npgsqlSchema);
            await using var dataSource = NpgsqlDataSource.Create(npgsqlConnectionString);
            await ExerciseContractAsync(
                new NpgsqlProviderRepository(dataSource),
                new NpgsqlProviderKeyCredentialRepository(dataSource));
        }
        finally
        {
            await DropSchemaAsync(efSchema);
            await DropSchemaAsync(npgsqlSchema);
        }
    }

    private static async Task ExerciseContractAsync(
        IProviderRepository providers,
        IProviderKeyCredentialRepository credentials)
    {
        (await providers.ListAsync()).Should().BeEmpty();
        (await providers.CountAsync(null)).Should().Be(0);

        var openAi = new Provider
        {
            ProviderType = ProviderType.OpenAI,
            ProviderName = "Primary OpenAI",
            BaseUrl = "https://example.invalid/openai",
            Settings = new Dictionary<string, string>
            {
                ["organization"] = "org-1",
                ["region"] = "west"
            },
            IsEnabled = true,
            TrustProviderReportedCosts = true,
            ProviderCostMarkupMultiplier = 1.125m,
            CreatedAt = default
        };
        var groq = new Provider
        {
            ProviderType = ProviderType.Groq,
            ProviderName = "Disabled Groq",
            IsEnabled = false,
            Settings = null
        };

        (await providers.CreateAsync(openAi)).Should().Be(openAi.Id).And.BeGreaterThan(0);
        (await providers.CreateAsync(groq)).Should().Be(groq.Id).And.BeGreaterThan(0);
        openAi.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        openAi.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);

        var graphCreate = new Provider
        {
            ProviderType = ProviderType.OpenRouter,
            ProviderName = "invalid graph",
            ProviderKeyCredentials = [new ProviderKeyCredential { ApiKey = "nested" }]
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => providers.CreateAsync(graphCreate));

        (await providers.CountAsync(null)).Should().Be(2);
        (await providers.CountAsync(true)).Should().Be(1);
        (await providers.CountAsync(false)).Should().Be(1);
        (await providers.GetProviderNameMapAsync()).Should().Contain(new Dictionary<int, string>
        {
            [openAi.Id] = openAi.ProviderName,
            [groq.Id] = groq.ProviderName
        });

        var (firstProviderPage, providerCount) = await providers.GetPaginatedAsync(1, 1);
        providerCount.Should().Be(2);
        firstProviderPage.Should().ContainSingle().Which.Id.Should().Be(openAi.Id);

        var loadedProvider = await providers.GetByIdAsync(openAi.Id);
        loadedProvider.Should().NotBeNull();
        loadedProvider!.Settings.Should().BeEquivalentTo(openAi.Settings);
        loadedProvider.ProviderCostMarkupMultiplier.Should().Be(1.125m);
        loadedProvider.ProviderKeyCredentials.Should().BeEmpty();

        loadedProvider.ProviderName = "Updated OpenAI";
        loadedProvider.BaseUrl = null;
        loadedProvider.Settings = new Dictionary<string, string> { ["region"] = "east" };
        loadedProvider.TrustProviderReportedCosts = false;
        loadedProvider.ProviderCostMarkupMultiplier = 1m;
        (await providers.UpdateAsync(loadedProvider)).Should().BeTrue();
        (await providers.UpdateAsync(new Provider
        {
            Id = -1,
            ProviderName = "missing",
            ProviderType = ProviderType.OpenAI
        })).Should().BeFalse();

        var first = new ProviderKeyCredential
        {
            ProviderId = openAi.Id,
            ProviderAccountGroup = 0,
            ApiKey = "enc:v1:first",
            BaseUrl = null,
            SecretSettings = new Dictionary<string, string> { ["secret"] = "enc:v1:value" },
            KeyName = "first",
            IsPrimary = false,
            IsEnabled = true,
            CreatedAt = default
        };
        var second = new ProviderKeyCredential
        {
            ProviderId = openAi.Id,
            ProviderAccountGroup = 1,
            ApiKey = "enc:v1:second",
            KeyName = "second",
            IsPrimary = false,
            IsEnabled = true
        };
        var disabled = new ProviderKeyCredential
        {
            ProviderId = openAi.Id,
            ProviderAccountGroup = 2,
            ApiKey = null,
            SecretSettings = null,
            KeyName = "disabled",
            IsPrimary = false,
            IsEnabled = false
        };

        (await credentials.CreateAsync(first)).Should().Be(first.Id).And.BeGreaterThan(0);
        first.IsPrimary.Should().BeTrue();
        (await credentials.CreateAsync(second)).Should().Be(second.Id).And.BeGreaterThan(0);
        second.IsPrimary.Should().BeFalse();
        (await credentials.CreateAsync(disabled)).Should().Be(disabled.Id).And.BeGreaterThan(0);
        disabled.IsPrimary.Should().BeFalse();

        (await credentials.HasKeyCredentialsAsync(openAi.Id)).Should().BeTrue();
        (await credentials.HasKeyCredentialsAsync(groq.Id)).Should().BeFalse();
        (await credentials.CountByProviderIdAsync(openAi.Id)).Should().Be(3);

        var loadedCredential = await credentials.GetByIdAsync(first.Id);
        loadedCredential.Should().NotBeNull();
        loadedCredential!.Provider.Id.Should().Be(openAi.Id);
        loadedCredential.Provider.ProviderName.Should().Be("Updated OpenAI");
        loadedCredential.SecretSettings.Should().BeEquivalentTo(first.SecretSettings);

        var (credentialPage, credentialCount) = await credentials.GetPaginatedAsync(1, 2);
        credentialCount.Should().Be(3);
        credentialPage.Should().HaveCount(2);
        credentialPage.Should().OnlyContain(credential => credential.Provider.Id == openAi.Id);

        var (providerCredentials, scopedCount) =
            await credentials.GetByProviderIdPaginatedAsync(openAi.Id, 1, 10);
        scopedCount.Should().Be(3);
        providerCredentials.Select(credential => credential.Id)
            .Should().Equal(first.Id, second.Id, disabled.Id);

        (await credentials.GetPrimaryKeyAsync(openAi.Id))!.Id.Should().Be(first.Id);
        (await credentials.GetEnabledKeysByProviderIdAsync(openAi.Id))
            .Select(credential => credential.Id)
            .Should().Equal(first.Id, second.Id);

        (await credentials.SetPrimaryKeyAsync(openAi.Id, -1)).Should().BeFalse();
        (await credentials.GetPrimaryKeyAsync(openAi.Id))!.Id.Should().Be(first.Id);

        var disabledPromotion = await Record.ExceptionAsync(
            () => credentials.SetPrimaryKeyAsync(openAi.Id, disabled.Id));
        disabledPromotion.Should().NotBeNull();
        (await credentials.GetPrimaryKeyAsync(openAi.Id))!.Id.Should().Be(first.Id);

        (await credentials.SetPrimaryKeyAsync(openAi.Id, second.Id)).Should().BeTrue();
        (await credentials.GetPrimaryKeyAsync(openAi.Id))!.Id.Should().Be(second.Id);

        var update = await credentials.GetByIdAsync(second.Id);
        update.Should().NotBeNull();
        update!.ApiKey = "enc:v1:second-rotated";
        update.BaseUrl = "https://example.invalid/key";
        update.SecretSettings = new Dictionary<string, string>
        {
            ["secret"] = "enc:v1:rotated",
            ["tenant"] = "enc:v1:tenant"
        };
        update.KeyName = "second rotated";
        (await credentials.UpdateAsync(update)).Should().BeTrue();

        var afterUpdate = await credentials.GetByIdAsync(second.Id);
        afterUpdate.Should().NotBeNull();
        afterUpdate!.ApiKey.Should().Be("enc:v1:second-rotated");
        afterUpdate.BaseUrl.Should().Be("https://example.invalid/key");
        afterUpdate.KeyName.Should().Be("second rotated");
        afterUpdate.SecretSettings.Should().BeEquivalentTo(update.SecretSettings);
        afterUpdate.IsPrimary.Should().BeTrue();

        var enableDisabled = await credentials.GetByIdAsync(disabled.Id);
        enableDisabled!.IsEnabled = true;
        (await credentials.UpdateAsync(enableDisabled)).Should().BeTrue();
        (await credentials.GetByIdAsync(disabled.Id))!.IsPrimary.Should().BeFalse();

        (await providers.GetByIdAsync(openAi.Id))!.ProviderKeyCredentials.Should().HaveCount(3);
        (await credentials.UpdateAsync(new ProviderKeyCredential { Id = -1 })).Should().BeFalse();
        (await credentials.DeleteAsync(-1)).Should().BeFalse();

        (await providers.DeleteAsync(openAi.Id)).Should().BeTrue();
        (await credentials.CountByProviderIdAsync(openAi.Id)).Should().Be(0);
        (await providers.DeleteAsync(openAi.Id)).Should().BeFalse();
        (await providers.DeleteAsync(groq.Id)).Should().BeTrue();
        (await providers.ListAsync()).Should().BeEmpty();
    }

    private async Task CreateSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."Providers" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ProviderType" integer NOT NULL,
                "ProviderName" character varying(100) NOT NULL,
                "BaseUrl" text NULL,
                "Settings" jsonb NULL,
                "IsEnabled" boolean NOT NULL,
                "TrustProviderReportedCosts" boolean NOT NULL,
                "ProviderCostMarkupMultiplier" numeric NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE INDEX "IX_Providers_ProviderType"
                ON "{schema}"."Providers" ("ProviderType");
            CREATE TABLE "{schema}"."ProviderKeyCredentials" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ProviderId" integer NOT NULL REFERENCES "{schema}"."Providers" ("Id") ON DELETE CASCADE,
                "ProviderAccountGroup" smallint NOT NULL,
                "ApiKey" text NULL,
                "BaseUrl" text NULL,
                "SecretSettings" jsonb NULL,
                "KeyName" text NULL,
                "IsPrimary" boolean NOT NULL,
                "IsEnabled" boolean NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "CK_PKC_AccountGroupRange"
                    CHECK ("ProviderAccountGroup" >= 0 AND "ProviderAccountGroup" <= 32),
                CONSTRAINT "CK_PKC_PrimaryMustBeEnabled"
                    CHECK ("IsPrimary" = false OR "IsEnabled" = true)
            );
            CREATE INDEX "IX_PKC_ProviderId"
                ON "{schema}"."ProviderKeyCredentials" ("ProviderId");
            CREATE UNIQUE INDEX "IX_PKC_UniqueApiKey"
                ON "{schema}"."ProviderKeyCredentials" ("ProviderId", "ApiKey")
                WHERE "ApiKey" IS NOT NULL;
            CREATE UNIQUE INDEX "IX_PKC_OnePrimary"
                ON "{schema}"."ProviderKeyCredentials" ("ProviderId", "IsPrimary")
                WHERE "IsPrimary" = true;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private string WithSearchPath(string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = schema
        };
        return builder.ConnectionString;
    }

    private sealed class TestDbContextFactory(string connectionString) :
        IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(connectionString)
                .Options;

        public ConduitDbContext CreateDbContext() => new(_options);

        public Task<ConduitDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
