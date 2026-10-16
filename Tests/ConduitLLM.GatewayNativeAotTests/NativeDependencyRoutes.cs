using System.Net;

using ConduitLLM.Configuration.Utilities;

using Npgsql;

using StackExchange.Redis;

internal static class NativeDependencyRoutes
{
    public static NpgsqlConnectionStringBuilder ParseDatabase(string value) =>
        PostgresConnectionStringParser.Parse(value);

    public static string RedirectDatabaseUrl(string value, int port)
    {
        var target = ParseDatabase(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("Native host proxy routing requires a PostgreSQL DATABASE_URL URI.");
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("The PostgreSQL proxy requires a valid TCP port.");
        ValidateDatabaseTarget(target);
        if (QueryOptions(uri).Any(IsEndpointOption))
            throw new InvalidOperationException("Host/Port query overrides can bypass the owned PostgreSQL proxy.");

        // UriBuilder normalizes dot segments, including escaped database names.
        // Replace only the transport authority and preserve the supplied values.
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0) authorityEnd = value.Length;
        var authority = value[authorityStart..authorityEnd];
        var userInfoEnd = authority.LastIndexOf('@');
        var userInfo = userInfoEnd >= 0 ? authority[..(userInfoEnd + 1)] : string.Empty;
        return FormattableString.Invariant($"{uri.Scheme}://{userInfo}127.0.0.1:{port}{value[authorityEnd..]}");
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
        try
        {
            return RedisUrlParser.ResolveConfiguration(url, connectionString)
                ?? throw new InvalidOperationException("REDIS_URL or CONDUIT_REDIS_CONNECTION_STRING is required.");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Redis dependency configuration is invalid.");
        }
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
        if (value is not null && (value.Contains(',') || value.Any(char.IsControl)))
            throw new InvalidOperationException("Redis credentials/options cannot be represented by this proxy fixture.");
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
        var query = uri.Query.Length > 0 ? uri.Query[1..] : string.Empty;
        foreach (var parts in query.Split('&', StringSplitOptions.RemoveEmptyEntries)
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
