using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Exceptions;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Tests.TestInfrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Configuration.Repositories
{
    /// <summary>
    /// Tests for the idempotent balance adjustment used by spend processing (#927):
    /// one application per idempotency key, with the key recorded on the ledger row
    /// in the same atomic save as the balance change.
    /// </summary>
    public class VirtualKeyGroupRepositoryIdempotencyTests : IDisposable
    {
        private readonly SqliteTestDatabase _database;
        private readonly DbContextOptions<ConduitDbContext> _options;
        private readonly VirtualKeyGroupRepository _repository;

        public VirtualKeyGroupRepositoryIdempotencyTests()
        {
            _database = new SqliteTestDatabase();
            _options = _database.Options;

            var dbContextFactoryMock = new Mock<IDbContextFactory<ConduitDbContext>>();
            dbContextFactoryMock
                .Setup(f => f.CreateDbContext())
                .Returns(() => _database.CreateContext());
            dbContextFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _database.CreateContext());

            _repository = new VirtualKeyGroupRepository(
                dbContextFactoryMock.Object,
                new Mock<ILogger<VirtualKeyGroupRepository>>().Object);

            using var context = _database.CreateContext();
            context.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = 1,
                GroupName = "Test Group",
                Balance = 100m,
                LifetimeCreditsAdded = 100m,
                LifetimeSpent = 0m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        [Fact]
        public async Task AdjustBalanceIdempotentAsync_SameKeyTwice_AppliesOnce()
        {
            // Act
            var first = await _repository.AdjustBalanceIdempotentAsync(
                1, -10m, "spend:req-1", "usage", "System", ReferenceType.VirtualKey, "1");
            var second = await _repository.AdjustBalanceIdempotentAsync(
                1, -10m, "spend:req-1", "usage", "System", ReferenceType.VirtualKey, "1");

            // Assert
            Assert.True(first.Applied);
            Assert.Equal(90m, first.NewBalance);
            Assert.Equal(10m, first.LifetimeSpent);

            Assert.False(second.Applied);
            Assert.Equal(90m, second.NewBalance);
            Assert.Equal(10m, second.LifetimeSpent);

            using var context = new ConduitDbContext(_options);
            var group = await context.VirtualKeyGroups.SingleAsync(g => g.Id == 1);
            Assert.Equal(90m, group.Balance);
            Assert.Equal(10m, group.LifetimeSpent);

            var ledgerRows = await context.VirtualKeyGroupTransactions
                .Where(t => t.IdempotencyKey == "spend:req-1")
                .ToListAsync();
            Assert.Single(ledgerRows);
        }

        [Fact]
        public async Task AdjustBalanceIdempotentAsync_DistinctKeys_BothApply()
        {
            // Act
            var first = await _repository.AdjustBalanceIdempotentAsync(
                1, -10m, "spend:req-a", "usage", "System", ReferenceType.VirtualKey, "1");
            var second = await _repository.AdjustBalanceIdempotentAsync(
                1, -20m, "spend:req-b", "usage", "System", ReferenceType.VirtualKey, "1");

            // Assert
            Assert.True(first.Applied);
            Assert.True(second.Applied);
            Assert.Equal(70m, second.NewBalance);
            Assert.Equal(30m, second.LifetimeSpent);
        }

        [Fact]
        public async Task AdjustBalanceIdempotentAsync_WithMissingGroup_Throws()
        {
            // Act
            var act = () => _repository.AdjustBalanceIdempotentAsync(
                999, -10m, "spend:req-x", "usage", "System", ReferenceType.VirtualKey, "1");

            // Assert
            await Assert.ThrowsAsync<InvalidOperationException>(act);
        }

        [Fact]
        public async Task AdjustBalanceIdempotentAsync_WithEmptyKey_Throws()
        {
            // Act
            var act = () => _repository.AdjustBalanceIdempotentAsync(
                1, -10m, " ", "usage", "System", ReferenceType.VirtualKey, "1");

            // Assert
            await Assert.ThrowsAsync<ArgumentException>(act);
        }

        [Fact]
        public async Task AdjustBalanceAsync_StoresNoIdempotencyKey()
        {
            // Act
            await _repository.AdjustBalanceAsync(1, -10m, "usage", "System", ReferenceType.VirtualKey, "1");

            // Assert
            using var context = new ConduitDbContext(_options);
            var ledgerRow = await context.VirtualKeyGroupTransactions.SingleAsync();
            Assert.Null(ledgerRow.IdempotencyKey);
        }

        [Fact]
        public async Task AdjustBalanceIdempotentAsync_VerifiedLegacyExactAmount_ReplaysWithoutSecondDebitAsync()
        {
            const decimal original = 0.12500001m;
            await _repository.AdjustBalanceIdempotentAsync(
                1, -original, "batch-spend:legacy", "usage", "System", ReferenceType.System, "legacy");
            using (var context = _database.CreateContext())
            {
                var row = await context.VirtualKeyGroupTransactions.SingleAsync();
                Assert.Equal(original, row.OriginalAmount);
                row.Amount = 0.125000m;
                row.OriginalAmount = null;
                await context.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<IdempotencyConflictException>(() => _repository.AdjustBalanceIdempotentAsync(
                1, -original, "batch-spend:legacy", "usage", "System", ReferenceType.System, "legacy"));
            using (var context = _database.CreateContext())
            {
                var row = await context.VirtualKeyGroupTransactions.SingleAsync();
                row.OriginalAmount = original;
                row.OriginalAmountVerifiedBy = "test-operator";
                row.OriginalAmountEvidence = "verified-original-operation";
                row.OriginalAmountVerifiedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }

            var replay = await _repository.AdjustBalanceIdempotentAsync(
                1, -original, "batch-spend:legacy", "usage", "System", ReferenceType.System, "legacy");
            Assert.False(replay.Applied);
            Assert.Equal(100m - original, replay.NewBalance);
            Assert.Equal(original, replay.LifetimeSpent);
            await Assert.ThrowsAsync<IdempotencyConflictException>(() => _repository.AdjustBalanceIdempotentAsync(
                1, -0.12500002m, "batch-spend:legacy", "usage", "System", ReferenceType.System, "legacy"));
            using var verified = _database.CreateContext();
            var ledger = await verified.VirtualKeyGroupTransactions.SingleAsync();
            Assert.Equal(0.125000m, ledger.Amount);
            Assert.Equal(original, ledger.OriginalAmount);
        }

        [Fact]
        public async Task AdjustBalanceIdempotentAsync_ExcessPrecision_RejectsBeforeBalanceChangesAsync()
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _repository.AdjustBalanceIdempotentAsync(
                1, -0.125000011m, "batch-spend:excess", "usage", "System", ReferenceType.System, "excess"));
            using var context = _database.CreateContext();
            Assert.Empty(await context.VirtualKeyGroupTransactions.ToListAsync());
            Assert.Equal(100m, (await context.VirtualKeyGroups.SingleAsync()).Balance);
        }

        public void Dispose() => _database.Dispose();
    }
}
