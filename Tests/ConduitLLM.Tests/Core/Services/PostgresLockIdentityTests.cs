using ConduitLLM.Core.Services;
using AwesomeAssertions;

namespace ConduitLLM.Tests.Core.Services;

[Trait("Category", "Unit")]
[Trait("Component", "DistributedLock")]
public sealed class PostgresLockIdentityTests
{
    [Theory]
    [InlineData("media:cleanup:leader", -6767408278747800924L)]
    [InlineData("openrouter:metadata-sync:leader", 853480829366151680L)]
    [InlineData("lock:alert:vk:42:threshold:80", 6244119389078348034L)]
    [InlineData("cache:discovery:all", -2259516046294963932L)]
    [InlineData("discovery:cache:warming", -7084440215188924688L)]
    [InlineData("conduit:connectionpool:warming:CoreAPI", 8458402910011165425L)]
    [InlineData("媒体:🔒", -5787207717908349635L)]
    [InlineData("Aa", 650843846546077079L)]
    [InlineData("BB", 653890593267282085L)]
    public void NumericIdentity_MatchesLegacyGoldenValues(string key, long expected)
    {
        PostgresLockIdentity.GetLockId(key).Should().Be(expected);
    }

    [Fact]
    public void GetLockId_IsStableAndAvoidsKnownThirtyOneHashCollision()
    {
        var first = PostgresLockIdentity.GetLockId("media:cleanup:leader");
        var second = PostgresLockIdentity.GetLockId("media:cleanup:leader");

        first.Should().Be(second);
        PostgresLockIdentity.GetLockId("Aa")
            .Should().NotBe(PostgresLockIdentity.GetLockId("BB"));
    }
}
