using Xunit;

namespace ConduitLLM.IntegrationTests.Tests.CriticalPath;

/// <summary>Retirement records only; executable replacements are listed in scripts/ci/critical-path-map.json.</summary>
[Trait("CriticalPath", "true")]
public sealed class ProviderFailoverIntegrationTests
{
    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Provider with invalid key - Fails gracefully with error response")]
    public void ProviderWithInvalidKey_FailsGracefully() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Provider disabled - Returns 404 Not Found")]
    public void ProviderDisabled_Returns404() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Model mapping disabled - Returns 404 Not Found")]
    public void ModelMappingDisabled_Returns404() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Consecutive failures - Provider continues to accept requests")]
    public void ConsecutiveFailures_StillAcceptsRequests() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Recovery after failure - Successful request after fixing provider")]
    public void RecoveryAfterFailure_SuccessfulRequest() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Provider timeout - Returns gateway timeout error")]
    public void ProviderTimeout_ReturnsGatewayTimeout() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

}
