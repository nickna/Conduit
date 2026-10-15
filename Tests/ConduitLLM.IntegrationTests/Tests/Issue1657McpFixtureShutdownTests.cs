using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed class Issue1657McpFixtureShutdownTests
{
    [Fact]
    public async Task CancelledHeldRequestCannotPreventIndependentFixtureShutdownAndListenerClosure()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var stub = new NativeMcpProviderStub(NativeMcpCallMode.HoldFirstList);
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, stub.Endpoint)
        {
            Content = new StringContent("""
                {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"issue-1657","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
                """, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NativeMcpProviderStub.ApiKey);
        request.Headers.Add("MCP-Protocol-Version", NativeMcpProviderStub.ProtocolVersion);
        request.Headers.Add("Mcp-Method", "tools/list");
        var pending = client.SendAsync(request, caller.Token);
        try
        {
            await stub.ListReceived.WaitAsync(deadline.Token);

            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var response = await pending;
            });
            await stub.DisposeAsync().AsTask().WaitAsync(deadline.Token);

            Assert.Equal(0, stub.Counters.ActiveHandlers);
            Assert.Equal(0, stub.Counters.HeldRequests);
            using var refused = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () =>
                await refused.ConnectAsync(IPAddress.Loopback, stub.Endpoint.Port, deadline.Token));
        }
        finally
        {
            // Even a setup assertion failure must observe the task and dispose
            // any HTTP response it produced; fixture cleanup has its own budget.
            caller.Cancel();
            await stub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            try { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                Assert.True(caller.IsCancellationRequested);
            }
        }
    }
}
