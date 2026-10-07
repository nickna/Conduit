using Xunit;

namespace ConduitLLM.IntegrationTests.Tests.CriticalPath;

/// <summary>Retirement records only; executable replacements are listed in scripts/ci/critical-path-map.json.</summary>
[Trait("CriticalPath", "true")]
public sealed class RequestPipelineIntegrationTests
{
    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Chat Completion - Non-streaming returns complete response")]
    public void ChatCompletion_NonStreaming_ReturnsCompleteResponse() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Chat Completion - Streaming returns SSE events")]
    public void ChatCompletion_Streaming_ReturnsSSEEvents() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Chat Completion - Streaming extracts usage from final chunk")]
    public void ChatCompletion_Streaming_ExtractsUsageFromFinalChunk() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Model Routing - Valid alias routes to correct provider")]
    public void ModelRouting_ValidAlias_RoutesToCorrectProvider() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Model Routing - Unknown model returns 404")]
    public void ModelRouting_UnknownModel_Returns404() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Billing Policy - 400 Bad Request does not deduct spend")]
    public void ClientError_400BadRequest_DoesNotDeductSpend() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Billing Policy - 429 Rate Limited does not deduct spend")]
    public void RateLimited_429_DoesNotDeductSpend() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Billing Policy - Successful request deducts spend")]
    public void Success_200_DeductsSpend() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

}
