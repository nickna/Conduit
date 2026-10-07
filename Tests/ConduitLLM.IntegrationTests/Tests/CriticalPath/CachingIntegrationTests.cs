using Xunit;

namespace ConduitLLM.IntegrationTests.Tests.CriticalPath;

/// <summary>Retirement records only; executable replacements are listed in scripts/ci/critical-path-map.json.</summary>
[Trait("CriticalPath", "true")]
public sealed class CachingIntegrationTests
{
    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Redis Unavailable - Health endpoints still work")]
    public void RedisUnavailable_HealthEndpointsStillWork() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Redis Unavailable - Circuit opens after failures")]
    public void RedisUnavailable_CircuitOpens_Returns503() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Redis Recovery - Circuit closes after successful reconnection")]
    public void RedisRecovery_CircuitCloses_NormalOperation() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Redis Down - Requests still processed with graceful fallback")]
    public void RedisDown_RequestsStillProcessed_GracefulFallback() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

    [Fact(Skip = "Retired external harness (#1464). Execution ownership: scripts/ci/critical-path-map.json; run the required replacement suite.", DisplayName = "Redis Flush - Clears cached data for test isolation")]
    public void RedisFlush_ClearsCachedData_TestIsolation() =>
        throw new InvalidOperationException("Retired harness: implement the mapped acceptance scenario before enabling a test.");

}
