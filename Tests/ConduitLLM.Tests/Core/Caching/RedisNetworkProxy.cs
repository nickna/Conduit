using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Core.Caching;

/// <summary>A fixture-only TCP disconnect switch; all cache and pub/sub traffic still reaches real Redis.</summary>
internal sealed class RedisNetworkProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Server)> _connections = new();
    private readonly ConcurrentBag<Task> _forwarders = [];
    private readonly string _host;
    private readonly int _port;
    private readonly Task _acceptor;
    private volatile bool _blocked;
    private int _nextId;
    public string ConnectionString { get; }

    public RedisNetworkProxy(string redis)
    {
        var options = ConfigurationOptions.Parse(redis);
        if (options.Ssl || options.EndPoints.Count != 1) throw new InvalidOperationException("The disconnect fixture requires one non-TLS Redis endpoint.");
        (_host, _port) = options.EndPoints[0] switch
        {
            DnsEndPoint endpoint => (endpoint.Host, endpoint.Port),
            IPEndPoint endpoint => (endpoint.Address.ToString(), endpoint.Port),
            _ => throw new InvalidOperationException("Unsupported fixture endpoint.")
        };
        _listener.Start();
        options.EndPoints.Clear();
        options.EndPoints.Add((IPEndPoint)_listener.LocalEndpoint);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 250;
        options.AsyncTimeout = 250;
        options.SyncTimeout = 250;
        options.ConnectRetry = 0;
        ConnectionString = options.ToString();
        _acceptor = AcceptAsync();
    }

    public void Disconnect()
    {
        _blocked = true;
        foreach (var pair in _connections.Values) { pair.Client.Dispose(); pair.Server.Dispose(); }
    }

    public void Reconnect() => _blocked = false;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                if (_blocked) { client.Dispose(); continue; }
                _forwarders.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        var id = Interlocked.Increment(ref _nextId);
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                await server.ConnectAsync(_host, _port, _lifetime.Token);
                _connections[id] = (client, server);
                if (_blocked) return;
                var upstream = client.GetStream().CopyToAsync(server.GetStream(), _lifetime.Token);
                var downstream = server.GetStream().CopyToAsync(client.GetStream(), _lifetime.Token);
                await Task.WhenAny(upstream, downstream);
                client.Dispose();
                server.Dispose();
                await Task.WhenAll(upstream, downstream);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { _connections.TryRemove(id, out _); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        _lifetime.Cancel();
        _listener.Stop();
        await _acceptor;
        await Task.WhenAll(_forwarders).WaitAsync(TimeSpan.FromSeconds(5));
        _lifetime.Dispose();
    }
}
