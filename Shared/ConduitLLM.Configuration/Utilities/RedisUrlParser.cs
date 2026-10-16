using System.Globalization;
using System.Net;

using StackExchange.Redis;

namespace ConduitLLM.Configuration.Utilities;

/// <summary>Parses the shared Redis environment configuration contract.</summary>
public static class RedisUrlParser
{
    /// <summary>Resolves REDIS_URL first, falling back only when it is absent or blank.</summary>
    public static ConfigurationOptions? ResolveConfiguration() => ResolveConfiguration(
        Environment.GetEnvironmentVariable("REDIS_URL"),
        Environment.GetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING"));

    /// <summary>Resolves explicit values using the same precedence and validation as the hosts.</summary>
    public static ConfigurationOptions? ResolveConfiguration(string? url, string? connectionString)
    {
        var value = !string.IsNullOrWhiteSpace(url) ? url : connectionString;
        return string.IsNullOrWhiteSpace(value) ? null : ParseConfiguration(value);
    }

    /// <summary>Returns validated canonical options, or null when Redis is unconfigured.</summary>
    public static string? ResolveConnectionString() => ResolveConfiguration()?.ToString(includePassword: true);

    /// <summary>Parses a Redis URI, TCP endpoint, or StackExchange.Redis options string.</summary>
    public static string ParseRedisUrl(string redisUrl) => ParseConfiguration(redisUrl).ToString(includePassword: true);

    /// <summary>
    /// Parses Redis configuration without exposing credentials in failure diagnostics.
    /// URI credentials are percent-decoded once; options strings retain literal values.
    /// </summary>
    public static ConfigurationOptions ParseConfiguration(string configuration)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configuration) || configuration != configuration.Trim() ||
                configuration.Any(char.IsControl))
                throw InvalidConfiguration();

            ConfigurationOptions options;
            if (configuration.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) ||
                configuration.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
                options = ParseUri(configuration);
            else
            {
                if (configuration.Contains("://", StringComparison.Ordinal)) throw InvalidConfiguration();
                ValidateRawValues(configuration);
                options = ConfigurationOptions.Parse(configuration);
                options.SetDefaultPorts();
                if (!HasOption(configuration, "abortConnect")) options.AbortOnConnectFail = false;
                if (!HasOption(configuration, "connectTimeout")) options.ConnectTimeout = 10000;
                if (!HasOption(configuration, "syncTimeout")) options.SyncTimeout = 10000;
            }
            ValidateOptions(options);
            return options;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
                                           OverflowException or InvalidOperationException)
        {
            // Third-party parser messages can include the complete input. Do not
            // attach the original exception, even as an inner exception.
            throw InvalidConfiguration();
        }
    }

    private static ConfigurationOptions ParseUri(string value)
    {
        if (value.Any(char.IsWhiteSpace) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Fragment.Length != 0 || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw InvalidConfiguration();
        ValidatePercentEscapes(value);
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0) authorityEnd = value.Length;
        var authority = value[authorityStart..authorityEnd];
        if (authority.EndsWith(':')) throw InvalidConfiguration();

        var options = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 10000,
            SyncTimeout = 10000,
            Ssl = uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase)
        };
        var host = uri.IdnHost.Trim('[', ']');
        var port = uri.Port < 0 ? 6379 : uri.Port;
        if (port is < 1 or > 65535) throw InvalidConfiguration();
        options.EndPoints.Add(host, port);

        if (authority.Contains('@'))
        {
            var credentials = uri.UserInfo.Split(':', 2);
            if (credentials.Length != 2) throw InvalidConfiguration();
            options.User = credentials[0].Length == 0 ? null : Uri.UnescapeDataString(credentials[0]);
            options.Password = Uri.UnescapeDataString(credentials[1]);
            if (options.Password.Length == 0) throw InvalidConfiguration();
            ValidateSerializableValue(options.User);
            ValidateSerializableValue(options.Password);
        }

        var pathEnd = value.IndexOfAny(['?', '#'], authorityEnd);
        if (pathEnd < 0) pathEnd = value.Length;
        var path = Uri.UnescapeDataString(value[authorityEnd..pathEnd]);
        if (path.Length > 0 && path != "/")
        {
            if (path[0] != '/' || !int.TryParse(path.AsSpan(1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var database) || database < 0)
                throw InvalidConfiguration();
            options.DefaultDatabase = database;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (uri.Query.Length > 1)
        {
            foreach (var item in uri.Query[1..].Split('&'))
            {
                var parts = item.Split('=', 2);
                if (parts.Length != 2) throw InvalidConfiguration();
                var key = Uri.UnescapeDataString(parts[0]);
                var option = Uri.UnescapeDataString(parts[1]);
                if (!seen.Add(key)) throw InvalidConfiguration();
                ApplyUriOption(options, key, option, uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase));
            }
        }
        if (options.Ssl && !seen.Contains("sslHost")) options.SslHost = host;
        return options;
    }

    private static void ApplyUriOption(ConfigurationOptions options, string key, string value, bool requireTls)
    {
        ValidateSerializableValue(value);
        switch (key.ToLowerInvariant())
        {
            case "connecttimeout": options.ConnectTimeout = Number(value, positive: true); break;
            case "synctimeout": options.SyncTimeout = Number(value, positive: true); break;
            case "asynctimeout": options.AsyncTimeout = Number(value, positive: true); break;
            case "connectretry": options.ConnectRetry = Number(value, positive: false); break;
            case "keepalive": options.KeepAlive = Number(value, positive: false); break;
            case "abortconnect": options.AbortOnConnectFail = Boolean(value); break;
            case "ssl":
                var ssl = Boolean(value);
                if (requireTls && !ssl) throw InvalidConfiguration();
                options.Ssl = ssl;
                break;
            case "sslhost":
                if (value.Length == 0) throw InvalidConfiguration();
                options.SslHost = value;
                break;
            default: throw InvalidConfiguration();
        }
    }

    private static int Number(string value, bool positive) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= (positive ? 1 : 0)
            ? number : throw InvalidConfiguration();

    private static bool Boolean(string value) =>
        bool.TryParse(value, out var result) ? result : throw InvalidConfiguration();

    private static bool HasOption(string value, string key) => value.Split(',').Any(part =>
        part.Split('=', 2) is { Length: 2 } option && option[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));

    private static void ValidateRawValues(string value)
    {
        foreach (var part in value.Split(','))
        {
            var option = part.Split('=', 2);
            if (option.Length == 2 && (option[0].Trim().Equals("user", StringComparison.OrdinalIgnoreCase) ||
                                       option[0].Trim().Equals("password", StringComparison.OrdinalIgnoreCase) ||
                                       option[0].Trim().Equals("sslHost", StringComparison.OrdinalIgnoreCase)))
                ValidateSerializableValue(option[1]);
        }
    }

    private static void ValidateOptions(ConfigurationOptions options)
    {
        if (options.EndPoints.Count == 0) throw InvalidConfiguration();
        foreach (var endpoint in options.EndPoints)
        {
            if (endpoint is IPEndPoint ip && ip.Port is >= 1 and <= 65535) continue;
            if (endpoint is DnsEndPoint dns && dns.Port is >= 1 and <= 65535 &&
                Uri.CheckHostName(dns.Host) != UriHostNameType.Unknown) continue;
            throw InvalidConfiguration();
        }
        ValidateSerializableValue(options.User);
        ValidateSerializableValue(options.Password);
        ValidateSerializableValue(options.SslHost);
        var roundTrip = ConfigurationOptions.Parse(options.ToString(includePassword: true));
        if ((roundTrip.User ?? "") != (options.User ?? "") || (roundTrip.Password ?? "") != (options.Password ?? "") ||
            roundTrip.Ssl != options.Ssl || (roundTrip.SslHost ?? "") != (options.SslHost ?? ""))
            throw InvalidConfiguration();
    }

    private static void ValidateSerializableValue(string? value)
    {
        if (value is not null && (value.Contains(',') || value.Any(char.IsControl) || value != value.Trim()))
            throw InvalidConfiguration();
    }

    private static void ValidatePercentEscapes(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                throw InvalidConfiguration();
            index += 2;
        }
    }

    private static ArgumentException InvalidConfiguration() =>
        new("Redis configuration is invalid. Use a Redis URI, TCP endpoint, or supported StackExchange.Redis options.");
}
