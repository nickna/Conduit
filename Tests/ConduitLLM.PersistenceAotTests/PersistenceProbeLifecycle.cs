using Npgsql;

internal static class PersistenceProbeLifecycle
{
    internal const string SchemaPrefix = "conduit_aot_probe_";

    internal static async Task<int> RunAsync(
        string schema, Func<Task> probe, Func<Task> cleanup, Action passed, TextWriter diagnostics)
    {
        Exception? probeFailure = null;
        Exception? cleanupFailure = null;
        try { await probe(); }
        catch (Exception exception) { probeFailure = exception; }
        // The probe delegate has finished disposing its resources before cleanup starts.
        try { await cleanup(); }
        catch (Exception exception) { cleanupFailure = exception; }
        if (probeFailure is not null)
            await diagnostics.WriteLineAsync($"Persistence probe failed: {probeFailure}");
        if (cleanupFailure is not null)
            await diagnostics.WriteLineAsync($"Cleanup failed for owned schema '{schema}': {cleanupFailure}");
        if (probeFailure is not null || cleanupFailure is not null) return 1;
        passed();
        return 0;
    }

    internal static async Task CleanupOwnedSchemaAsync(string connectionString, string schema, TimeSpan? timeout = null)
    {
        if (!schema.StartsWith(SchemaPrefix, StringComparison.Ordinal) ||
            schema.Length != SchemaPrefix.Length + 32 ||
            !Guid.TryParseExact(schema.AsSpan(SchemaPrefix.Length), "N", out _))
            throw new ArgumentException("Cleanup requires this probe's exact GUID schema name.", nameof(schema));
        var budget = timeout ?? TimeSpan.FromSeconds(15);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(budget);
        // Independent bounds override even caller-supplied unlimited driver timeouts.
        // Cancellation acknowledgement may take at most one additional second.
        var bounded = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Timeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds)),
            CommandTimeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds)),
            CancellationTimeout = 1000
        };
        await using var connection = new NpgsqlConnection(bounded.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync(deadline.Token);
    }
}
