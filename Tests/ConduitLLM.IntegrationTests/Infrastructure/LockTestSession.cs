using ConduitLLM.Core.Services;
using Npgsql;

namespace ConduitLLM.IntegrationTests.Infrastructure;

internal static class LockTestSession
{
    public static async Task TerminateAsync(string connectionString, string key)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'conduit-distributed-lock' AND pid IN (SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND classid::bigint = @high AND objid::bigint = @low AND objsubid = 1)", observer);
        var bits = unchecked((ulong)PostgresLockIdentity.GetLockId(key));
        command.Parameters.AddWithValue("high", (long)(bits >> 32));
        command.Parameters.AddWithValue("low", (long)(bits & uint.MaxValue));
        if (await command.ExecuteScalarAsync() is not true)
        {
            throw new InvalidOperationException("Isolated test holder session was not terminated");
        }
    }
}
