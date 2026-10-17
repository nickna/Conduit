using Npgsql;

// Short child-process fixture for finalization fault tests. The normal native
// persistence workload uses the same lifecycle and cleanup implementation.
internal static class PersistenceProbeCleanupFixture
{
    internal static Task<int> RunAsync(string connectionString, bool failWork, bool readOnlyCleanup)
    {
        var schema = $"{PersistenceProbeLifecycle.SchemaPrefix}{Guid.NewGuid():N}";
        var cleanupConnection = new NpgsqlConnectionStringBuilder(connectionString);
        if (readOnlyCleanup)
        {
            // A real, immediate PostgreSQL DROP rejection, independent of role
            // privileges and distinct from the blocked-command timeout fixture.
            cleanupConnection.Options = string.IsNullOrWhiteSpace(cleanupConnection.Options)
                ? "-c default_transaction_read_only=on"
                : $"{cleanupConnection.Options} -c default_transaction_read_only=on";
        }
        return PersistenceProbeLifecycle.RunAsync(schema, async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(deadline.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".owned (id integer)";
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            Console.WriteLine($"CLEANUP_FIXTURE_READY:{schema}");
            if (await Console.In.ReadLineAsync(deadline.Token) != "continue")
                throw new InvalidOperationException("Cleanup fixture did not receive its continuation signal.");
            if (failWork) throw new InvalidOperationException("Injected primary probe failure.");
        }, () => PersistenceProbeLifecycle.CleanupOwnedSchemaAsync(cleanupConnection.ConnectionString, schema, TimeSpan.FromSeconds(1)),
            () => Console.WriteLine("Cleanup fixture passed."), Console.Error);
    }
}
