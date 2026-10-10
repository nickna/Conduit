using System.Diagnostics;

using ConduitLLM.Admin.DTOs;

namespace ConduitLLM.Admin.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapAdminMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/metrics")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Metrics");
        group.MapGet("/", GetAllMetrics).WithName("Metrics_GetAllMetrics")
            .WithSummary("Scrape Prometheus metrics")
            .WithDescription("The deployment's metrics middleware serves Prometheus or OpenMetrics text at /metrics before this endpoint handler runs. Supply the Admin master key; the middleware also checks network/authentication context.")
            .Produces<string>(StatusCodes.Status200OK, "text/plain", "application/openmetrics-text");

        return app;
    }

    private static IResult GetAllMetrics()
    {
        return Results.Ok(new AllMetricsDto
        {
            Timestamp = DateTime.UtcNow,
            Application = new ApplicationInfoDto
            {
                Name = "Conduit Admin API",
                Version = typeof(MetricsEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"
            },
            System = new SystemMetricsDto
            {
                CpuCount = Environment.ProcessorCount,
                WorkingSetMb = Environment.WorkingSet / 1024 / 1024,
                GcMemoryMb = GC.GetTotalMemory(false) / 1024 / 1024,
                ThreadCount = Process.GetCurrentProcess().Threads.Count,
                Uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()
            }
        });
    }
}
