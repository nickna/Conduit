namespace ConduitLLM.Tests.Core.Caching;

public sealed class RedisNetworkProxyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WithPendingAccept_Completes(bool disconnected)
    {
        // Concurrent shutdowns exercise the race between cancellation and stopping the listener.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var proxy = new RedisNetworkProxy("127.0.0.1:1");
                if (disconnected) proxy.Disconnect();
                await proxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        })));
    }
}
