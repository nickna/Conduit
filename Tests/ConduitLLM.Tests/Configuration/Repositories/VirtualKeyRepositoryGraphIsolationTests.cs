using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Configuration.Repositories;

public sealed class VirtualKeyRepositoryGraphIsolationTests : IDisposable
{
    private static readonly DateTime OriginalTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
    private readonly SqliteTestDatabase _database = new();
    private readonly VirtualKeyRepository _keys;
    private readonly VirtualKeyGroupRepository _groups;

    public VirtualKeyRepositoryGraphIsolationTests()
    {
        var factory = _database.CreateDbContextFactory();
        _keys = new VirtualKeyRepository(factory, NullLogger<VirtualKeyRepository>.Instance);
        _groups = new VirtualKeyGroupRepository(factory, NullLogger<VirtualKeyGroupRepository>.Instance);
        _database.Seed(db =>
        {
            db.VirtualKeyGroups.AddRange(
                new VirtualKeyGroup
                {
                    Id = 1, GroupName = "original-group", Balance = 100m,
                    LifetimeCreditsAdded = 100m, LifetimeSpent = 20m, UpdatedAt = OriginalTime
                },
                new VirtualKeyGroup
                {
                    Id = 2, GroupName = "destination-group", Balance = 200m,
                    LifetimeCreditsAdded = 200m, LifetimeSpent = 30m, UpdatedAt = OriginalTime
                });
            db.VirtualKeys.Add(new VirtualKey
            {
                Id = 1, KeyName = "original-key", KeyHash = "graph-isolation-key",
                VirtualKeyGroupId = 1, IsEnabled = true, UpdatedAt = OriginalTime
            });
            db.SaveChanges();
        });
    }

    [Fact]
    public async Task DetachedKeyUpdate_PreservesCommittedGroupChanges()
    {
        var key = await _keys.GetByIdAsync(1);
        Assert.Equal(100m, key.VirtualKeyGroup.Balance);
        var winningTime = OriginalTime.AddMinutes(1);
        await using (var writer = _database.CreateContext())
        {
            var group = await writer.VirtualKeyGroups.SingleAsync(group => group.Id == 1);
            group.GroupName = "winning-group";
            group.Balance = 150m;
            group.LifetimeCreditsAdded = 150m;
            group.LifetimeSpent = 25m;
            group.RateLimitRpm = 30;
            group.UpdatedAt = winningTime;
            await writer.SaveChangesAsync();
        }

        key.KeyName = "edited-key";
        key.IsEnabled = false;
        Assert.True(await _keys.UpdateAsync(key));

        await using var verify = _database.CreateContext();
        var persisted = await verify.VirtualKeyGroups.AsNoTracking().SingleAsync(group => group.Id == 1);
        Assert.Equal("winning-group", persisted.GroupName);
        Assert.Equal(150m, persisted.Balance);
        Assert.Equal(150m, persisted.LifetimeCreditsAdded);
        Assert.Equal(25m, persisted.LifetimeSpent);
        Assert.Equal(30, persisted.RateLimitRpm);
        Assert.Equal(winningTime, persisted.UpdatedAt);
        var savedKey = await verify.VirtualKeys.AsNoTracking().SingleAsync(key => key.Id == 1);
        Assert.Equal("edited-key", savedKey.KeyName);
        Assert.False(savedKey.IsEnabled);
        Assert.NotEqual(OriginalTime, savedKey.UpdatedAt);
    }

