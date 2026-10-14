using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Npgsql;

using StackExchange.Redis;

internal static class NativeDependencyProxyHost
{
    public static async Task RunAsync()
    {
        var database = new NpgsqlConnectionStringBuilder(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        NativeDependencyRoutes.ValidateDatabaseTarget(database);
        var redis = ConfigurationOptions.Parse(ProbeSettings.RedisConnectionStringFromEnvironment());
        var (redisHost, redisPort) = NativeDependencyRoutes.RedisTarget(redis);
        var token = Required("CONDUIT_NATIVE_PROXY_TOKEN");
        var controlUri = new Uri(Required("CONDUIT_NATIVE_PROXY_CONTROL_URL"));
        if (controlUri.Host != "127.0.0.1" || controlUri.Scheme != "http")
            throw new InvalidOperationException("The native proxy control listener must use loopback HTTP.");

        await using var postgresProxy = new SwitchableTcpProxy(database.Host!, database.Port,
            int.Parse(Required("CONDUIT_NATIVE_PROXY_POSTGRES_PORT"), CultureInfo.InvariantCulture));
        await using var redisProxy = new SwitchableTcpProxy(redisHost, redisPort,
            int.Parse(Required("CONDUIT_NATIVE_PROXY_REDIS_PORT"), CultureInfo.InvariantCulture));
        using var control = new TcpListener(IPAddress.Loopback, controlUri.Port);
        control.Start();
        Console.WriteLine("Native dependency proxy listeners started.");
        while (true)
        {
            using var client = await control.AcceptTcpClientAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                postgresProxy.ThrowIfFaulted();
                redisProxy.ThrowIfFaulted();
                var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream, deadline.Token);
                var status = GetStatus(request, token, postgresProxy, redisProxy);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Control\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Console.Error.WriteLine("Native dependency control client exceeded its owned five-second deadline.");
            }
            catch (IOException)
            {
                Console.Error.WriteLine("Native dependency control client disconnected during HTTP framing or response.");
            }
            catch (SocketException)
            {
                Console.Error.WriteLine("Native dependency control client disconnected at the socket transport.");
            }
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var received = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (received == 0)
                return string.Empty;
            length += received;
            var request = Encoding.ASCII.GetString(buffer, 0, length);
            if (request.Contains("\r\n\r\n", StringComparison.Ordinal))
                return request[..request.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        }
        return string.Empty;
    }

    private static int GetStatus(string request, string token, SwitchableTcpProxy postgres, SwitchableTcpProxy redis)
    {
        var lines = request.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1")
            return 400;
        if (first[0] == "GET" && first[1] == "/health")
            return 200;
        var authorization = lines.Skip(1).Where(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (authorization.Length != 1 || !string.Equals(authorization[0][14..].Trim(), $"Bearer {token}", StringComparison.Ordinal))
            return 401;
        var path = first[1];
        if (first[0] != "POST" || path is not ("/postgres/block" or "/postgres/restore" or "/redis/block" or "/redis/restore"))
            return 404;
        var proxy = path.StartsWith("/postgres/", StringComparison.Ordinal) ? postgres : redis;
        proxy.SetBlocked(path.EndsWith("/block", StringComparison.Ordinal));
        return 204;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} is required.");
}
