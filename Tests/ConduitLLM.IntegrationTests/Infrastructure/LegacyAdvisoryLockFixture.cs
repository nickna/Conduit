using System.Text;
using Npgsql;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>Test-only snapshot of the former single-bigint SQL and UTF-8 FNV identity.</summary>
internal sealed class LegacyAdvisoryLockFixture(NpgsqlConnection connection, long lockId) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _release;

    public static async Task<LegacyAdvisoryLockFixture?> TryAcquireAsync(string connectionString, string key)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var value in Encoding.UTF8.GetBytes(key)) { hash = unchecked((hash ^ value) * prime); }
        var id = unchecked((long)hash);
        var session = new NpgsqlConnection(connectionString);
        try
        {
            await session.OpenAsync();
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@id)", session);
            acquire.Parameters.AddWithValue("id", id);
            if (await acquire.ExecuteScalarAsync() is true) { return new(session, id); }
            await session.DisposeAsync();
            return null;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new(_release ??= ReleaseAsync()); }
    }

    private async Task ReleaseAsync()
    {
        try
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@id)", connection);
            release.Parameters.AddWithValue("id", lockId);
            await release.ExecuteNonQueryAsync();
        }
        finally { await connection.DisposeAsync(); }
    }
}
