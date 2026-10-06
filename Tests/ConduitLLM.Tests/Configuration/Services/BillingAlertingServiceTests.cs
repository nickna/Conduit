using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Configuration.Services;

public class BillingAlertingServiceTests
{
    [Fact]
    public async Task SendCriticalAlertAsync_DuringCooldown_RecordsEveryAuditEvent()
    {
        var logger = new Mock<ILogger<BillingAlertingService>>();
        var auditService = new Mock<IBillingAuditService>();
        var service = new BillingAlertingService(logger.Object, auditService.Object);

        await service.SendCriticalAlertAsync(
            "first failure",
            101,
            new Dictionary<string, object?> { ["Amount"] = 1.25m });
        await service.SendCriticalAlertAsync(
            "second failure",
            202,
            new Dictionary<string, object?> { ["Amount"] = 2.50m });

        auditService.Verify(
            audit => audit.LogBillingEventAsync(It.IsAny<BillingAuditEvent>()),
            Times.Exactly(2));
        auditService.Verify(
            audit => audit.LogBillingEventAsync(It.Is<BillingAuditEvent>(billingEvent =>
                billingEvent.EventType == BillingAuditEventType.SpendUpdateFailed &&
                billingEvent.VirtualKeyId == 202 &&
                billingEvent.FailureReason == "second failure" &&
                billingEvent.MetadataJson != null &&
                billingEvent.MetadataJson.Contains("2.50"))),
            Times.Once);
    }

    [Fact]
    public async Task SendCriticalAlertAsync_AcrossInstances_NotifiesForEachDistinctAlertKey()
    {
        var firstLogger = new Mock<ILogger<BillingAlertingService>>();
        var secondLogger = new Mock<ILogger<BillingAlertingService>>();
        var firstService = new BillingAlertingService(firstLogger.Object);
        var secondService = new BillingAlertingService(secondLogger.Object);
        var uniqueFailure = Guid.NewGuid().ToString();

        await Task.WhenAll(
            firstService.SendCriticalAlertAsync($"{uniqueFailure}: redis failure", 101),
            secondService.SendCriticalAlertAsync($"{uniqueFailure}: database failure", 202));

        var criticalNotifications = CountLogs(firstLogger, LogLevel.Critical) +
            CountLogs(secondLogger, LogLevel.Critical);

        Assert.Equal(2, criticalNotifications);
    }

    [Fact]
    public async Task SendCriticalAlertAsync_AcrossInstances_SuppressesRepeatedAlertKey()
    {
        var firstLogger = new Mock<ILogger<BillingAlertingService>>();
        var secondLogger = new Mock<ILogger<BillingAlertingService>>();
        var firstService = new BillingAlertingService(firstLogger.Object);
        var secondService = new BillingAlertingService(secondLogger.Object);
        var message = $"{Guid.NewGuid()}: repeated failure";

        await Task.WhenAll(
            firstService.SendCriticalAlertAsync(message, 101),
            secondService.SendCriticalAlertAsync(message, 101));

        var criticalNotifications = CountLogs(firstLogger, LogLevel.Critical) +
            CountLogs(secondLogger, LogLevel.Critical);
        var suppressedNotifications = CountLogs(firstLogger, LogLevel.Warning) +
            CountLogs(secondLogger, LogLevel.Warning);

        Assert.Equal(1, criticalNotifications);
        Assert.Equal(1, suppressedNotifications);
    }

    private static int CountLogs(Mock<ILogger<BillingAlertingService>> logger, LogLevel level) =>
        logger.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(ILogger.Log) &&
            invocation.Arguments[0] is LogLevel actualLevel &&
            actualLevel == level);
}
