using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace ConduitLLM.SchemaStartupFixtures;

/// <summary>Creates disposable databases for native startup tests; never shipped with a service.</summary>
public sealed record SchemaStartupDatabase(string Name, string ConnectionString, string? AppliedVersion)
{
    private const string Prefix = "conduit_native_startup_";

    public static async Task<SchemaStartupDatabase> CreateAsync(
        string managementConnectionString, bool previousSchema, CancellationToken cancellationToken = default)
    {
        var management = ManagementOptions(managementConnectionString);
        var name = Prefix + Guid.NewGuid().ToString("N");
        var owned = new NpgsqlConnectionStringBuilder(management.ConnectionString) { Database = name };
        var created = false;
        try
        {
            await using (var connection = new NpgsqlConnection(management.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", connection))
                {
                    await create.ExecuteNonQueryAsync(cancellationToken);
                    created = true;
                }
                await using var mark = new NpgsqlCommand($"COMMENT ON DATABASE \"{name}\" IS '{OwnershipMarker(name)}'", connection);
                await mark.ExecuteNonQueryAsync(cancellationToken);
            }

            string? expected = null;
            if (previousSchema)
            {
                var options = new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(owned.ConnectionString).Options;
                await using var context = new ConduitDbContext(options);
                var migrations = context.Database.GetMigrations().ToArray();
                if (migrations.Length < 2 || migrations[^1] != ConduitSchemaVersion.Current)
                    throw new InvalidOperationException("The release schema version must match the latest migration, with a prior migration available.");
                expected = migrations[^2];
                await context.GetService<IMigrator>().MigrateAsync(expected, cancellationToken);
            }

            await using var dataSource = NpgsqlDataSource.Create(owned.ConnectionString);
            var status = await new SchemaVersionProbe(dataSource).GetStatusAsync(cancellationToken);
            if (status.AppliedVersion != expected || status.IsCurrent)
                throw new InvalidOperationException("Startup fixture must have an uninitialized or genuinely previous schema.");
            return new SchemaStartupDatabase(name, owned.ConnectionString, status.AppliedVersion);
        }
        catch (Exception original)
        {
            if (created)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await DropCreatedAsync(management.ConnectionString, name, cleanup.Token); }
                catch (Exception failure) { throw new AggregateException("Startup fixture creation and owned database cleanup failed.", original, failure); }
            }
            // Include the private generated name if CREATE had an unknown outcome;
            // never conceal a possible orphan behind a successful fixture result.
            throw new InvalidOperationException($"Could not initialize startup fixture database '{name}'.", original);
        }
    }

    public static async Task DropAsync(string managementConnectionString, string name, CancellationToken cancellationToken = default)
    {
        ValidateOwnedName(name);
        var management = ManagementOptions(managementConnectionString);
        if (management.Database == name) throw new InvalidOperationException("Cannot remove the management database.");
        await using var connection = new NpgsqlConnection(management.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var ownership = new NpgsqlCommand("""
            SELECT COALESCE(shobj_description(oid, 'pg_database'), '') FROM pg_database WHERE datname = @name
            """, connection);
        ownership.Parameters.AddWithValue("name", name);
        var marker = await ownership.ExecuteScalarAsync(cancellationToken);
        if (marker is null) return; // Already removed.
        if (!string.Equals(marker as string, OwnershipMarker(name), StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a database without this fixture's ownership marker.");
        await DropCreatedAsync(management.ConnectionString, name, cancellationToken);
    }

    private static NpgsqlConnectionStringBuilder ManagementOptions(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(options.Options) ||
            (!string.IsNullOrEmpty(options.SearchPath) && options.SearchPath != "public"))
            throw new InvalidOperationException("Startup fixtures require the default public schema without PostgreSQL session Options.");
        options.Pooling = false;
        options.CommandTimeout = 15;
        return options;
    }

    private static string OwnershipMarker(string name) => "conduit-native-startup:" + name;

    private static void ValidateOwnedName(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length != Prefix.Length + 32 ||
            name.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("A fixture-generated database name is required.", nameof(name));
    }

    private static async Task DropCreatedAsync(string connectionString, string name, CancellationToken token)
    {
        ValidateOwnedName(name);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync(token);
    }
}
