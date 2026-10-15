using System.Net;
using System.Net.Sockets;

internal sealed class SwitchableTcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    // These sources span accept/forward tasks. The terminal using scopes in
    // DisposeCoreAsync retain ownership until its socket/task drain completes.
    private readonly CancellationTokenSource _stopping;
    private CancellationTokenSource _acceptGeneration;
    private readonly object _gate = new();
    private readonly HashSet<Connection> _connections = [];
    private readonly Task _accepting;
    private bool _blocked;
    private int _generation;
    private Task? _disposing;
    private Exception? _unexpectedFailure;

    public SwitchableTcpProxy(string upstreamHost, int upstreamPort, int listenPort = 0)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _stopping = new CancellationTokenSource();
        _acceptGeneration = new CancellationTokenSource();
        try { _listener.Start(); }
        catch
        {
            // A failed constructor has no instance available for DisposeAsync.
            _listener.Dispose();
            _stopping.Dispose();
            _acceptGeneration.Dispose();
            throw;
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int Port { get; }

    public void SetBlocked(bool blocked)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            _blocked = blocked;
            if (!blocked)
                return;
            ++_generation;
            // Cancel the old pending accept as well as registered connections.
            // Its generation must not reject the first client after restoration.
            var previous = _acceptGeneration;
            _acceptGeneration = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            foreach (var connection in _connections)
                connection.Close();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                int generation;
                CancellationToken acceptingToken;
                lock (_gate)
                {
                    generation = _generation;
                    acceptingToken = _acceptGeneration.Token;
                }
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(acceptingToken);
                }
                catch (OperationCanceledException) when (!_stopping.IsCancellationRequested && acceptingToken.IsCancellationRequested)
                {
                    continue;
                }
                lock (_gate)
                {
                    if (_blocked || _stopping.IsCancellationRequested || generation != _generation)
                    {
                        client.Dispose();
                        continue;
                    }
                    // Register both sockets before connecting upstream: blocking
                    // also closes in-flight connection attempts, not just pumps.
                    var connection = new Connection(client);
                    _connections.Add(connection);
                    _ = ForwardAsync(connection);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
        catch (SocketException) when (_stopping.IsCancellationRequested) { return; }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { return; }
        catch (Exception exception) { RecordFailure(exception); }
    }

    private async Task ForwardAsync(Connection connection)
    {
        try
        {
            await connection.Upstream.ConnectAsync(_upstreamHost, _upstreamPort, _stopping.Token);
            using var pumping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var downstream = connection.Client.GetStream();
            var upstream = connection.Upstream.GetStream();
            var requests = downstream.CopyToAsync(upstream, pumping.Token);
            var responses = upstream.CopyToAsync(downstream, pumping.Token);
            await Task.WhenAny(requests, responses);
            pumping.Cancel();
            connection.Close();
            await Task.WhenAll(requests, responses);
        }
        // Blocking, shutdown and the first completed pump close both sockets.
        // Failures before that owned close (including upstream refusal) remain visible.
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested || connection.IsClosed) { return; }
        catch (IOException) when (_stopping.IsCancellationRequested || connection.IsClosed) { return; }
        catch (SocketException) when (_stopping.IsCancellationRequested || connection.IsClosed) { return; }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested || connection.IsClosed) { return; }
        catch (InvalidOperationException) when (connection.IsClosed) { return; }
        catch (Exception exception) { RecordFailure(exception); }
        finally
        {
            connection.Close();
            lock (_gate) _connections.Remove(connection);
            connection.Completed.SetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new ValueTask(_disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        // DisposeAsync invokes this method while holding _gate. Capture the
        // exact final generation before shutdown; SetBlocked cannot replace it
        // after stopping is canceled. The scopes also cover shutdown failures
        // before the first await, as well as a failed or timed-out drain.
        using var acceptGeneration = _acceptGeneration;
        using var stopping = _stopping;
        Task[] forwarding;
        lock (_gate)
        {
            stopping.Cancel();
            acceptGeneration.Cancel();
            _listener.Stop();
            foreach (var connection in _connections)
                connection.Close();
            forwarding = _connections.Select(connection => connection.Completed.Task).ToArray();
        }
        await Task.WhenAll(forwarding.Append(_accepting)).WaitAsync(TimeSpan.FromSeconds(10));
        ThrowIfFaulted();
    }

    public void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _unexpectedFailure) is { } failure)
            throw new InvalidOperationException("Native dependency proxy failed unexpectedly.", failure);
    }

    private void RecordFailure(Exception exception)
    {
        Interlocked.CompareExchange(ref _unexpectedFailure, exception, null);
        Console.Error.WriteLine("Native dependency proxy failed. " + NativeFixtureDiagnostics.Describe(exception));
    }

    private sealed class Connection(TcpClient client)
    {
        private int _closed;
        public TcpClient Client { get; } = client;
        public TcpClient Upstream { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            Client.Dispose();
            Upstream.Dispose();
        }
    }
}
