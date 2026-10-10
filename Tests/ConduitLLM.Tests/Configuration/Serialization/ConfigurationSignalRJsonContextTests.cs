using ConduitLLM.Configuration.Serialization;
using ConduitLLM.Configuration.DTOs.SignalR;

namespace ConduitLLM.Tests.Configuration.Serialization;

[Trait("Category", "Unit")]
[Trait("Component", "SignalRSerialization")]
public sealed class ConfigurationSignalRJsonContextTests
{
    [Theory]
    [InlineData(typeof(string[]))]
    [InlineData(typeof(TaskSubscriptionNotification))]
    [InlineData(typeof(HubErrorNotification))]
    public void GetTypeInfo_IncludesNativeHubPayload(Type payloadType)
    {
        Assert.NotNull(ConfigurationSignalRJsonContext.Default.GetTypeInfo(payloadType));
    }
}
