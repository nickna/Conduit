using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Migrator;
using ConduitLLM.SchemaStartupFixtures;

using Npgsql;

namespace ConduitLLM.Tests.Configuration.Data;

[Collection("MigrationEnvironment")]
public sealed class WolverineStorageStartupProbeTests
{
    [SkippableFact]
    public async Task ReleaseProvisionedStorage_MissingTablesAndColumnsGateAndRecoverOnOnePoolSlot()
    {
        var management = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(management), "Requires PostgreSQL database creation rights.");
        var fixture = await SchemaStartupDatabase.CreateAsync(management!, previousSchema: false);
        var variables = new[] { "DATABASE_URL", "ConduitLLM__Messaging__Backend", "ConduitLLM__Messaging__Wolverine__Transport", "ConduitLLM__Messaging__Wolverine__SchemaName" };
        var originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            var options = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = true, MaxPoolSize = 1 };
            await using var source = NpgsqlDataSource.Create(options.ConnectionString);
            var queues = WolverineQueueNames.Gateway.Concat(WolverineQueueNames.Admin);
            var gateway = new WolverineStorageStartupProbe(source, "wolverine_conduit_gateway", WolverineMessagingExtensions.TransportSchemaName, queues);
            var admin = new WolverineStorageStartupProbe(source, "wolverine_conduit_admin", WolverineMessagingExtensions.TransportSchemaName, queues);
            Assert.False(await gateway.IsReadyAsync());
            Environment.SetEnvironmentVariable(variables[0], fixture.ConnectionString);
            Environment.SetEnvironmentVariable(variables[1], "Wolverine");
            Environment.SetEnvironmentVariable(variables[2], "Postgresql");
            Environment.SetEnvironmentVariable(variables[3], null);
            Assert.Equal(0, await MigrationRunner.RunAsync());
            Assert.True(await gateway.IsReadyAsync());
            Assert.True(await admin.IsReadyAsync());

            await using var control = new NpgsqlConnection(fixture.ConnectionString);
            await control.OpenAsync();
            foreach (var table in new[]
            {
                "wolverine_conduit_gateway.wolverine_nodes",
                "wolverine_conduit_gateway.wolverine_node_assignments",
                "wolverine_conduit_gateway.wolverine_agent_restrictions",
                "wolverine_conduit_gateway.wolverine_control_queue",
                "wolverine_conduit_gateway.wolverine_node_records",
                "wolverine_conduit_gateway.wolverine_incoming_envelopes",
                "wolverine_conduit_gateway.wolverine_outgoing_envelopes",
                "wolverine_conduit_gateway.wolverine_dead_letters",
                "wolverine_queues.wolverine_queue_admin_events",
                "wolverine_queues.wolverine_queue_webhook_delivery_scheduled"
            })
            {
                var pieces = table.Split('.');
                await Sql($"ALTER TABLE {table} RENAME TO startup_hidden");
                try { Assert.False(await gateway.IsReadyAsync()); }
                finally { await Sql($"ALTER TABLE {pieces[0]}.startup_hidden RENAME TO {pieces[1]}"); }
                Assert.True(await gateway.IsReadyAsync());
            }
            foreach (var (table, column) in new[]
            {
                ("wolverine_conduit_gateway.wolverine_nodes", "node_number"),
                ("wolverine_conduit_gateway.wolverine_outgoing_envelopes", "destination"),
                ("wolverine_queues.wolverine_queue_image_generation_events_scheduled", "execution_time")
            })
            {
                await Sql($"ALTER TABLE {table} RENAME COLUMN {column} TO startup_hidden");
                try { Assert.False(await gateway.IsReadyAsync()); }
                finally { await Sql($"ALTER TABLE {table} RENAME COLUMN startup_hidden TO {column}"); }
                Assert.True(await gateway.IsReadyAsync());
            }
            // A view with identical column names cannot stand in for the writable
            // durability table, even though pg_attribute describes both.
            await Sql("ALTER TABLE wolverine_conduit_gateway.wolverine_outgoing_envelopes RENAME TO startup_hidden");
            try
            {
                await Sql("CREATE VIEW wolverine_conduit_gateway.wolverine_outgoing_envelopes AS SELECT * FROM wolverine_conduit_gateway.startup_hidden");
                Assert.False(await gateway.IsReadyAsync());
            }
            finally
            {
                await Sql("DROP VIEW IF EXISTS wolverine_conduit_gateway.wolverine_outgoing_envelopes");
                await Sql("ALTER TABLE wolverine_conduit_gateway.startup_hidden RENAME TO wolverine_outgoing_envelopes");
            }
            Assert.True(await gateway.IsReadyAsync());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.IsReadyAsync(cancelled.Token));
            Assert.True(await gateway.IsReadyAsync());

            async Task Sql(string sql)
            {
                await using var command = new NpgsqlCommand(sql, control);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            for (var index = 0; index < variables.Length; index++) Environment.SetEnvironmentVariable(variables[index], originals[index]);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SchemaStartupDatabase.DropAsync(management!, fixture.Name, cleanup.Token);
        }
    }

    [SkippableFact]
    public async Task ConfiguredSchema_IsQuotedAsOneIdentifierWithoutSqlInjection()
    {
        var management = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(management), "Requires PostgreSQL database creation rights.");
        var fixture = await SchemaStartupDatabase.CreateAsync(management!, previousSchema: false);
        try
        {
            await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
            var probe = new WolverineStorageStartupProbe(source, "Unexpected\"; DROP SCHEMA public; --", "wolverine_queues", ["gateway-events"]);
            Assert.False(await probe.IsReadyAsync());
            await using var query = source.CreateCommand("SELECT to_regnamespace('public') IS NOT NULL");
            Assert.Equal(true, await query.ExecuteScalarAsync());
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SchemaStartupDatabase.DropAsync(management!, fixture.Name, cleanup.Token);
        }
    }
}
