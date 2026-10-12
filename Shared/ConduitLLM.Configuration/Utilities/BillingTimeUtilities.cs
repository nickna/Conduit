namespace ConduitLLM.Configuration.Utilities;

/// <summary>
/// Normalizes usage timestamps for hourly spend attribution.
/// </summary>
public static class BillingTimeUtilities
{
    /// <summary>
    /// Captures the current UTC time when omitted, or converts the supplied timestamp to UTC.
    /// Local and unspecified timestamps follow <see cref="DateTime.ToUniversalTime"/> semantics.
    /// </summary>
    public static DateTime NormalizeUtc(DateTime? billedAtUtc)
    {
        var value = billedAtUtc ?? DateTime.UtcNow;
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    /// <summary>
    /// Gets the start of the UTC hour containing the usage timestamp.
    /// </summary>
    public static DateTime GetBillingWindowStartUtc(DateTime? billedAtUtc)
    {
        var value = NormalizeUtc(billedAtUtc);
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
    }
}
