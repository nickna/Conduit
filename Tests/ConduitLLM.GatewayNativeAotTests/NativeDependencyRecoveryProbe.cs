using System.Net;
using System.Net.Http.Headers;

internal sealed class NativeDependencyRecoveryProbe(
    HttpClient admin, HttpClient primary, HttpClient secondary, HttpClient control, string controlToken,
    TimeSpan? phaseTimeout = null, TimeSpan? pollInterval = null)
{
    public async Task RunAsync()
    {
        Exception? assertionFailure = null;
        try
        {
            await WaitForHealthAsync(HttpStatusCode.OK, HttpStatusCode.OK, "healthy baseline");
            await SetBlockedAsync("redis", true, CancellationToken.None);
            await WaitForHealthAsync(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, "Redis outage");
            await SetBlockedAsync("redis", false, CancellationToken.None);
            await WaitForHealthAsync(HttpStatusCode.OK, HttpStatusCode.OK, "Redis recovery");
            await SetBlockedAsync("postgres", true, CancellationToken.None);
            await WaitForHealthAsync(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, "PostgreSQL outage");
            await SetBlockedAsync("postgres", false, CancellationToken.None);
            await WaitForHealthAsync(HttpStatusCode.OK, HttpStatusCode.OK, "PostgreSQL recovery");
        }
        catch (Exception exception)
        {
            assertionFailure = exception;
            throw;
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // Dispatch both independently: a stalled first restore must not
            // consume the deadline before the other command is even sent.
            var failures = await Task.WhenAll(
                TryRestoreAsync("redis", cleanupDeadline.Token),
                TryRestoreAsync("postgres", cleanupDeadline.Token));
            var cleanupFailures = failures.OfType<Exception>().ToList();
            if (cleanupFailures.Count > 0)
            {
                if (assertionFailure is not null) cleanupFailures.Insert(0, assertionFailure);
                throw new AggregateException("Could not restore owned dependency proxies.", cleanupFailures);
            }
        }
        Console.WriteLine("Native PostgreSQL and Redis readiness outage/recovery: PASS");
    }

    private async Task<Exception?> TryRestoreAsync(string dependency, CancellationToken token)
    {
        try
        {
            await SetBlockedAsync(dependency, false, token);
            return null;
        }
        catch (Exception exception) { return exception; }
    }

    private async Task SetBlockedAsync(string dependency, bool blocked, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{dependency}/{(blocked ? "block" : "restore")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", controlToken);
        using var response = await control.SendAsync(request, token);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new InvalidOperationException($"Dependency control {dependency}/{(blocked ? "block" : "restore")} returned {(int)response.StatusCode}.");
    }

    private async Task WaitForHealthAsync(HttpStatusCode adminReady, HttpStatusCode gatewayReady, string stage)
    {
        using var deadline = new CancellationTokenSource(phaseTimeout ?? TimeSpan.FromSeconds(90));
        var latest = "No health responses received.";
        try
        {
            while (true)
            {
                var complete = true;
                var observations = new List<string>();
                foreach (var (client, name, expected) in new[]
                         { (admin, "Admin", adminReady), (primary, "primary Gateway", gatewayReady), (secondary, "secondary Gateway", gatewayReady) })
                {
                    using var ready = await client.GetAsync("health/ready", deadline.Token);
                    using var live = await client.GetAsync("health/live", deadline.Token);
                    observations.Add($"{name}: ready={(int)ready.StatusCode}, live={(int)live.StatusCode}");
                    latest = string.Join("; ", observations);
                    if (live.StatusCode != HttpStatusCode.OK || ready.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
                        throw new InvalidOperationException($"Unexpected health response during {stage}: {latest}");
                    complete &= ready.StatusCode == expected;
                }
                if (complete) return;
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(1), deadline.Token);
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {stage}: {latest}", exception);
        }
    }
}
