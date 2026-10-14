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

    public static bool HasBusinessAggregates(string exposition, string model, string provider) =>
        TryRead(exposition, $"conduit_cost_rate_dollars_per_minute{{provider=\"{provider}\"}}", out var cost) &&
        Math.Abs(cost - 0.4) < 1e-9 &&
        TryRead(exposition, $"conduit_models_active_count{{provider=\"-9001\"}}", out var active) &&
        active == 1 &&
        TryRead(exposition, $"conduit_model_response_time_seconds_sum{{model=\"{model}\",provider=\"{provider}\"}}", out var sum) &&
        TryRead(exposition, $"conduit_model_response_time_seconds_count{{model=\"{model}\",provider=\"{provider}\"}}", out var count) &&
        count >= 1 && Math.Abs(sum / count - 0.2) < 1e-9;

    public static bool HasPendingNotificationTasks(string exposition) =>
        TryRead(exposition, "conduit_tasks_queue_depth{task_type=\"native_signalr_probe\",status=\"pending\"}", out var count) &&
        count == 2;
}
