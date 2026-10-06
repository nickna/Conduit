using System.Text;

namespace ConduitLLM.Core.Services;

/// <summary>Rollout-compatible single signed 64-bit PostgreSQL advisory-lock namespace.</summary>
public static class PostgresLockIdentity
{
    public static long GetLockId(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = 14695981039346656037UL;
        foreach (var value in Encoding.UTF8.GetBytes(key))
        {
            hash ^= value;
            hash = unchecked(hash * 1099511628211UL);
        }
        return unchecked((long)hash);
    }
}
