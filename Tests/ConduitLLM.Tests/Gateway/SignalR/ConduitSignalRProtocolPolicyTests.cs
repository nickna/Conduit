using AwesomeAssertions;
using ConduitLLM.Core.Extensions;

namespace ConduitLLM.Tests.Gateway.SignalR;

public sealed class ConduitSignalRProtocolPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Jit_build_keeps_messagepack_unless_explicitly_disabled(string? configuredValue)
    {
        ConduitSignalRProtocolPolicy.ResolveMessagePackEnabled(false, configuredValue)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void Jit_build_honors_existing_messagepack_disable_switch(string configuredValue)
    {
        ConduitSignalRProtocolPolicy.ResolveMessagePackEnabled(false, configuredValue)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    public void Native_build_is_always_json_only(string? configuredValue)
    {
        ConduitSignalRProtocolPolicy.ResolveMessagePackEnabled(true, configuredValue)
            .Should().BeFalse();
    }
}
