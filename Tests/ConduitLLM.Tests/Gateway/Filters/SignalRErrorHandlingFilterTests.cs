using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using ConduitLLM.Gateway.Filters;
using ConduitLLM.Gateway.Metrics;

namespace ConduitLLM.Tests.Gateway.Filters;

[Trait("Category", "Unit")]
[Trait("Component", "SignalRErrorHandlingFilter")]
public sealed class SignalRErrorHandlingFilterTests
{
    [Fact]
    public async Task OnConnectedAsync_WhenDownstreamRejectsConnection_Rethrows()
    {
        using var meterFactory = new TestMeterFactory();
        var filter = new SignalRErrorHandlingFilter(
            Mock.Of<ILogger<SignalRErrorHandlingFilter>>(),
            new SignalRMetrics(meterFactory));
        var callerContext = new Mock<HubCallerContext>();
        callerContext.Setup(context => context.ConnectionId).Returns("rejected-connection");
        var lifetimeContext = new HubLifetimeContext(
            callerContext.Object,
            Mock.Of<IServiceProvider>(),
            Mock.Of<Hub>());
        var rejection = new HubException("connection limit reached");

        var thrown = await Assert.ThrowsAsync<HubException>(() =>
            filter.OnConnectedAsync(lifetimeContext, _ => Task.FromException(rejection)));

        Assert.Same(rejection, thrown);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }
        }
    }
}
