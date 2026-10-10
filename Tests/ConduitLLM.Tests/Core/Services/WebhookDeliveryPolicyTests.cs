using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Core.Services;

public sealed class WebhookDeliveryPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(Now); }
    private static WebhookDeliveryPolicy Policy(double random = 0.5) => new(Options.Create(new WebhookDeliveryOptions()), new Clock(), () => random);

    [Theory]
    [InlineData(400, false)] [InlineData(401, false)] [InlineData(403, false)] [InlineData(404, false)]
    [InlineData(408, true)] [InlineData(429, true)] [InlineData(500, true)] [InlineData(501, false)]
    [InlineData(502, true)] [InlineData(503, true)] [InlineData(504, true)] [InlineData(505, false)]
    [InlineData(307, false)]
    public void StatusClassification_IsExplicit(int status, bool retryable) =>
        Assert.Equal(retryable, WebhookDeliveryPolicy.IsRetryable(WebhookSendResult.Failed(status, "test")));

    [Fact]
    public void TimeoutAndNetwork_RetryButInvalidRequestDoesNot()
    {
        Assert.True(WebhookDeliveryPolicy.IsRetryable(WebhookSendResult.Failed(null, "test", WebhookFailureKind.Timeout)));
        Assert.True(WebhookDeliveryPolicy.IsRetryable(WebhookSendResult.Failed(null, "test", WebhookFailureKind.Network)));
        Assert.False(WebhookDeliveryPolicy.IsRetryable(WebhookSendResult.Failed(null, "test", WebhookFailureKind.InvalidRequest)));
    }

    [Fact]
    public void TerminalAndProgress_UseDifferentWindowsAndLegacyTimestamp()
    {
        var policy = Policy();
        var terminal = new WebhookDeliveryRequested { Timestamp = Now.AddMinutes(-1), EventType = WebhookEventType.TaskCompleted };
        Assert.Equal(Now.AddHours(24).AddMinutes(-1), policy.Deadline(terminal));
        Assert.Equal(Now.AddMinutes(4), policy.Deadline(terminal with { EventType = WebhookEventType.TaskProgress }));
        Assert.Equal(Now.AddHours(24), policy.Deadline(terminal with { DeliveryStartedAt = Now }));
    }

    [Fact]
    public void BackoffJitterRetryAfterAndDeferral_AreBoundedByDeadline()
    {
        var deadline = Now.AddDays(1);
        Assert.Equal(Now.AddSeconds(2), Policy().RetryAt(1, deadline, null));
        Assert.Equal(Now.AddSeconds(4), Policy().RetryAt(2, deadline, null));
        Assert.Equal(Now.AddSeconds(1.6), Policy(0).RetryAt(1, deadline, null));
        Assert.Equal(Now.AddMilliseconds(2400), Policy(1).RetryAt(1, deadline, null));
        Assert.Equal(Now.AddHours(1), Policy().RetryAt(100, deadline, null));
        Assert.Equal(Now.AddHours(1), Policy().RetryAt(1, deadline, TimeSpan.FromDays(1)));
        Assert.Equal(Now.AddSeconds(2), Policy().RetryAt(1, deadline, TimeSpan.FromSeconds(-1)));
        Assert.Equal(Now.AddSeconds(1), Policy().RetryAt(1, Now.AddSeconds(1), TimeSpan.FromHours(1)));
        Assert.Equal(Now.AddSeconds(1), Policy().DeferUntil(Now.AddSeconds(1)));
    }
}
