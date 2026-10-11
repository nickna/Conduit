using System.Text.Json;

using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Serialization;

using AwesomeAssertions;

namespace ConduitLLM.Tests.Admin.Endpoints;

public class HealthMonitoringAggregationTests
{
    private static readonly DateTime Now =
        new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AllHealthyInstances_ProduceHealthyLogicalService()
    {
        var status = HealthMonitoringEndpoints.BuildClusterServiceStatus(
            "core-api",
            "Gateway API",
            [Heartbeat("a", 10), Heartbeat("b", 20)],
            Now);

        status.Status.Should().Be("healthy");
        status.Instances.Should().HaveCount(2);
        var details = JsonSerializer.SerializeToElement(status, AdminJsonOptions.Create()).GetProperty("details");
        details.GetProperty("healthyInstances").GetInt32().Should().Be(2);
        details.EnumerateObject().Should().HaveCount(3);
        details.TryGetProperty("reason", out _).Should().BeFalse();
    }

    [Fact]
    public void HealthyAndStaleInstances_ProduceDegradedLogicalService()
    {
        var status = HealthMonitoringEndpoints.BuildClusterServiceStatus(
            "core-api",
            "Gateway API",
            [Heartbeat("healthy", 10), Heartbeat("stale", 100)],
            Now);

        status.Status.Should().Be("degraded");
        status.Instances.Single(item => item.InstanceId == "stale").Status
            .Should().Be("degraded");
    }

    [Fact]
    public void NoHealthyInstances_ProduceUnhealthyLogicalService()
    {
        var status = HealthMonitoringEndpoints.BuildClusterServiceStatus(
            "admin-api",
            "Admin API",
            [Heartbeat("degraded", 90), Heartbeat("unhealthy", 200)],
            Now);

        status.Status.Should().Be("unhealthy");
    }

    [Fact]
    public void UnrecognizedReportedStatus_ProducesUnknownInstanceStatus()
    {
        var heartbeat = Heartbeat("garbled", 10, "definitely-not-a-status");

        var status = HealthMonitoringEndpoints.BuildClusterServiceStatus(
            "core-api",
            "Gateway API",
            [Heartbeat("ok", 10), heartbeat],
            Now);

        status.Instances.Single(item => item.InstanceId == "garbled").Status
            .Should().Be("unknown");
        status.Status.Should().Be("degraded");
    }

    [Fact]
    public void NoInstances_ProduceUnknownLogicalService()
    {
        var status = HealthMonitoringEndpoints.BuildClusterServiceStatus(
            "admin-api",
            "Admin API",
            [],
            Now);

        status.Status.Should().Be("unknown");
        status.Instances.Should().BeEmpty();
        var details = JsonSerializer.SerializeToElement(status, AdminJsonOptions.Create()).GetProperty("details");
        details.GetProperty("healthyInstances").GetInt32().Should().Be(0);
        details.GetProperty("totalInstances").GetInt32().Should().Be(0);
        details.GetProperty("reason").GetString().Should().Be("No instance heartbeat has been received");
        details.EnumerateObject().Should().HaveCount(4);
    }

    private static ServiceHeartbeatSnapshot Heartbeat(
        string instanceId,
        double ageSeconds,
        string status = "healthy") =>
        new()
        {
            ServiceId = "gateway",
            Heartbeat = new()
            {
                InstanceId = instanceId,
                Version = "3.0.0",
                CommitSha = "abc123",
                BuildTimestamp = "2026-07-23T11:00:00Z",
                Status = status,
                UptimeSeconds = 500,
                IntervalSeconds = 30
            },
            ReceivedAtUtc = Now.AddSeconds(-ageSeconds)
        };
}
