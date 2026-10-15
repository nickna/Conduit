using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Data;
using ConduitLLM.IntegrationTests.Infrastructure;

using Npgsql;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Admin database health pool")]
[Trait("Category", "Integration")]
[Trait("Component", "ConnectionConfiguration")]
public sealed class PostgresConnectionConfigurationTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task HostsMigratorAndSharedParserConnectWithTheSameEscapedCredentialsDatabaseAndOptions()
    {
        var identity = Guid.NewGuid().ToString("N");
        // These names belong only to this test. Punctuation also exercises SQL
        // identifier/literal quoting and URI percent decoding independently.
        var role = "conduit_cfg_" + identity + ":;@'\"";
        var database = "conduit_cfg_" + identity + " space%2F:;@'\"";
        var password = "p:;@/%2F'\"" + identity;
        var application = "conduit cfg; percent% " + identity;
        var observerSettings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false };
        await using var observer = new NpgsqlConnection(observerSettings.ConnectionString);
        await observer.OpenAsync();
        var roleCreated = false;
        var databaseCreated = false;
        try
        {
            await ExecuteAsync(observer, $"CREATE ROLE {Identifier(role)} LOGIN PASSWORD {Literal(password)}");
            roleCreated = true;
            await ExecuteAsync(observer, $"CREATE DATABASE {Identifier(database)} OWNER {Identifier(role)}");
            databaseCreated = true;

            var expected = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = role,
                Password = password,
                Database = database,
                ApplicationName = application,
                Pooling = false,
                Timeout = 5
            };
            var configuredHost = Assert.IsType<string>(expected.Host);
            var host = configuredHost.Contains(':') ? $"[{configuredHost.Trim('[', ']')}]" : configuredHost;
            var authority = $"{Uri.EscapeDataString(role)}:{Uri.EscapeDataString(password)}@{host}:{expected.Port}";
            var suffix = $"/{Uri.EscapeDataString(database)}?Application%20Name={Uri.EscapeDataString(application)}&Pooling=false&Timeout=5";
            var inputs = new[]
            {
                "postgres://" + authority + suffix,
                "postgresql://" + authority + suffix,
                expected.ConnectionString
            };

            foreach (var input in inputs)
            {
                using var environment = new EnvironmentScope(new Dictionary<string, string?> { ["DATABASE_URL"] = input });
                var (provider, runtimeConnection) = new ConnectionStringManager().GetProviderAndConnectionString("AdminAPI");
                Assert.Equal("postgres", provider);
                // The release migrator uses this exact resolver before opening
                // its release lock and applying migrations.
                var migrationConnection = ConfigurationDbContextFactory.ResolveNpgsqlConnectionString();
                var sharedConnection = PostgresConnectionStringParser.Parse(input).ConnectionString;

                foreach (var connectionString in new[] { runtimeConnection, migrationConnection, sharedConnection })
                {
                    AssertExpectedSettings(expected, new NpgsqlConnectionStringBuilder(connectionString));
                    await using var connection = new NpgsqlConnection(connectionString);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await connection.OpenAsync(deadline.Token);
                    await using var command = new NpgsqlCommand(
                        "SELECT current_user::text, current_database()::text, current_setting('application_name')", connection);
                    await using var reader = await command.ExecuteReaderAsync(deadline.Token);
                    Assert.True(await reader.ReadAsync(deadline.Token));
                    Assert.Equal(role, reader.GetString(0));
                    Assert.Equal(database, reader.GetString(1));
                    Assert.Equal(application, reader.GetString(2));
                    Assert.False(await reader.ReadAsync(deadline.Token));
                }
            }
        }
        finally
        {
            try
            {
                if (databaseCreated)
                    await ExecuteAsync(observer, $"DROP DATABASE {Identifier(database)} WITH (FORCE)");
            }
            finally
            {
                if (roleCreated)
                    await ExecuteAsync(observer, $"DROP ROLE {Identifier(role)}");
            }
        }
    }

    private static void AssertExpectedSettings(NpgsqlConnectionStringBuilder expected, NpgsqlConnectionStringBuilder actual)
    {
        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.Database, actual.Database);
        Assert.Equal(expected.Username, actual.Username);
        Assert.Equal(expected.Password, actual.Password);
        Assert.Equal(expected.ApplicationName, actual.ApplicationName);
        Assert.Equal(expected.Timeout, actual.Timeout);
        Assert.False(actual.Pooling);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 10 };
        await command.ExecuteNonQueryAsync();
    }

    private static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
