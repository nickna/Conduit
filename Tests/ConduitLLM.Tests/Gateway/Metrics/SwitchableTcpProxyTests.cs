using System.Net;
using System.Net.Sockets;

namespace ConduitLLM.Tests.Gateway.Metrics;

public sealed class SwitchableTcpProxyTests
{
    [Fact]
    public async Task BlockClosesExistingAndNewConnections_RestoreForwardsToSameUpstream()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        await using var proxy = new SwitchableTcpProxy("127.0.0.1", ((IPEndPoint)upstream.LocalEndpoint).Port);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token);
        using var server = await upstream.AcceptTcpClientAsync(deadline.Token);
        await AssertForwardedAsync(client, server, deadline.Token);

        proxy.SetBlocked(true);
        await AssertClosedAsync(client, deadline.Token);
        await AssertClosedAsync(server, deadline.Token);
        using var blockedClient = new TcpClient();
        await blockedClient.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token);
        await AssertClosedAsync(blockedClient, deadline.Token);

        proxy.SetBlocked(false);
        using var recoveredClient = new TcpClient();
        await recoveredClient.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token);
        using var recoveredServer = await upstream.AcceptTcpClientAsync(deadline.Token);
        await AssertForwardedAsync(recoveredClient, recoveredServer, deadline.Token);
    }

    [Fact]
    public async Task RestoreWithoutAnOutageClient_ForwardsFirstNewConnection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        await using var proxy = new SwitchableTcpProxy("127.0.0.1", ((IPEndPoint)upstream.LocalEndpoint).Port);
        proxy.SetBlocked(true);
        proxy.SetBlocked(false);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token);
        using var server = await upstream.AcceptTcpClientAsync(deadline.Token);
        await AssertForwardedAsync(client, server, deadline.Token);
    }

    [Fact]
    public async Task DisposeClosesBothPumpsAndListener_LeavesUpstreamAvailable()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var proxy = new SwitchableTcpProxy("127.0.0.1", upstreamPort);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token);
            using var server = await upstream.AcceptTcpClientAsync(deadline.Token);
            await AssertForwardedAsync(client, server, deadline.Token);
            await proxy.DisposeAsync();
            await AssertClosedAsync(client, deadline.Token);
            await AssertClosedAsync(server, deadline.Token);
            using var refused = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () =>
                await refused.ConnectAsync(IPAddress.Loopback, proxy.Port, deadline.Token));
            using var directClient = new TcpClient();
            await directClient.ConnectAsync(IPAddress.Loopback, upstreamPort, deadline.Token);
            using var directServer = await upstream.AcceptTcpClientAsync(deadline.Token);
            await AssertForwardedAsync(directClient, directServer, deadline.Token);
        }
        finally
        {
            await proxy.DisposeAsync();
        }
    }

    private static async Task AssertForwardedAsync(TcpClient client, TcpClient server, CancellationToken token)
    {
        await client.GetStream().WriteAsync(new byte[] { 17, 29 }, token);
        var bytes = new byte[2];
        await server.GetStream().ReadExactlyAsync(bytes, token);
        Assert.Equal(new byte[] { 17, 29 }, bytes);
        await server.GetStream().WriteAsync(new byte[] { 41, 53 }, token);
        await client.GetStream().ReadExactlyAsync(bytes, token);
        Assert.Equal(new byte[] { 41, 53 }, bytes);
    }

    private static async Task AssertClosedAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], token));
        }
        catch (IOException exception) when (exception.InnerException is SocketException) { }
    }
}