    [Fact]
    public async Task DetachedGroupUpdate_PreservesCommittedMemberKeyChanges()
    {
        var group = await _groups.GetByIdAsync(1);
        Assert.Equal("original-key", Assert.Single(group.VirtualKeys).KeyName);
        var winningTime = OriginalTime.AddMinutes(1);
        await using (var writer = _database.CreateContext())
        {
            var key = await writer.VirtualKeys.SingleAsync(key => key.Id == 1);
            key.KeyName = "winning-key";
            key.IsEnabled = false;
            key.RateLimitRpm = 50;
            key.UpdatedAt = winningTime;
            await writer.SaveChangesAsync();
        }

        group.GroupName = "edited-group";
        group.RateLimitRpm = 100;
        Assert.True(await _groups.UpdateAsync(group));

        await using var verify = _database.CreateContext();
        var persisted = await verify.VirtualKeys.AsNoTracking().SingleAsync(key => key.Id == 1);
        Assert.Equal("winning-key", persisted.KeyName);
        Assert.False(persisted.IsEnabled);
        Assert.Equal(50, persisted.RateLimitRpm);
        Assert.Equal(winningTime, persisted.UpdatedAt);
        var savedGroup = await verify.VirtualKeyGroups.AsNoTracking().SingleAsync(group => group.Id == 1);
        Assert.Equal("edited-group", savedGroup.GroupName);
        Assert.Equal(100, savedGroup.RateLimitRpm);
        Assert.NotEqual(OriginalTime, savedGroup.UpdatedAt);
    }

    [Fact]
    public async Task DirectSpendUpdate_KeyTimestampRefreshPreservesDebitAndLedger()
    {
        Assert.True(await CreateService().UpdateSpendAsync(1, 5m, OriginalTime));

        await using var verify = _database.CreateContext();
        var group = await verify.VirtualKeyGroups.AsNoTracking().SingleAsync(group => group.Id == 1);
        Assert.Equal(95m, group.Balance);
        Assert.Equal(25m, group.LifetimeSpent);
        Assert.Equal(100m, group.LifetimeCreditsAdded);
        var transaction = await verify.VirtualKeyGroupTransactions.AsNoTracking().SingleAsync();
        Assert.Equal(TransactionType.Debit, transaction.TransactionType);
        Assert.Equal(5m, transaction.Amount);
        Assert.Equal(group.Balance, transaction.BalanceAfter);
        Assert.Equal(ReferenceType.VirtualKey, transaction.ReferenceType);
        Assert.Equal("1", transaction.ReferenceId);
        var key = await verify.VirtualKeys.AsNoTracking().SingleAsync(key => key.Id == 1);
        Assert.NotEqual(OriginalTime, key.UpdatedAt);
    }

    [Fact]
    public async Task ResetSpend_KeyTimestampRefreshPreservesResetLifetimeCounter()
    {
        Assert.True(await CreateService().ResetSpendAsync(1));

        await using var verify = _database.CreateContext();
        var group = await verify.VirtualKeyGroups.AsNoTracking().SingleAsync(group => group.Id == 1);
        Assert.Equal(0m, group.LifetimeSpent);
        Assert.Equal(100m, group.Balance);
        Assert.Equal(100m, group.LifetimeCreditsAdded);
        var history = await verify.VirtualKeySpendHistory.AsNoTracking().SingleAsync();
        Assert.Equal(1, history.VirtualKeyId);
        Assert.Equal(20m, history.Amount);
    }

    [Fact]
    public async Task GroupReassignment_PreservesNewForeignKeyDespiteOldGroupNavigation()
    {
        Assert.True(await CreateService().UpdateVirtualKeyAsync(1,
            new UpdateVirtualKeyRequestDto { VirtualKeyGroupId = 2 }));

        var reloaded = await _keys.GetByIdAsync(1);
        Assert.Equal(2, reloaded.VirtualKeyGroupId);
        Assert.Equal("destination-group", reloaded.VirtualKeyGroup.GroupName);
        Assert.Equal(200m, reloaded.VirtualKeyGroup.Balance);
        await using var verify = _database.CreateContext();
        Assert.Equal(100m, (await verify.VirtualKeyGroups.AsNoTracking().SingleAsync(group => group.Id == 1)).Balance);
        Assert.Equal(2, await verify.VirtualKeyGroups.CountAsync());
    }

    private DirectApiVirtualKeyService CreateService() => new(
        _keys, _groups,
        new VirtualKeySpendHistoryRepository(_database.CreateDbContextFactory(), NullLogger<VirtualKeySpendHistoryRepository>.Instance),
        eventBus: null, NullLogger<DirectApiVirtualKeyService>.Instance);

    public void Dispose() => _database.Dispose();
}
