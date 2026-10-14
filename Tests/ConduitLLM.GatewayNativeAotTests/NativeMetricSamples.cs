using System.Globalization;

// Deliberately reads exact sample keys emitted by prometheus-net, including its
// declared label order. The probe supplies only its own ASCII fixture labels.
internal static class NativeMetricSamples
{
    public static bool TryRead(string exposition, string key, out double value)
    {
        value = 0;
        var found = false;
        using var lines = new StringReader(exposition);
        while (lines.ReadLine() is { } line)
        {
            if (!line.StartsWith(key + " ", StringComparison.Ordinal))
                continue;

            if (found || !double.TryParse(line.AsSpan(key.Length + 1),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                !double.IsFinite(value))
                throw new InvalidOperationException($"Invalid or duplicate metric sample: {key}");
            found = true;
        }
        return found;
    }

    public static bool TryReadNonnegativeInteger(string exposition, string key, out long value)
    {
        value = 0;
        if (!TryRead(exposition, key, out var sample))
            return false;

        // Prometheus uses binary64 samples. Above this consecutive-integer
        // ceiling, rounding can hide a lost counter increment. After rejecting
        // negatives, truncation must leave no fractional remainder.
        if (sample < 0 || sample > 9_007_199_254_740_991d || sample > Math.Truncate(sample))
            throw new InvalidOperationException($"Invalid nonnegative integer metric sample: {key}");
        value = checked((long)sample);
        return true;
    }

    public static bool HasBusinessAggregates(string exposition, string model, string provider) =>
        TryRead(exposition, $"conduit_cost_rate_dollars_per_minute{{provider=\"{provider}\"}}", out var cost) &&
        Math.Abs(cost - 0.4) < 1e-9 &&
        TryReadNonnegativeInteger(exposition, $"conduit_models_active_count{{provider=\"-9001\"}}", out var active) &&
        active == 1 &&
        TryRead(exposition, $"conduit_model_response_time_seconds_sum{{model=\"{model}\",provider=\"{provider}\"}}", out var sum) &&
        TryReadNonnegativeInteger(exposition, $"conduit_model_response_time_seconds_count{{model=\"{model}\",provider=\"{provider}\"}}", out var count) &&
        count >= 1 && Math.Abs(sum / count - 0.2) < 1e-9;

    public static bool HasPendingNotificationTasks(string exposition) =>
        TryReadNonnegativeInteger(exposition, "conduit_tasks_queue_depth{task_type=\"native_signalr_probe\",status=\"pending\"}", out var count) &&
        count == 2;
}
