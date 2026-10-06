using System.Text.Json;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Options;
using ConduitLLM.Core.Services;
using ConduitLLM.Tests.TestInfrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Admin.Services;

[Trait("Category", "Unit")]
[Trait("Component", "MediaLifecycle")]
public sealed class MediaCleanupStatusServiceTests : IDisposable
{
    private readonly ConduitDbContext _context;
    private readonly ServiceProvider _serviceProvider;
    private readonly SqliteTestDatabase _database;
    private readonly Mock<IMediaCleanupApprovalService> _approvalService;

    public MediaCleanupStatusServiceTests()
    {
        _database = new SqliteTestDatabase();
        _context = _database.CreateContext();

        var budgetService = new Mock<IMediaDeletionBudgetService>();
        budgetService
            .Setup(service => service.GetMonthlyDeleteCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(12);
        budgetService
            .Setup(service => service.GetRemainingBudgetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(88);
        budgetService.SetupGet(service => service.BackendName).Returns("InMemory");
        budgetService.SetupGet(service => service.IsPersistent).Returns(false);

        var settingRepository = new Mock<IGlobalSettingRepository>();
        settingRepository
            .Setup(repository => repository.GetByKeyAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlobalSetting?)null);

        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationDbContext>(_context);
        services.AddSingleton(budgetService.Object);
        services.AddSingleton(settingRepository.Object);
        _approvalService = new Mock<IMediaCleanupApprovalService>();
        _approvalService
            .Setup(service => service.ListPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MediaCleanupApprovalDto>());
        services.AddSingleton(_approvalService.Object);
        services.AddSingleton<IMediaStorageService>(
            new InMemoryMediaStorageService(Mock.Of<ILogger<InMemoryMediaStorageService>>()));
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task GetStatusAsync_WithoutRedis_ReturnsLastOutcomeForEveryCleanupType()
    {
        var options = new MediaLifecycleOptions
        {
            Enabled = true,
            MonthlyDeleteBudget = 100,
            EnableSoftDelete = true,
            SoftDeleteGracePeriodDays = 9,
            EnableExpirationCleanup = true,
            EnableReconciliation = false,
            EnableRetentionCleanup = true,
            TestVirtualKeyGroups = [3, 1, 3],
            BudgetAlertThresholdPercent = 85
        };
        var service = new MediaCleanupStatusService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            Mock.Of<ILogger<MediaCleanupStatusService>>(),
            s3Options: Options.Create(new S3StorageOptions
            {
                PublicBaseUrl = "https://cdn.example.com"
            }));
        _approvalService
            .Setup(approvals => approvals.ListPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new MediaCleanupApprovalDto
                {
                    Id = Guid.NewGuid(),
                    CleanupType = MediaCleanupTypes.Retention,
                    CandidateCount = 125,
                    CandidateBytes = 4096
                }
            });

        await service.RecordOperationCompletionAsync(
            MediaCleanupTypes.Expiration, 2, 4096, 1.25, "Completed", "test-leader", "scheduled");
        await service.RecordOperationCompletionAsync(
            MediaCleanupTypes.Retention, 1, 1024, 0.5, "Completed with errors", "test-leader", "manual");
        await service.RecordReconciliationDriftAsync(4, 8192);

        var status = await service.GetStatusAsync();

        status.OperationStatuses.Should().HaveCount(5);
        status.IsSoftDeleteEnabled.Should().BeTrue();
        status.SoftDeleteGracePeriodDays.Should().Be(9);
        status.OperationStatuses.Single(item => item.CleanupType == MediaCleanupTypes.Purge)
            .IsEnabled.Should().BeTrue();
        status.OperationStatuses.Single(item => item.CleanupType == MediaCleanupTypes.Expiration)
            .Should().BeEquivalentTo(new
            {
                IsEnabled = true,
                LastRunStatus = "Completed",
                TriggeredBy = "scheduled",
                LastRunFilesDeleted = 2,
                LastRunBytesFreed = 4096L
            });
        status.OperationStatuses.Single(item => item.CleanupType == MediaCleanupTypes.Reconciliation)
            .Should().Match<MediaCleanupOperationStatusDto>(item =>
                !item.IsEnabled && item.LastRunTimeUtc == null);
        status.OperationStatuses.Single(item => item.CleanupType == MediaCleanupTypes.Quota)
            .IsEnabled.Should().BeTrue();
        status.OperationStatuses.Single(item => item.CleanupType == MediaCleanupTypes.Retention)
            .LastRunStatus.Should().Be("Completed with errors");
        status.CurrentLeaderInstanceId.Should().Be("test-leader");
        status.StorageBackend.Should().Be("InMemory");
        status.IsPublicMediaBaseUrlConfigured.Should().BeTrue();
        status.TestScopeActive.Should().BeTrue();
        status.TestVirtualKeyGroups.Should().Equal(1, 3);
        status.UntrackedObjectCount.Should().Be(4);
        status.UntrackedBytes.Should().Be(8192);
        status.PendingApprovalCount.Should().Be(1);
        status.PendingApprovals.Single().CandidateCount.Should().Be(125);
        status.BudgetBackend.Should().Be("InMemory");
        status.IsBudgetBackendPersistent.Should().BeFalse();
        status.BudgetFailureMode.Should().Be("FailClosed");
        status.BudgetAlertThresholdPercent.Should().Be(85);
    }

    [Fact]
    public void RedisContracts_PreserveLegacyPascalCaseAndReadExistingValues()
    {
        var run = new LastRunInfo
        {
            LastRunTimeUtc = new DateTime(2026, 8, 12, 12, 30, 0, DateTimeKind.Utc),
            FilesDeleted = 3,
            BytesFreed = 4096,
            DurationSeconds = 1.5,
            Status = "Completed",
            LeaderInstanceId = "admin-1",
            TriggeredBy = "scheduled"
        };

        var json = JsonSerializer.Serialize(
            run,
            MediaCleanupRedisJsonContext.Default.LastRunInfo);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("FilesDeleted").GetInt32().Should().Be(3);
        document.RootElement.TryGetProperty("filesDeleted", out _).Should().BeFalse();

        const string existingDrift =
            """{"UntrackedObjectCount":7,"UntrackedBytes":8192,"ObservedAtUtc":"2026-08-12T12:30:00Z"}""";
        var drift = JsonSerializer.Deserialize(
            existingDrift,
            MediaCleanupRedisJsonContext.Default.ReconciliationDriftInfo);

        drift.Should().NotBeNull();
        drift!.UntrackedObjectCount.Should().Be(7);
        drift.UntrackedBytes.Should().Be(8192);
        drift.ObservedAtUtc.Should().Be(new DateTime(2026, 8, 12, 12, 30, 0, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _context.Dispose();
        _database.Dispose();
    }
}
