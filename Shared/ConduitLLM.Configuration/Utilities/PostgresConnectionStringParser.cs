using Npgsql;

namespace ConduitLLM.Configuration.Utilities;

/// <summary>Parses the shared PostgreSQL configuration contract without host or EF dependencies.</summary>
public static class PostgresConnectionStringParser
{
    private const string InvalidConfigurationMessage =
        "Invalid PostgreSQL connection configuration. Use a postgres:// or postgresql:// URI, " +
        "or an Npgsql key/value connection string with Host, Database, and Username.";

    /// <summary>
    /// Parses a PostgreSQL URI or Npgsql connection string. Defaults apply only to
    /// options that were not explicitly supplied, including their Npgsql aliases.
    /// </summary>
    public static NpgsqlConnectionStringBuilder Parse(string value, NpgsqlConnectionStringBuilder? defaults = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException();

            var result = value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
                ? ParseUri(value)
                : new NpgsqlConnectionStringBuilder(value);

            if (defaults is not null)
            {
                // ContainsKey reports supported keywords even when absent. Keys
                // contains canonical, explicitly serialized names, so aliases
                // and casing cannot accidentally replace a caller's setting.
                foreach (string key in defaults.Keys)
                {
                    if (!result.ShouldSerialize(key))
                        result[key] = defaults[key];
                }
            }

            if (string.IsNullOrWhiteSpace(result.Host) || string.IsNullOrWhiteSpace(result.Database) ||
                string.IsNullOrWhiteSpace(result.Username) || result.Port <= 0 ||
                result.MinPoolSize > result.MaxPoolSize)
                throw new InvalidOperationException();

            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or OverflowException)
        {
            // Provider and URI exception messages can contain credentials or
            // malformed option values. Do not retain the original as an inner exception.
            throw new InvalidOperationException(InvalidConfigurationMessage);
        }
    }

    /// <summary>Returns canonical configuration with password values masked, or a fixed invalid-input marker.</summary>
    public static string Sanitize(string value)
    {
        try
        {
            var result = Parse(value);
            if (result.ShouldSerialize("Password"))
                result.Password = "*****";
            if (result.ShouldSerialize("SSL Password"))
                result.SslPassword = "*****";
            return result.ConnectionString;
        }
        catch (InvalidOperationException)
        {
            return "[invalid PostgreSQL connection configuration]";
        }
    }

    private static NpgsqlConnectionStringBuilder ParseUri(string value)
    {
        // Uri otherwise repairs malformed escapes (for example %GG becomes
        // %25GG), silently changing the supplied credentials or database name.
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
                continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                throw new InvalidOperationException();
            index += 2;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Fragment.Length != 0)
            throw new InvalidOperationException();

        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
            authorityEnd = value.Length;
        if (value[authorityStart..authorityEnd].EndsWith(':'))
            throw new InvalidOperationException();
        var pathEnd = value.IndexOfAny(['?', '#'], authorityEnd);
        if (pathEnd < 0)
            pathEnd = value.Length;
        // Uri normalizes dot path segments, including percent-escaped dots.
        // PostgreSQL database names are values, so decode the supplied path once.
        var path = value[authorityEnd..pathEnd];

        var userInfo = uri.UserInfo.Split(':', 2);
        var result = new NpgsqlConnectionStringBuilder
        {
            Host = uri.IdnHost,
            Port = uri.Port < 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(path.Length > 0 ? path[1..] : string.Empty),
            Username = Uri.UnescapeDataString(userInfo[0])
        };
        if (userInfo.Length == 2)
            result.Password = Uri.UnescapeDataString(userInfo[1]);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
                throw new InvalidOperationException();
            // Assign each decoded value independently; semicolons in passwords
            // or other values remain data instead of becoming new options.
            result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
        }

        return result;
    }
}
