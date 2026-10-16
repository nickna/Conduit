using ConduitLLM.Core.Constants;

namespace ConduitLLM.Tests.Core.Constants;

public sealed class SignalRConstantsTests
{
    [Theory]
    [InlineData("http://hooks.example.com/orders?source=a")]
    [InlineData("https://hooks.example.com:8443/orders?source=a")]
    [InlineData("https://hooks.example.com/orders?source=b")]
    [InlineData("https://hooks.example.com/Orders?source=a")]
    [InlineData("https://hooks-example.com/orders?source=a")]
    public void Webhook_PartitionsEveryDestinationComponent(string otherDestination)
    {
        Assert.NotEqual(SignalRConstants.Groups.Webhook(11, "https://hooks.example.com/orders?source=a"),
            SignalRConstants.Groups.Webhook(11, otherDestination));
    }

    [Fact]
    public void Webhook_PartitionsOwnersWithoutExposingDestinationDetails()
    {
        const string url = "https://hooks.example.com/orders?token=fixture-only";
        var ownerA = SignalRConstants.Groups.Webhook(11, url);
        var ownerB = SignalRConstants.Groups.Webhook(22, url);
        Assert.NotEqual(ownerA, ownerB);
        Assert.Matches("^webhook-11-[0-9a-f]{64}$", ownerA);
        Assert.DoesNotContain("hooks", ownerA);
        Assert.DoesNotContain("fixture-only", ownerA);
    }

    [Fact]
    public void Webhook_NormalizesEquivalentAbsoluteUris()
    {
        Assert.Equal(SignalRConstants.Groups.Webhook(11, "HTTPS://HOOKS.EXAMPLE.COM:443/orders"),
            SignalRConstants.Groups.Webhook(11, "https://hooks.example.com/orders"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Webhook_RejectsUnverifiableOwners(int owner)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalRConstants.Groups.Webhook(owner, "https://hooks.example.com/"));
    }
}
