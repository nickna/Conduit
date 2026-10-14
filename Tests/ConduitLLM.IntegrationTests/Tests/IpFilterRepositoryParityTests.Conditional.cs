using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs.IpFilter;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;
using Npgsql;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class IpFilterRepositoryParityTests
{
    [Fact]
    public async Task TypedConditionalAdministrationPreservesVersionsWithoutUsingEf()
    {
        var schema = $"ip_filters_conditional_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var repository = new NpgsqlIpFilterRepository(source);
            var ef = new Mock<IDbContextFactory<ConduitDbContext>>(MockBehavior.Strict);
            var service = ConditionalService(repository, ef.Object);
            var row = await SeedConditionalFilterAsync(repository);
            var tag = Tag(row);

            await Assert.ThrowsAsync<ResourcePreconditionRequiredException>(() =>
                service.UpdateFilterConditionalAsync(row.Id, new(), new([]), CancellationToken.None));
            await Assert.ThrowsAsync<ResourcePreconditionRequiredException>(() =>
                service.DeleteFilterConditionalAsync(row.Id, new([]), CancellationToken.None));
            await Assert.ThrowsAsync<ResourcePreconditionFailedException>(() =>
                service.UpdateFilterConditionalAsync(row.Id, new() { Name = "loser" }, new(["\"stale\""]), CancellationToken.None));

            var noOp = await service.UpdateFilterConditionalAsync(row.Id, new(), new([tag]), CancellationToken.None);
            Assert.True(noOp.Success);
            Assert.Equal(tag, noOp.Filter!.ResourceETag);
            Assert.Equal(row.UpdatedAt, (await repository.GetByIdAsync(row.Id))!.UpdatedAt);
            var invalid = await service.UpdateFilterConditionalAsync(row.Id,
                new() { IpAddressOrCidr = "invalid" }, new([tag]), CancellationToken.None);
            Assert.False(invalid.Success);
            Assert.Equal(tag, Tag((await repository.GetByIdAsync(row.Id))!));

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateConditionalAsync(row.Id,
                _ => { }, entity => { entity.Name = "rolled back"; throw new InvalidOperationException("abort"); }));
            Assert.Equal("original", (await repository.GetByIdAsync(row.Id))!.Name);

            var updated = await service.UpdateFilterConditionalAsync(row.Id,
                new() { Name = "changed" }, new([$"\"stale\", {tag}"]), CancellationToken.None);
            var persisted = (await repository.GetByIdAsync(row.Id))!;
            Assert.True(updated.Success);
            Assert.Equal("changed", persisted.Name);
            Assert.Equal(row.CreatedAt, persisted.CreatedAt);
            Assert.Equal(Tag(persisted), updated.Filter!.ResourceETag);
            Assert.NotEqual(tag, updated.Filter.ResourceETag);
            await Assert.ThrowsAsync<ResourcePreconditionFailedException>(() =>
                service.DeleteFilterConditionalAsync(row.Id, new([tag]), CancellationToken.None));
            Assert.True((await service.DeleteFilterConditionalAsync(row.Id,
                new([updated.Filter.ResourceETag]), CancellationToken.None)).Success);
            Assert.Null(await repository.GetByIdAsync(row.Id));
            Assert.False((await service.UpdateFilterConditionalAsync(row.Id, new(), new([]), CancellationToken.None)).Success);
            Assert.False((await service.DeleteFilterConditionalAsync(row.Id, new([]), CancellationToken.None)).Success);
            ef.VerifyNoOtherCalls();
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TypedConditionalMutationsValidateAfterACompetingCommit(bool delete, bool wildcard)
    {
        var schema = $"ip_filters_race_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var repository = new NpgsqlIpFilterRepository(source);
            var ef = new Mock<IDbContextFactory<ConduitDbContext>>(MockBehavior.Strict);
            var service = ConditionalService(repository, ef.Object);
            var row = await SeedConditionalFilterAsync(repository);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var winner = await source.OpenConnectionAsync(deadline.Token);
            await using var transaction = await winner.BeginTransactionAsync(deadline.Token);
            await LockFilterAsync(winner, row.Id, deadline.Token);
            var condition = new ResourcePrecondition([wildcard ? "*" : Tag(row)]);
            var operation = delete
                ? (Task)service.DeleteFilterConditionalAsync(row.Id, condition, deadline.Token)
                : service.UpdateFilterConditionalAsync(row.Id, new() { Name = "requested" }, condition, deadline.Token);
            await WaitForBlockedFilterAsync(source, winner.ProcessID, operation, deadline.Token);
            await using (var command = winner.CreateCommand())
            {
                command.CommandText = "UPDATE \"IpFilters\" SET \"Name\" = 'winner', \"UpdatedAt\" = @updated WHERE \"Id\" = @id";
                command.Parameters.AddWithValue("id", row.Id);
                command.Parameters.AddWithValue("updated", row.UpdatedAt.AddMinutes(1));
                Assert.Equal(1, await command.ExecuteNonQueryAsync(deadline.Token));
            }
            await transaction.CommitAsync(deadline.Token);
            if (wildcard) await operation;
            else await Assert.ThrowsAsync<ResourcePreconditionFailedException>(() => operation);
            var persisted = await repository.GetByIdAsync(row.Id, deadline.Token);
            if (wildcard && delete) Assert.Null(persisted);
            else Assert.Equal(wildcard ? "requested" : "winner", persisted!.Name);
            ef.VerifyNoOtherCalls();
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task TypedConditionalLockCancellationAllowsSubsequentMutation()
    {
        var schema = $"ip_filters_cancel_{Guid.NewGuid():N}";
        try
        {
            await CreateSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var repository = new NpgsqlIpFilterRepository(source);
            var ef = new Mock<IDbContextFactory<ConduitDbContext>>(MockBehavior.Strict);
            var service = ConditionalService(repository, ef.Object);
            var row = await SeedConditionalFilterAsync(repository);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            await using var winner = await source.OpenConnectionAsync(deadline.Token);
            await using var transaction = await winner.BeginTransactionAsync(deadline.Token);
            await LockFilterAsync(winner, row.Id, deadline.Token);
            var operation = service.DeleteFilterConditionalAsync(row.Id, new([Tag(row)]), request.Token);
            await WaitForBlockedFilterAsync(source, winner.ProcessID, operation, deadline.Token);
            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            await transaction.RollbackAsync(deadline.Token);
            Assert.Equal(Tag(row), Tag((await repository.GetByIdAsync(row.Id, deadline.Token))!));
            Assert.True((await service.UpdateFilterConditionalAsync(row.Id,
                new() { Name = "recovered" }, new([Tag(row)]), deadline.Token)).Success);
            ef.VerifyNoOtherCalls();
        }
        finally { await DropSchemaAsync(schema); }
    }

    private static AdminIpFilterService ConditionalService(IIpFilterRepository repository,
        IDbContextFactory<ConduitDbContext> ef) => new(repository,
        Mock.Of<IGlobalSettingRepository>(), Mock.Of<IOptionsMonitor<IpFilterOptions>>(),
        NullLogger<AdminIpFilterService>.Instance, dbContextFactory: ef);

    private static async Task<IpFilterEntity> SeedConditionalFilterAsync(NpgsqlIpFilterRepository repository)
    {
        var row = await repository.AddAsync(new IpFilterEntity
        {
            FilterType = "blacklist", IpAddressOrCidr = "192.0.2.0/24", Name = "original", IsEnabled = true
        });
        return (await repository.GetByIdAsync(row.Id))!;
    }

    private static string Tag(IpFilterEntity row) => new ResourceVersion(row.RowVersion, row.UpdatedAt).ETag;

    private static async Task LockFilterAsync(NpgsqlConnection connection, int id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\" FROM \"IpFilters\" WHERE \"Id\" = @id FOR UPDATE";
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(id, await command.ExecuteScalarAsync(token));
    }

    private static async Task WaitForBlockedFilterAsync(NpgsqlDataSource source, int blocker,
        Task operation, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid)))";
        command.Parameters.AddWithValue("blocker", blocker);
        while (!(bool)(await command.ExecuteScalarAsync(token))!)
        {
            Assert.False(operation.IsCompleted, "Conditional mutation must wait for the competing row lock.");
            await Task.Delay(20, token);
        }
    }
}
