using ConduitLLM.Core.Caching;

namespace ConduitLLM.Tests.Core.Caching;

public sealed class DiscoveryCacheKeyTests
{
    [Theory]
    [InlineData(null, null, false, "all")]
    [InlineData("chat", null, false, "capability:chat")]
    [InlineData(null, 42, false, "virtualkey:42")]
    [InlineData("chat", 42, false, "virtualkey:42:capability:chat")]
    [InlineData(null, null, true, "all:with_pricing")]
    [InlineData("chat", null, true, "capability:chat:with_pricing")]
    [InlineData(null, 42, true, "virtualkey:42:with_pricing")]
    [InlineData("chat", 42, true, "virtualkey:42:capability:chat:with_pricing")]
    public void VariantsRemainSeparate(string? capability, int? key, bool pricing, string expected) =>
        Assert.Equal(expected, DiscoveryCacheKeys.Build(capability, key, pricing));
}
