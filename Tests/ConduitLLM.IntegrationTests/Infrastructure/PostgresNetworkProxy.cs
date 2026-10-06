using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>Fixture-only TCP blackhole. PostgreSQL stays connected until fixture teardown.</summary>
internal sealed class PostgresNetworkProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentBag<Task> _forwarders = [];
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Server)> _connections = new();
    private readonly string _host;
    private readonly int _port;
    private readonly Task _acceptor;
    private volatile bool _blackhole;
    private volatile bool _observeKeepalive;
    private readonly TaskCompletionSource _keepalive = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;
    public string ConnectionString { get; }

    public PostgresNetworkProxy(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString);
        _host = options.Host!;
        _port = options.Port;
        _listener.Start();
        options.Host = "127.0.0.1";
        options.Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        options.SslMode = SslMode.Disable;
        ConnectionString = options.ConnectionString;
        _acceptor = AcceptAsync();
    }

    public void Blackhole() => _blackhole = true;

    public Task WaitForKeepaliveAsync()
    {
        _observeKeepalive = true;
        return _keepalive.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _forwarders.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task PumpAsync(Stream source, Stream destination, bool upstream)
    {
        var buffer = new byte[8192];
        while (await source.ReadAsync(buffer, _lifetime.Token) is var length && length != 0)
        {
            if (!_blackhole)
            {
                await destination.WriteAsync(buffer.AsMemory(0, length), _lifetime.Token);
                // Npgsql's idle keepalive is the five-byte PostgreSQL Sync message.
                if (upstream && _observeKeepalive && length == 5 && buffer[0] == (byte)'S') { _keepalive.TrySetResult(); }
            }
        }
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
                var upstream = PumpAsync(client.GetStream(), server.GetStream(), true);
                var downstream = PumpAsync(server.GetStream(), client.GetStream(), false);
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
        _lifetime.Cancel();
        _listener.Stop();
        foreach (var pair in _connections.Values) { pair.Client.Dispose(); pair.Server.Dispose(); }
        await _acceptor;
        await Task.WhenAll(_forwarders).WaitAsync(TimeSpan.FromSeconds(5));
        _lifetime.Dispose();
    }
}
