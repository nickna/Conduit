#nullable enable

using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Data;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.Tests.Core.Data;

[Collection("AdminCompositionEnvironment")]
public sealed class PostgresConnectionStringParserTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    public void UriCredentialsDatabaseAndQueryValues_AreDecodedExactlyOnceIntoTypedFields(string scheme)
    {
        var value = scheme + "://user%3Aname:p%40ss%3Aword%3Bvalue@db.invalid:5544/db%3Bname%3Aone" +
            "?Application%20Name=fixture%3BPooling%3Dfalse%26value&Search%20Path=one%2Ctwo&sslmode=Require";

        var settings = PostgresConnectionStringParser.Parse(value);

        Assert.Equal("db.invalid", settings.Host);
        Assert.Equal(5544, settings.Port);
        Assert.Equal("user:name", settings.Username);
        Assert.Equal("p@ss:word;value", settings.Password);
        Assert.Equal("db;name:one", settings.Database);
        Assert.Equal("fixture;Pooling=false&value", settings.ApplicationName);
        Assert.Equal("one,two", settings.SearchPath);
        Assert.Equal(SslMode.Require, settings.SslMode);
        Assert.True(settings.Pooling);
        var reparsed = new NpgsqlConnectionStringBuilder(settings.ConnectionString);
        Assert.Equal(settings.Password, reparsed.Password);
        Assert.Equal(settings.ApplicationName, reparsed.ApplicationName);
    }

    [Fact]
    public void UriEscapes_AreNotDecodedASecondTimeAndPlusRemainsLiteral()
    {
        var settings = PostgresConnectionStringParser.Parse(
            "postgresql://user%253Aname:pass%2540word%253Bvalue@db.invalid/db%252Fname?ApplicationName=a+b%2520c");

        Assert.Equal("user%3Aname", settings.Username);
        Assert.Equal("pass%40word%3Bvalue", settings.Password);
        Assert.Equal("db%2Fname", settings.Database);
        Assert.Equal("a+b%20c", settings.ApplicationName);
    }

    [Theory]
    [InlineData("%2E", ".")]
    [InlineData(".", ".")]
    [InlineData("name%2Fchild", "name/child")]
    [InlineData("name/../child", "name/../child")]
    [InlineData("db%3Fname%23tag", "db?name#tag")]
    public void DatabasePath_IsDecodedWithoutUriDotSegmentNormalization(string path, string database)
    {
        var settings = PostgresConnectionStringParser.Parse("postgres://user:password@db.invalid/" + path);
        Assert.Equal(database, settings.Database);
    }

    [Fact]
    public void Ipv6Uri_RetainsItsAddressAndPort()
    {
        var settings = PostgresConnectionStringParser.Parse("postgres://user:password@[::1]:5545/database");

        Assert.Equal("::1", Assert.IsType<string>(settings.Host).Trim('[', ']'));
        Assert.Equal(5545, settings.Port);
        Assert.Equal("database", settings.Database);
    }

    [Theory]
    [InlineData("postgresql://user@db.invalid/database")]
    [InlineData("postgresql://user:@db.invalid/database")]
    [InlineData("Host=db.invalid;Database=database;Username=user")]
    [InlineData("Host=db.invalid;Database=database;Username=user;Password=")]
    public void AlternativeAuthentication_DoesNotRequireAPassword(string value)
    {
        var settings = PostgresConnectionStringParser.Parse(value);
        Assert.Equal("user", settings.Username);
        Assert.True(string.IsNullOrEmpty(settings.Password));
        new ConnectionStringManager().ValidateConnectionString("postgres", settings.ConnectionString);
    }

    [Theory]
    [InlineData("Host=db.invalid;Database=database;User ID=user;Password=\"dummy;value\"", "dummy;value")]
    [InlineData("Host=db.invalid;Database=database;User ID=user;Password='dummy;value'", "dummy;value")]
    [InlineData("Host=db.invalid;Database=database;User ID=user;Password=\"dummy\"\"quote;value\"", "dummy\"quote;value")]
    public void KeyValueInputs_UseNpgsqlQuotingAndAliasRules(string value, string password)
    {
        var settings = PostgresConnectionStringParser.Parse(value);

        Assert.Equal("db.invalid", settings.Host);
        Assert.Equal("user", settings.Username);
        Assert.Equal(password, settings.Password);
        Assert.Equal(password, new NpgsqlConnectionStringBuilder(settings.ConnectionString).Password);
    }

    [Fact]
    public void KeyValueInputs_DoNotPercentDecodeLiteralValues()
    {
        var settings = PostgresConnectionStringParser.Parse(
            "Host=db.invalid;Database=db%2Fname;Username=user%3Aname;Password=pass%2Cword");

        Assert.Equal("db%2Fname", settings.Database);
        Assert.Equal("user%3Aname", settings.Username);
        Assert.Equal("pass%2Cword", settings.Password);
    }

    [Fact]
    public void TypedQueryAliasesAndDuplicateAliases_UseTheLastExplicitValue()
    {
        var settings = PostgresConnectionStringParser.Parse(
            "postgres://user:password@db.invalid/database?Minimum%20Pool%20Size=2&minpoolsize=3" +
            "&MAXPOOLSIZE=17&TIMEOUT=9&Command%20Timeout=11&Pooling=false");

        Assert.Equal(3, settings.MinPoolSize);
        Assert.Equal(17, settings.MaxPoolSize);
        Assert.Equal(9, settings.Timeout);
        Assert.Equal(11, settings.CommandTimeout);
        Assert.False(settings.Pooling);
    }

    [Theory]
    [InlineData("Host=other.invalid;Port=5546;Database=other;Username=other;Password=override")]
    [InlineData("host=other.invalid;port=5546;database=other;user%20id=other;password=override")]
    public void QueryEndpointAndAuthenticationOverrides_AreTypedOptions(string queryAsKeyValues)
    {
        var query = queryAsKeyValues.Replace(';', '&');
        var settings = PostgresConnectionStringParser.Parse("postgres://user:password@db.invalid/database?" + query);

        Assert.Equal("other.invalid", settings.Host);
        Assert.Equal(5546, settings.Port);
        Assert.Equal("other", settings.Database);
        Assert.Equal("other", settings.Username);
        Assert.Equal("override", settings.Password);
    }

    [Fact]
    public void SuppliedDefaults_OnlyFillFieldsWithoutExplicitCanonicalAliases()
    {
        var defaults = new NpgsqlConnectionStringBuilder
        {
            Pooling = true, MinPoolSize = 5, MaxPoolSize = 75, ConnectionLifetime = 300, ConnectionIdleLifetime = 60
        };

        var settings = PostgresConnectionStringParser.Parse(
            "postgres://user:password@db.invalid/database?Minimum%20Pool%20Size=0&MaxPoolSize=19&Pooling=false", defaults);

        Assert.Equal(0, settings.MinPoolSize);
        Assert.Equal(19, settings.MaxPoolSize);
        Assert.False(settings.Pooling);
        Assert.Equal(300, settings.ConnectionLifetime);
        Assert.Equal(60, settings.ConnectionIdleLifetime);
        Assert.Equal(5, defaults.MinPoolSize);
        Assert.Equal(75, defaults.MaxPoolSize);
    }

    [Theory]
    [InlineData(null, 1, 100)]
    [InlineData("AdminAPI", 5, 75)]
    [InlineData("adminapi", 5, 75)]
    [InlineData("CoreAPI", 10, 150)]
    [InlineData("WebAdmin", 2, 20)]
    public void CoreEnvironmentResolution_AppliesTheSelectedServiceDefaults(string? profile, int minimum, int maximum)
    {
        using var environment = new DatabaseEnvironment("postgresql://user:password@db.invalid/database");

        var (provider, connection) = new ConnectionStringManager().GetProviderAndConnectionString(profile);
        var settings = new NpgsqlConnectionStringBuilder(connection);

        Assert.Equal("postgres", provider);
        Assert.Equal(minimum, settings.MinPoolSize);
        Assert.Equal(maximum, settings.MaxPoolSize);
        Assert.True(settings.Pooling);
        Assert.Equal(300, settings.ConnectionLifetime);
        Assert.Equal(60, settings.ConnectionIdleLifetime);
    }

    [Theory]
    [InlineData("?MinPoolSize=0", 0, 75, true)]
    [InlineData("?Maximum%20Pool%20Size=19", 5, 19, true)]
    [InlineData("?POOLING=false", 5, 75, false)]
    [InlineData("?Pooling=true&MinPoolSize=2&MaxPoolSize=11", 2, 11, true)]
    public void CoreProfileDefaults_PreservePartialUriOverrides(string query, int minimum, int maximum, bool pooling)
    {
        using var environment = new DatabaseEnvironment("postgres://user:password@db.invalid/database" + query);

        var (_, connection) = new ConnectionStringManager().GetProviderAndConnectionString("AdminAPI");
        var settings = new NpgsqlConnectionStringBuilder(connection);

        Assert.Equal(minimum, settings.MinPoolSize);
        Assert.Equal(maximum, settings.MaxPoolSize);
        Assert.Equal(pooling, settings.Pooling);
    }

    [Fact]
    public void CoreEnvironmentResolution_AcceptsKeyValueInputAndPreservesExplicitPoolOptions()
    {
        using var environment = new DatabaseEnvironment(
            "Host=db.invalid;Database=database;Username=user;Password=\"dummy;value\";Minimum Pool Size=0;Maximum Pool Size=9;Pooling=false");

        var (_, connection) = new ConnectionStringManager().GetProviderAndConnectionString("CoreAPI");
        var settings = new NpgsqlConnectionStringBuilder(connection);

        Assert.Equal("dummy;value", settings.Password);
        Assert.Equal(0, settings.MinPoolSize);
        Assert.Equal(9, settings.MaxPoolSize);
        Assert.False(settings.Pooling);
    }

    [Theory]
    [InlineData("postgres://user:dummy-password@db.invalid/database#fragment")]
    [InlineData("postgres://user:dummy-password@/database")]
    [InlineData("postgres://:dummy-password@db.invalid/database")]
    [InlineData("postgres://user:dummy-password@db.invalid/")]
    [InlineData("postgres://user:dummy-password@db.invalid:70000/database")]
    [InlineData("postgres://user:dummy-password@db.invalid:/database")]
    [InlineData("postgres://user:dummy-password@db.invalid/database?UnknownOption=dummy-password")]
    [InlineData("postgres://user:dummy-password@db.invalid/database?Timeout=not-a-number")]
    [InlineData("postgres://user:dummy-password@db.invalid/database?Pooling=not-a-boolean")]
    [InlineData("postgres://user:dummy-password@db.invalid/database??Pooling=false")]
    [InlineData("postgres://user:dummy-password@db.invalid/database?%3FPooling=false")]
    [InlineData("postgres://user:dummy-password@db.invalid/database?application_name")]
    [InlineData("postgres://user:dummy-password@db.invalid/database?Password=%GG")]
    [InlineData("postgres://user:dummy-password%2@db.invalid/database")]
    [InlineData("http://user:dummy-password@db.invalid/database")]
    [InlineData("Host=db.invalid;Database=database;Password=dummy-password")]
    [InlineData("Database=database;Username=user;Password=dummy-password")]
    [InlineData("Host=db.invalid;Username=user;Password=dummy-password")]
    [InlineData("Host=db.invalid;Database=database;Username=user;Password=dummy-password;UnknownOption=value")]
    public void InvalidInputs_KeepRawAndDecodedCredentialsOutOfExceptionsAndCoreLogs(string input)
    {
        var direct = Assert.Throws<InvalidOperationException>(() => PostgresConnectionStringParser.Parse(input));
        Assert.Null(direct.InnerException);
        Assert.DoesNotContain("dummy-password", direct.ToString());
        Assert.DoesNotContain(input, direct.ToString());

        using var environment = new DatabaseEnvironment(input);
        var captured = new List<string>();
        var error = Assert.Throws<InvalidOperationException>(() => new ConnectionStringManager()
            .GetProviderAndConnectionString("AdminAPI", captured.Add));
        Assert.DoesNotContain("dummy-password", error.ToString());
        Assert.All(captured, message => Assert.DoesNotContain("dummy-password", message));
        Assert.DoesNotContain("dummy-password", PostgresConnectionStringParser.Sanitize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyConfiguration_IsRejectedWithoutRetainingTheInput(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgresConnectionStringParser.Parse(value!));
        Assert.Null(error.InnerException);
        using var environment = new DatabaseEnvironment(value);
        Assert.Throws<InvalidOperationException>(() => new ConnectionStringManager().GetProviderAndConnectionString());
    }

    [Theory]
    [InlineData("Host=db.invalid;Database=database;Username=user;Password=\"dummy;secret\"")]
    [InlineData("postgresql://user:dummy%3Bsecret@db.invalid/database")]
    [InlineData("Host=db.invalid;Database=database;Username=user;Password=\"dummy;secret\";SSL Password=\"dummy;tls-secret\"")]
    public void SanitizedSettingsAndCoreDiagnostics_RedactWholeQuotedPasswordValues(string input)
    {
        var sanitized = PostgresConnectionStringParser.Sanitize(input);
        Assert.DoesNotContain("dummy", sanitized);
        Assert.DoesNotContain("secret", sanitized);
        var settings = new NpgsqlConnectionStringBuilder(sanitized);
        Assert.Equal("db.invalid", settings.Host);
        Assert.Equal("database", settings.Database);
        Assert.Equal("*****", settings.Password);

        using var environment = new DatabaseEnvironment(input);
        var logger = new RecordingLogger<ConnectionStringManager>();
        var captured = new List<string>();
        var manager = new ConnectionStringManager(logger);
        var (_, connection) = manager.GetProviderAndConnectionString("AdminAPI", captured.Add);
        Assert.Equal("dummy;secret", new NpgsqlConnectionStringBuilder(connection).Password);
        Assert.DoesNotContain("dummy", manager.SanitizeConnectionString(connection));
        Assert.All(captured.Concat(logger.Messages), message => Assert.DoesNotContain("dummy", message));
    }

    private sealed class DatabaseEnvironment(string? value) : IDisposable
    {
        private readonly string? _original = Environment.GetEnvironmentVariable("DATABASE_URL");
        private readonly bool _applied = Apply(value);
        private static bool Apply(string? value) { Environment.SetEnvironmentVariable("DATABASE_URL", value); return true; }
        public void Dispose() { if (_applied) Environment.SetEnvironmentVariable("DATABASE_URL", _original); }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + " " + exception);
    }
}
