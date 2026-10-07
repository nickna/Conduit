using System.Text.Json;

using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Moq;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class StoreBackedRequestLogRuntimeWriterTests
{
    [Fact]
    public async Task LogRequestAsyncMapsTheCompleteRuntimeRecord()
    {
        var timestamp = new DateTime(2026, 8, 27, 23, 0, 0, DateTimeKind.Utc);
        using var metadataValue = JsonDocument.Parse("""{"nested":true}""");
        var request = new LogRequestDto
        {
            VirtualKeyId = 41,
            ModelName = "gpt-native",
            ProviderId = 12,
            ProviderType = "OpenAI",
            ModelProviderMappingId = 77,
            PromptCachingEligible = true,
            PromptCachingPolicyApplied = true,
            CachedReadSavings = 0.2m,
            CacheWritePremium = 0.05m,
            RoutingAffinityUsed = true,
            RoutingDecisionReason = "cache-affinity",
            RoutingFailoverCount = 1,
            RequestType = "chat",
            InputTokens = 100,
            OutputTokens = 25,
            CachedInputTokens = 80,
            CachedWriteTokens = 10,
            Cost = 1.25m,
            BillingMethod = RequestBillingMethod.ProviderReportedCost,
            ProviderReportedCostUsd = 1m,
            ProviderCostMarkupMultiplier = 1.25m,
            ResponseTimeMs = 42.5,
            Timestamp = timestamp,
            UserId = "user-1",
            ClientIp = "192.0.2.10",
            RequestPath = "/v1/chat/completions",
            StatusCode = 200,
            Metadata = new Dictionary<string, JsonElement>
            {
                ["details"] = metadataValue.RootElement.Clone()
            }
        };
        RequestLogRuntimeRecord? captured = null;
        var store = new Mock<IRequestLogRuntimeStore>(MockBehavior.Strict);
        store.Setup(candidate => candidate.WriteAsync(
                It.IsAny<RequestLogRuntimeRecord>(),
                It.IsAny<CancellationToken>()))
            .Callback<RequestLogRuntimeRecord, CancellationToken>((record, _) => captured = record)
            .ReturnsAsync(17);

        await new StoreBackedRequestLogRuntimeWriter(store.Object)
            .LogRequestAsync(request);

        store.VerifyAll();
        Assert.NotNull(captured);
        Assert.Equal(request.VirtualKeyId, captured.VirtualKeyId);
        Assert.Equal(request.ModelName, captured.ModelName);
        Assert.Equal(request.ProviderId, captured.ProviderId);
        Assert.Equal(request.ProviderType, captured.ProviderType);
        Assert.Equal(request.ModelProviderMappingId, captured.ModelProviderMappingId);
        Assert.Equal(request.PromptCachingEligible, captured.PromptCachingEligible);
        Assert.Equal(request.PromptCachingPolicyApplied, captured.PromptCachingPolicyApplied);
        Assert.Equal(request.CachedReadSavings, captured.CachedReadSavings);
        Assert.Equal(request.CacheWritePremium, captured.CacheWritePremium);
        Assert.Equal(request.RoutingAffinityUsed, captured.RoutingAffinityUsed);
        Assert.Equal(request.RoutingDecisionReason, captured.RoutingDecisionReason);
        Assert.Equal(request.RoutingFailoverCount, captured.RoutingFailoverCount);
        Assert.Equal(request.RequestType, captured.RequestType);
        Assert.Equal(request.InputTokens, captured.InputTokens);
        Assert.Equal(request.OutputTokens, captured.OutputTokens);
        Assert.Equal(request.CachedInputTokens, captured.CachedInputTokens);
        Assert.Equal(request.CachedWriteTokens, captured.CachedWriteTokens);
        Assert.Equal(request.Cost, captured.Cost);
        Assert.Equal((int)request.BillingMethod.Value, captured.BillingMethod);
        Assert.Equal(request.ProviderReportedCostUsd, captured.ProviderReportedCostUsd);
        Assert.Equal(request.ProviderCostMarkupMultiplier, captured.ProviderCostMarkupMultiplier);
        Assert.Equal(timestamp, captured.BilledAtUtc);
        Assert.Equal(request.ResponseTimeMs, captured.ResponseTimeMs);
        Assert.Equal(timestamp, captured.Timestamp);
        Assert.Equal(request.UserId, captured.UserId);
        Assert.Equal(request.ClientIp, captured.ClientIp);
        Assert.Equal(request.RequestPath, captured.RequestPath);
        Assert.Equal(request.StatusCode, captured.StatusCode);
        using var metadata = JsonDocument.Parse(captured.MetadataJson!);
        Assert.True(metadata.RootElement.GetProperty("details").GetProperty("nested").GetBoolean());
    }

    [Fact]
    public async Task ZeroCostRequestRemainsUnbilled()
    {
        RequestLogRuntimeRecord? captured = null;
        var store = new Mock<IRequestLogRuntimeStore>();
        store.Setup(candidate => candidate.WriteAsync(
                It.IsAny<RequestLogRuntimeRecord>(),
                It.IsAny<CancellationToken>()))
            .Callback<RequestLogRuntimeRecord, CancellationToken>((record, _) => captured = record)
            .ReturnsAsync(1);
        var timestamp = new DateTime(2026, 8, 27, 23, 30, 0, DateTimeKind.Utc);

        await new StoreBackedRequestLogRuntimeWriter(store.Object).LogRequestAsync(new LogRequestDto
        {
            VirtualKeyId = 1,
            ModelName = "gpt-native",
            RequestType = "chat",
            Timestamp = timestamp,
            Cost = 0,
            BilledAtUtc = timestamp
        });

        Assert.NotNull(captured);
        Assert.Null(captured.BilledAtUtc);
    }
}
