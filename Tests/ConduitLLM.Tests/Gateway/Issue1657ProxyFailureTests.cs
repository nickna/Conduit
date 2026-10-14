using System.Net;
using System.Net.Sockets;

namespace ConduitLLM.Tests.Gateway;

public sealed class Issue1657ProxyFailureTests
{
    [Fact]
    public async Task UnblockedUpstreamRefusalIsObservableAndShutdownStillDrainsOwnedSockets()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var unavailable = new TcpListener(IPAddress.Loopback, 0);
        unavailable.Start();
        var port = ((IPEndPoint)unavailable.LocalEndpoint).Port;
        unavailable.Stop();
        var proxy = new SwitchableTcpProxy("127.0.0.1", port);
        using var client = new TcpClient();
        Exception? recordedFailure = null;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token);
            // The handler records refusal before closing the accepted socket.
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], deadline.Token));
            var failure = Assert.Throws<InvalidOperationException>(proxy.ThrowIfFaulted);
            Assert.IsType<SocketException>(failure.InnerException);
            recordedFailure = failure.InnerException;
            var shutdown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await proxy.DisposeAsync());
            Assert.Same(failure.InnerException, shutdown.InnerException);
            using var rejected = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () =>
                await rejected.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token));
        }
        finally
        {
            try { await proxy.DisposeAsync(); }
            catch (InvalidOperationException error) when (recordedFailure is not null)
            {
                // Repeated disposal reports the original recorded cause.
                Assert.Same(recordedFailure, error.InnerException);
            }
        }
    }
}
