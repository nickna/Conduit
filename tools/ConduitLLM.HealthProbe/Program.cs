using System.Diagnostics;
using System.Globalization;

var url = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
    ? args[0]
    : "http://localhost:8080/health/live";
var requests = ReadIntOption(args, "--requests", 1);
var concurrency = ReadIntOption(args, "--concurrency", 1);
var timeoutSeconds = ReadIntOption(args, "--timeout-seconds", 5);

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

if (requests <= 1)
{
    using var response = await client.GetAsync(url);
    return response.IsSuccessStatusCode ? 0 : 1;
}

var samples = new long[requests];
var next = -1;
var failures = 0;
var total = Stopwatch.StartNew();
var workers = Enumerable.Range(0, Math.Min(concurrency, requests)).Select(async _ =>
{
    while (true)
    {
        var index = Interlocked.Increment(ref next);
        if (index >= requests)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref failures);
            }
        }
        catch
        {
            Interlocked.Increment(ref failures);
        }
        finally
        {
            stopwatch.Stop();
            samples[index] = stopwatch.ElapsedTicks;
        }
    }
});

await Task.WhenAll(workers);
total.Stop();
Array.Sort(samples);

var frequency = (double)Stopwatch.Frequency;
double Milliseconds(long ticks) => ticks * 1000d / frequency;
double Percentile(double percentile) => Milliseconds(samples[
    Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)]);

Console.WriteLine(FormattableString.Invariant(
    $$"""{"requests":{{requests}},"concurrency":{{concurrency}},"failures":{{failures}},"throughputPerSecond":{{requests / total.Elapsed.TotalSeconds:F3}},"p50Milliseconds":{{Percentile(0.50):F3}},"p95Milliseconds":{{Percentile(0.95):F3}},"p99Milliseconds":{{Percentile(0.99):F3}}}"""));
return failures == 0 ? 0 : 1;

static int ReadIntOption(string[] arguments, string name, int fallback)
{
    var index = Array.IndexOf(arguments, name);
    if (index < 0 || index + 1 >= arguments.Length ||
        !int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
        value <= 0)
    {
        return fallback;
    }

    return value;
}
