namespace ConduitLLM.Tests.Core.Caching;

// Real Redis read/reconnect budgets should not race unrelated host startup and
// coverage-instrumented JIT work. All cases still execute in the required main suite.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApplicationCacheContractCollection
{
    public const string Name = "ApplicationCacheContracts";
}
