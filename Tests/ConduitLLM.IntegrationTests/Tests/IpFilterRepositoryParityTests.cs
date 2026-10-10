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
/// Runs the IP-filter behavioral contract against the production EF adapter and
/// the NativeAOT-oriented typed-Npgsql adapter on real PostgreSQL.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class IpFilterRepositoryParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public IpFilterRepositoryParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlImplementTheSameIpFilterContract()
    {
        var efSchema = $"ip_filters_ef_{Guid.NewGuid():N}";
        var npgsqlSchema = $"ip_filters_npgsql_{Guid.NewGuid():N}";

        try
        {
            await CreateSchemaAsync(efSchema);
            await CreateSchemaAsync(npgsqlSchema);

            var efConnectionString = WithSearchPath(efSchema);
            var efRepository = new IpFilterRepository(
                new TestDbContextFactory(efConnectionString),
                NullLogger<IpFilterRepository>.Instance);
            await ExerciseContractAsync(efRepository, efConnectionString);

            var npgsqlConnectionString = WithSearchPath(npgsqlSchema);
            await using var dataSource = NpgsqlDataSource.Create(npgsqlConnectionString);
            var npgsqlRepository = new NpgsqlIpFilterRepository(dataSource);
            await ExerciseContractAsync(npgsqlRepository, npgsqlConnectionString);
        }
        finally
        {
            await DropSchemaAsync(efSchema);
            await DropSchemaAsync(npgsqlSchema);
        }
    }

    private static async Task ExerciseContractAsync(
        IIpFilterRepository repository,
        string connectionString)
    {
        (await repository.ListAsync()).Should().BeEmpty();

        var globalBlacklist = await repository.AddAsync(new IpFilterEntity
        {
            FilterType = "blacklist",
            IpAddressOrCidr = "10.0.0.0/8",
            Name = "private network",
            Description = null,
            IsEnabled = true,
            CreatedAt = default,
            CreatedBy = "contract"
        });
        var globalWhitelist = await repository.AddAsync(new IpFilterEntity
        {
            FilterType = "whitelist",
            IpAddressOrCidr = "192.0.2.0/24",
            IsEnabled = true
        });
        var key41Disabled = await repository.AddAsync(new IpFilterEntity
        {
            FilterType = "whitelist",
            IpAddressOrCidr = "198.51.100.0/24",
            IsEnabled = false,
            VirtualKeyId = 41
        });
        var key41Enabled = await repository.AddAsync(new IpFilterEntity
        {
            FilterType = "blacklist",
            IpAddressOrCidr = "203.0.113.0/24",
            IsEnabled = true,
            VirtualKeyId = 41
        });
        var key42Enabled = await repository.AddAsync(new IpFilterEntity
        {
            FilterType = "blacklist",
            IpAddressOrCidr = "198.18.0.0/15",
            IsEnabled = true,
            VirtualKeyId = 42
        });

        globalBlacklist.Id.Should().BeGreaterThan(0);
        globalBlacklist.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        globalBlacklist.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);

        var byId = await repository.GetByIdAsync(globalBlacklist.Id);
        byId.Should().NotBeNull();
        byId!.Name.Should().Be("private network");
        byId.Description.Should().BeNull();
        byId.CreatedBy.Should().Be("contract");
        byId.VirtualKeyId.Should().BeNull();

        var all = await repository.ListAsync();
        all.Should().HaveCount(5);
        all.Select(filter => (filter.FilterType, filter.IpAddressOrCidr))
            .Should().BeInAscendingOrder();

        var enabledGlobal = await repository.GetEnabledAsync();
        enabledGlobal.Select(filter => filter.Id)
            .Should().Equal(globalBlacklist.Id, globalWhitelist.Id);

        var enabledPerKey = await repository.GetEnabledPerKeyAsync();
        enabledPerKey.Select(filter => filter.Id)
            .Should().Equal(key41Enabled.Id, key42Enabled.Id);

        var key41 = await repository.GetByVirtualKeyIdAsync(41);
        key41.Select(filter => filter.Id)
            .Should().Equal(key41Enabled.Id, key41Disabled.Id);

        byId.Name = "updated";
        byId.Description = "updated description";
        byId.UpdatedBy = "contract-update";
        byId.IsEnabled = false;
        (await repository.UpdateAsync(byId)).Should().BeTrue();

        var updated = await repository.GetByIdAsync(byId.Id);
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("updated");
        updated.Description.Should().Be("updated description");
        updated.UpdatedBy.Should().Be("contract-update");
        updated.IsEnabled.Should().BeFalse();

        await ForceRowVersionAsync(connectionString, updated.Id, [1, 2, 3, 4]);
        updated.Description = "stale write";
        (await repository.UpdateAsync(updated)).Should().BeFalse();

        (await repository.DeleteAsync(-1)).Should().BeFalse();
        foreach (var filter in await repository.ListAsync())
        {
            (await repository.DeleteAsync(filter.Id)).Should().BeTrue();
        }

        (await repository.ListAsync()).Should().BeEmpty();
    }

    private static async Task ForceRowVersionAsync(
        string connectionString,
        int id,
        byte[] rowVersion)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE \"IpFilters\" SET \"RowVersion\" = @rowVersion WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("rowVersion", rowVersion);
        command.Parameters.AddWithValue("id", id);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task CreateSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."VirtualKeys" (
                "Id" integer PRIMARY KEY
            );
            INSERT INTO "{schema}"."VirtualKeys" ("Id") VALUES (41), (42);
            CREATE TABLE "{schema}"."IpFilters" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "FilterType" character varying(10) NOT NULL,
                "IpAddressOrCidr" character varying(50) NOT NULL,
                "Name" character varying(100) NULL,
                "Description" character varying(500) NULL,
                "IsEnabled" boolean NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "CreatedBy" character varying(100) NULL,
                "UpdatedBy" character varying(100) NULL,
                "VirtualKeyId" integer NULL REFERENCES "{schema}"."VirtualKeys" ("Id") ON DELETE CASCADE,
                "RowVersion" bytea NULL
            );
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
