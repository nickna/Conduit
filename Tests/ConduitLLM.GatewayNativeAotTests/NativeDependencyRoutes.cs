using System.Globalization;
using System.Net;

using Npgsql;

using StackExchange.Redis;

internal static class NativeDependencyRoutes
{
    public static NpgsqlConnectionStringBuilder ParseDatabase(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return new NpgsqlConnectionStringBuilder(value);
        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var result = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty
        };
        foreach (var (key, option) in QueryOptions(uri))
            result[key] = option;
        return result;
    }

    public static string RedirectDatabaseUrl(string value, int port)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("Native host proxy routing requires a PostgreSQL DATABASE_URL URI.");
        var target = ParseDatabase(value);
        ValidateDatabaseTarget(target);
        if (QueryOptions(uri).Any(IsEndpointOption))
            throw new InvalidOperationException("Host/Port query overrides can bypass the owned PostgreSQL proxy.");
        return new UriBuilder(uri) { Host = "127.0.0.1", Port = port }.Uri.AbsoluteUri;
    }

    private static bool IsEndpointOption(KeyValuePair<string, string> pair)
    {
        // Let Npgsql canonicalize aliases such as Server before deciding whether
        // a preserved query option could override the rewritten URI endpoint.
        var option = new NpgsqlConnectionStringBuilder { [pair.Key] = pair.Value };
        return option.ConnectionString.StartsWith("Host=", StringComparison.OrdinalIgnoreCase) ||
               option.ConnectionString.StartsWith("Port=", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateDatabaseTarget(NpgsqlConnectionStringBuilder target)
    {
        if (target.Host is not { Length: > 0 } host || host.Contains(',') || host.StartsWith('/'))
            throw new InvalidOperationException("The native proxy fixture requires one PostgreSQL TCP host.");
        if (target.SslMode == SslMode.VerifyFull)
            throw new InvalidOperationException("VerifyFull PostgreSQL TLS identity cannot be preserved by this loopback proxy fixture.");
    }

    public static ConfigurationOptions ParseRedis(string? url, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(url))
            return ConfigurationOptions.Parse(connectionString is { Length: > 0 } value
                ? value : throw new InvalidOperationException("REDIS_URL or CONDUIT_REDIS_CONNECTION_STRING is required."));
        if (!url.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
            return ConfigurationOptions.Parse(url);
        var uri = new Uri(url);
        var result = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 10000,
            SyncTimeout = 10000,
            Ssl = uri.Scheme == "rediss"
        };
        result.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);
        if (uri.UserInfo.Length > 0)
        {
            var userInfo = uri.UserInfo.Split(':', 2);
            if (userInfo.Length != 2)
                throw new InvalidOperationException("Redis URI credentials must use username:password or :password.");
            if (userInfo[0].Length > 0) result.User = Uri.UnescapeDataString(userInfo[0]);
            result.Password = Uri.UnescapeDataString(userInfo[1]);
            ValidateRedisCredential(result.User);
            ValidateRedisCredential(result.Password);
        }
        var database = uri.AbsolutePath.Trim('/');
        if (database.Length > 0)
            result.DefaultDatabase = int.Parse(database, CultureInfo.InvariantCulture);
        var query = QueryOptions(uri).Select(pair =>
        {
            ValidateRedisValue(pair.Key);
            ValidateRedisValue(pair.Value);
            return $"{pair.Key}={pair.Value}";
        }).ToArray();
        return query.Length == 0 ? result : ConfigurationOptions.Parse(result.ToString(includePassword: true) + "," + string.Join(',', query));
    }

    public static string RedirectRedis(ConfigurationOptions original, int port)
    {
        var (host, _) = RedisTarget(original);
        ValidateRedisCredential(original.User);
        ValidateRedisCredential(original.Password);
        ValidateRedisValue(original.SslHost);
        var redirected = original.Clone();
        // SslHost can be inferred from the original endpoint without being an
        // explicit serialized option. Assign it before replacing that endpoint.
        if (redirected.Ssl) redirected.SslHost = original.SslHost is { Length: > 0 } identity ? identity : host;
        redirected.EndPoints.Clear();
        redirected.EndPoints.Add("127.0.0.1", port);
        var serialized = redirected.ToString(includePassword: true);
        var roundTrip = ConfigurationOptions.Parse(serialized);
        if (RedisTarget(roundTrip) != ("127.0.0.1", port) ||
            (roundTrip.User ?? string.Empty) != (redirected.User ?? string.Empty) ||
            (roundTrip.Password ?? string.Empty) != (redirected.Password ?? string.Empty) ||
            roundTrip.Ssl != redirected.Ssl ||
            (roundTrip.Ssl && roundTrip.SslHost != redirected.SslHost))
            throw new InvalidOperationException("Redis proxy options could not preserve the endpoint, authentication or TLS identity.");
        return serialized;
    }

    private static void ValidateRedisValue(string? value)
    {
        if (value?.Contains(',') == true)
            throw new InvalidOperationException("Comma-containing Redis URI credentials/options cannot be represented by this proxy fixture.");
    }

    private static void ValidateRedisCredential(string? value)
    {
        ValidateRedisValue(value);
        if (value is not null && value != value.Trim())
            throw new InvalidOperationException("Redis URI credential whitespace cannot be preserved by this proxy fixture.");
    }

    public static (string Host, int Port) RedisTarget(ConfigurationOptions options)
    {
        if (options.EndPoints.Count != 1 || !string.IsNullOrEmpty(options.ServiceName))
            throw new InvalidOperationException("The native proxy fixture requires one Redis endpoint without Sentinel discovery.");
        return options.EndPoints[0] switch
        {
            DnsEndPoint endpoint => (endpoint.Host, endpoint.Port),
            IPEndPoint endpoint => (endpoint.Address.ToString(), endpoint.Port),
            _ => throw new InvalidOperationException("Unsupported Redis proxy endpoint.")
        };
    }

    private static IEnumerable<KeyValuePair<string, string>> QueryOptions(Uri uri)
    {
        foreach (var parts in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                     .Select(pair => pair.Split('=', 2)))
        {
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("Dependency URI options require key=value.");
            }
            yield return new KeyValuePair<string, string>(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
        }
    }
}
