using System.Diagnostics;
using Prometheus;

namespace ConduitLLM.Gateway.Handlers;

/// <summary>One source for actual HTTP invocations, independent of delivery deferrals and SignalR.</summary>
public sealed class WebhookMetricsHandler : DelegatingHandler
{
    private static readonly Counter Requests = Prometheus.Metrics.CreateCounter("conduit_webhook_requests_total", "Actual HTTP attempts by response status",
        new CounterConfiguration { LabelNames = ["status"] });
    private static readonly Histogram Duration = Prometheus.Metrics.CreateHistogram("conduit_webhook_duration_ms", "HTTP attempt duration in milliseconds",
        new HistogramConfiguration { LabelNames = ["status"], Buckets = Histogram.ExponentialBuckets(1, 2, 15) });
    private static readonly Gauge Active = Prometheus.Metrics.CreateGauge("conduit_webhook_active_requests", "Active HTTP attempts");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var status = "error";
        Observe(() => Active.Inc());
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            status = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return response;
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        finally
        {
            Observe(() => { Active.Dec(); Requests.WithLabels(status).Inc(); Duration.WithLabels(status).Observe(watch.Elapsed.TotalMilliseconds); });
        }
    }
    private static void Observe(Action action) { try { action(); } catch (Exception) { /* Telemetry cannot change delivery. */ } }
}
