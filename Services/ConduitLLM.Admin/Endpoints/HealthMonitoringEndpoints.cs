using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using StackExchange.Redis;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Controller providing health monitoring data for dashboards.
    /// </summary>
    public static class HealthMonitoringEndpoints
    {
        /// <summary>
        /// Number of failed requests in a one-hour window before it is reported as an incident.
        /// </summary>
        internal const int IncidentErrorThreshold = 10;

        /// <summary>Smallest history window the endpoint will report on.</summary>
        internal const int MinHistoryHours = 1;

        /// <summary>Largest history window the endpoint will report on (30 days).</summary>
        internal const int MaxHistoryHours = 720;

        /// <summary>
        /// Truncates a UTC timestamp to the start of its hour, preserving <see cref="DateTimeKind.Utc"/>.
        /// Incident timestamps built with <c>new DateTime(...)</c> defaulted to
        /// <see cref="DateTimeKind.Unspecified"/> and serialized without a <c>Z</c> suffix, so clients
        /// read them as local time while every sibling timestamp in the response was UTC.
        /// </summary>
        internal static DateTime FloorToHourUtc(DateTime value) =>
            new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);

        public static IEndpointRouteBuilder MapHealthMonitoringEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/v1/admin/health-status")
                .AddEndpointFilter<OperationLoggingEndpointFilter>()
                .WithTags("Health Monitoring");
            group.MapGet("/services", GetServiceHealth)
                .WithName("HealthMonitoring_GetServiceHealth")
                .Produces<ServiceHealthResponse>();
            group.MapGet("/incidents", GetIncidents)
                .WithName("HealthMonitoring_GetIncidents")
                .Produces<IncidentsResponse>();
            group.MapGet("/history", GetHealthHistory)
                .WithName("HealthMonitoring_GetHealthHistory")
                .Produces<HealthHistoryResponse>();
            return app;
        }

        /// <summary>
        /// Gets current service health status.
        /// </summary>
        /// <param name="dbContextFactory">Factory for the configuration database context.</param>
        /// <param name="healthCheckService">The Admin's registered ASP.NET health checks.</param>
        /// <param name="heartbeatStore">Store of cross-service liveness heartbeats.</param>
        /// <param name="configuration">Runtime configuration used to identify messaging mode.</param>
        /// <param name="hostEnvironment">Hosting environment used to assess in-memory messaging.</param>
        /// <param name="mediaStorageProbe">Non-mutating probe for the configured media store.</param>
        /// <param name="serviceProvider">Service provider used to resolve optional Redis connectivity.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Service health information.</returns>
        private static async Task<IResult> GetServiceHealth(
            [FromServices] IDbContextFactory<ConduitDbContext> dbContextFactory,
            [FromServices] HealthCheckService healthCheckService,
            [FromServices] IServiceHeartbeatStore heartbeatStore,
            [FromServices] IConfiguration configuration,
            [FromServices] IHostEnvironment hostEnvironment,
            [FromServices] IMediaStorageHealthProbe mediaStorageProbe,
            [FromServices] IServiceProvider serviceProvider,
            CancellationToken cancellationToken)
        {
            using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var services = new List<ServiceStatusDto>();

            var gatewayHeartbeatsTask = heartbeatStore.GetAllAsync(
                RedisKeys.ServiceHeartbeat.GatewayServiceId, cancellationToken);
            var adminHeartbeatsTask = heartbeatStore.GetAllAsync(
                RedisKeys.ServiceHeartbeat.AdminServiceId, cancellationToken);
            await Task.WhenAll(gatewayHeartbeatsTask, adminHeartbeatsTask);

            services.Add(BuildClusterServiceStatus(
                "core-api",
                "Gateway API",
                gatewayHeartbeatsTask.Result));
            services.Add(BuildClusterServiceStatus(
                "admin-api",
                "Admin API",
                adminHeartbeatsTask.Result));

            // Database — genuinely probed (SELECT 1) with a real response time and best-effort
            // server uptime (replacing the previous 30-day placeholder).
            var dbHealthCheck = await CheckDatabaseHealth(dbContext, cancellationToken);
            var dbUptime = await GetDatabaseUptimeAsync(dbContext, cancellationToken);
            var dbServerVersion = await GetDatabaseServerVersionAsync(dbContext, cancellationToken);
            services.Add(new ServiceStatusDto
            {
                Id = "database",
                Name = "PostgreSQL Database",
                Status = dbHealthCheck.IsHealthy ? "healthy" : "unhealthy",
                Version = dbServerVersion,
                Uptime = dbUptime,
                LastCheck = DateTime.UtcNow,
                ResponseTime = dbHealthCheck.ResponseTime,
                Details = new
                {
                    ConnectionPooling = true,
                    Provider = dbContext.Database.ProviderName ?? "unknown"
                }
            });

            services.Add(await BuildRedisStatusAsync(
                serviceProvider.GetService<IConnectionMultiplexer>(),
                cancellationToken));
            services.Add(await BuildMessagingStatusAsync(
                healthCheckService,
                configuration,
                hostEnvironment,
                cancellationToken));
            services.Add(await BuildMediaStorageStatusAsync(mediaStorageProbe, cancellationToken));

            // Calculate overall health. "unknown" services (e.g. a Gateway not seen yet) are
            // not counted as healthy — they pull the rollup down to at least "degraded".
            var healthyCount = services.Count(s => s.Status == "healthy");
            var degradedCount = services.Count(s => s.Status == "degraded");
            var unhealthyCount = services.Count(s => s.Status == "unhealthy");
            var unknownCount = services.Count(s => s.Status == "unknown");

            return Results.Ok(new ServiceHealthResponse
            {
                Timestamp = DateTime.UtcNow,
                OverallStatus = unhealthyCount > 0
                    ? "unhealthy"
                    : (degradedCount > 0 || unknownCount > 0) ? "degraded" : "healthy",
                Summary = new ServiceHealthSummary
                {
                    Healthy = healthyCount,
                    Degraded = degradedCount,
                    Unhealthy = unhealthyCount,
                    Unknown = unknownCount,
                    Total = services.Count
                },
                Services = services
            });
        }

        /// <summary>
        /// Gets incident history.
        /// </summary>
        /// <param name="dbContextFactory">Factory for the configuration database context.</param>
        /// <param name="days">Number of days to look back (default: 7).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Incident history data.</returns>
        private static async Task<IResult> GetIncidents(
            [FromServices] IDbContextFactory<ConduitDbContext> dbContextFactory,
            [FromQuery] int days = 7,
            CancellationToken cancellationToken = default)
        {
            using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var startDate = now.AddDays(-days);

            // Bucket on exact UTC hour boundaries. Grouping by `Timestamp.Date`/`.Hour` translated to
            // date_trunc over a timestamptz column, which resolves in the PostgreSQL server's
            // `timezone` GUC rather than UTC; offsetting from a UTC anchor keeps the windows correct
            // regardless of how the server is configured.
            var windowStart = FloorToHourUtc(startDate);
            var currentHour = FloorToHourUtc(now);

            var errorSpikes = await QueryErrorSpikes(dbContext.RequestLogs, windowStart)
                .ToListAsync(cancellationToken);

            var allIncidents = BuildIncidents(errorSpikes, windowStart, currentHour)
                .OrderByDescending(i => i.StartTime)
                .ToList();

            return Results.Ok(new IncidentsResponse
            {
                Timestamp = now,
                TimeRange = new TimeRangeDto { Start = windowStart, End = now },
                TotalIncidents = allIncidents.Count,
                ActiveIncidents = allIncidents.Count(i => i.Status == "active"),
                IncidentsByType = allIncidents.GroupBy(i => i.Type).Select(g => new IncidentTypeCountDto
                {
                    Type = g.Key,
                    Count = g.Count()
                }).ToList(),
                IncidentsBySeverity = allIncidents.GroupBy(i => i.Severity).Select(g => new IncidentSeverityCountDto
                {
                    Severity = g.Key,
                    Count = g.Count()
                }).ToList(),
                Incidents = allIncidents
            });
        }

        /// <summary>
        /// Gets health history data.
        /// </summary>
        /// <param name="dbContextFactory">Factory for the configuration database context.</param>
        /// <param name="hours">Number of hours to look back (default: 24).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Health history time series.</returns>
        private static async Task<IResult> GetHealthHistory(
            [FromServices] IDbContextFactory<ConduitDbContext> dbContextFactory,
            [FromQuery] int hours = 24,
            CancellationToken cancellationToken = default)
        {
            using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // Bound the window so a caller cannot ask for an unbounded number of buckets.
            var requestedHours = Math.Clamp(hours, MinHistoryHours, MaxHistoryHours);
            var now = DateTime.UtcNow;
            var startTime = now.AddHours(-requestedHours);
            var intervalMinutes = requestedHours <= 24 ? 15 : 60; // 15 min intervals for 24h, 1h for longer

            // One grouped query for the whole window. This previously issued a separate round trip
            // per interval — 96 sequential queries for the default 24 hours, on an endpoint the
            // dashboard polls.
            var buckets = await QueryHealthIntervals(dbContext.RequestLogs, startTime, now, intervalMinutes)
                .ToListAsync(cancellationToken);

            var healthHistory = BuildHealthHistory(buckets, startTime, now, intervalMinutes);

            return Results.Ok(new HealthHistoryResponse
            {
                Timestamp = now,
                TimeRange = new TimeRangeDto { Start = startTime, End = now },
                IntervalMinutes = intervalMinutes,
                History = healthHistory
            });
        }

        /// <summary>
        /// One hour of failed requests for a single model. Written as a mutable class projected via
        /// an object initializer because EF cannot translate a grouping aggregate into a positional
        /// record constructor.
        /// </summary>
        internal sealed class ErrorSpikeRow
        {
            /// <summary>Whole hours between the window start and this bucket's start.</summary>
            public int HourOffset { get; set; }

            /// <summary>Model the failed requests were routed to.</summary>
            public string Model { get; set; } = string.Empty;

            /// <summary>Number of failed requests in the bucket.</summary>
            public int ErrorCount { get; set; }

            /// <summary>Number of distinct HTTP status codes in the bucket.</summary>
            public int ErrorTypes { get; set; }
        }

        /// <summary>
        /// Groups failed requests into hourly buckets per model, keeping the whole aggregation in
        /// PostgreSQL. Buckets are measured as an epoch offset from <paramref name="windowStart"/>
        /// rather than via <c>Timestamp.Date</c>/<c>.Hour</c>, which would translate to
        /// <c>date_trunc</c> over a <c>timestamptz</c> column and resolve in the server's
        /// <c>timezone</c> setting instead of UTC.
        /// </summary>
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        internal static IQueryable<ErrorSpikeRow> QueryErrorSpikes(
            IQueryable<RequestLog> requestLogs,
            DateTime windowStart) =>
            requestLogs
                .Where(r => r.Timestamp >= windowStart && r.StatusCode >= 400)
                .GroupBy(r => new
                {
                    HourOffset = (int)Math.Floor((r.Timestamp - windowStart).TotalHours),
                    Model = r.ModelName
                })
                .Select(g => new ErrorSpikeRow
                {
                    HourOffset = g.Key.HourOffset,
                    Model = g.Key.Model,
                    ErrorCount = g.Count(),
                    ErrorTypes = g.Select(r => r.StatusCode).Distinct().Count()
                })
                .Where(row => row.ErrorCount >= IncidentErrorThreshold);

        /// <summary>
        /// Maps hourly error buckets to incidents with stable ids and UTC timestamps.
        /// </summary>
        /// <param name="spikes">Hourly error buckets from <see cref="QueryErrorSpikes"/>.</param>
        /// <param name="windowStart">UTC hour the query window began at.</param>
        /// <param name="currentHourUtc">Start of the current UTC hour.</param>
        internal static List<IncidentDto> BuildIncidents(
            IEnumerable<ErrorSpikeRow> spikes,
            DateTime windowStart,
            DateTime currentHourUtc) =>
            spikes.Select(spike =>
            {
                var spikeStart = windowStart.AddHours(spike.HourOffset);
                // Only the hour still accruing traffic can be ongoing. The previous check compared
                // calendar dates, so a spike that ended at 00:30 stayed "active" for the rest of the
                // day while one at 23:30 yesterday was reported "resolved" thirty minutes later.
                var isActive = spikeStart == currentHourUtc;
                return new IncidentDto
                {
                    // Deterministic so the dashboard can correlate the same incident across polls;
                    // this was previously a fresh Guid on every request.
                    Id = $"model-error-spike:{spike.Model}:{spikeStart:yyyyMMddTHHmmssZ}",
                    Title = $"Elevated error rate for model {spike.Model}",
                    Type = "model_error_spike",
                    Severity = spike.ErrorCount >= 50 ? "critical" : (spike.ErrorCount >= 25 ? "major" : "minor"),
                    Status = isActive ? "active" : "resolved",
                    StartTime = spikeStart,
                    EndTime = isActive ? null : spikeStart.AddHours(1),
                    // Request logs only record traffic served by the Gateway, so that is the affected
                    // service. The model is reported separately instead of being passed off as one —
                    // a model error spike is not an outage of a service named after the model.
                    AffectedService = "core-api",
                    AffectedModel = spike.Model,
                    Impact = $"{spike.ErrorCount} failed requests in a 1 hour period",
                    Details = new IncidentDetailsDto
                    {
                        ErrorCount = spike.ErrorCount,
                        UniqueErrorTypes = spike.ErrorTypes
                    }
                };
            }).ToList();

        /// <summary>
        /// Request totals for one interval of the health history. Mutable for the same reason as
        /// <see cref="ErrorSpikeRow"/> — EF projects grouping aggregates via object initializers.
        /// </summary>
        internal sealed class HealthIntervalRow
        {
            /// <summary>Zero-based interval index measured from the window start.</summary>
            public int Bucket { get; set; }

            /// <summary>Requests recorded in the interval.</summary>
            public int TotalRequests { get; set; }

            /// <summary>Requests in the interval that returned a 4xx or 5xx status.</summary>
            public int ErrorCount { get; set; }

            /// <summary>Mean response time in milliseconds, or null when the interval is empty.</summary>
            public double? AvgLatency { get; set; }
        }

        /// <summary>
        /// Aggregates request logs into fixed-width intervals in a single grouped query. Intervals
        /// are an epoch offset from <paramref name="startTime"/> for the same timezone-safety reason
        /// as <see cref="QueryErrorSpikes"/>. Intervals with no traffic produce no row.
        /// </summary>
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        internal static IQueryable<HealthIntervalRow> QueryHealthIntervals(
            IQueryable<RequestLog> requestLogs,
            DateTime startTime,
            DateTime endTime,
            int intervalMinutes) =>
            requestLogs
                .Where(r => r.Timestamp >= startTime && r.Timestamp < endTime)
                .GroupBy(r => (int)Math.Floor((r.Timestamp - startTime).TotalMinutes / intervalMinutes))
                .Select(g => new HealthIntervalRow
                {
                    Bucket = g.Key,
                    TotalRequests = g.Count(),
                    ErrorCount = g.Count(r => r.StatusCode >= 400),
                    AvgLatency = g.Average(r => (double?)r.ResponseTimeMs)
                });

        /// <summary>
        /// Expands sparse interval aggregates into a contiguous time series, filling intervals with
        /// no traffic the same way the previous per-interval queries did (100% health, zero volume).
        /// </summary>
        internal static List<HealthHistoryPointDto> BuildHealthHistory(
            IEnumerable<HealthIntervalRow> buckets,
            DateTime startTime,
            DateTime endTime,
            int intervalMinutes)
        {
            var byBucket = buckets.ToDictionary(bucket => bucket.Bucket);
            var history = new List<HealthHistoryPointDto>();

            for (var index = 0; startTime.AddMinutes((double)index * intervalMinutes) < endTime; index++)
            {
                var hasTraffic = byBucket.TryGetValue(index, out var row) && row.TotalRequests > 0;
                var errorRate = hasTraffic
                    ? row!.ErrorCount * 100.0 / row.TotalRequests
                    : 0;

                history.Add(new HealthHistoryPointDto
                {
                    Timestamp = startTime.AddMinutes((double)index * intervalMinutes),
                    SystemHealth = 100 - errorRate,
                    ResponseTime = hasTraffic ? row!.AvgLatency ?? 0 : 0,
                    RequestVolume = hasTraffic ? row!.TotalRequests : 0,
                    ErrorRate = errorRate
                });
            }

            return history;
        }

        internal static ServiceStatusDto BuildClusterServiceStatus(
            string id,
            string name,
            IReadOnlyList<ServiceHeartbeatSnapshot> heartbeats,
            DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (heartbeats.Count == 0)
            {
                return new ServiceStatusDto
                {
                    Id = id,
                    Name = name,
                    Status = "unknown",
                    LastCheck = now,
                    Details = new
                    {
                        Source = "heartbeat",
                        HealthyInstances = 0,
                        TotalInstances = 0,
                        Reason = "No instance heartbeat has been received"
                    }
                };
            }

            var instances = heartbeats.Select(heartbeat =>
            {
                var payload = heartbeat.Heartbeat;
                var interval = payload.IntervalSeconds > 0
                    ? payload.IntervalSeconds
                    : ServiceHeartbeatEvaluator.DefaultIntervalSeconds;
                var age = Math.Max(0, (now - heartbeat.ReceivedAtUtc).TotalSeconds);
                var freshness = ServiceHeartbeatEvaluator.EvaluateStatus(age, interval);
                var reported = NormalizeReportedStatus(payload.Status);
                return new ServiceInstanceStatusDto
                {
                    InstanceId = payload.InstanceId,
                    Status = WorstStatus(freshness, reported),
                    Version = payload.Version,
                    CommitSha = payload.CommitSha,
                    BuildTimestamp = payload.BuildTimestamp,
                    Uptime = TimeSpan.FromSeconds(Math.Max(0, payload.UptimeSeconds)),
                    LastHeartbeat = heartbeat.ReceivedAtUtc,
                    HeartbeatAgeSeconds = Math.Round(age, 1),
                    HeartbeatIntervalSeconds = interval
                };
            }).OrderBy(instance => instance.InstanceId, StringComparer.Ordinal).ToList();

            var healthy = instances.Count(instance => instance.Status == "healthy");
            var versions = instances.Select(instance => instance.Version).Distinct(StringComparer.Ordinal).ToArray();
            return new ServiceStatusDto
            {
                Id = id,
                Name = name,
                Status = healthy == instances.Count
                    ? "healthy"
                    : healthy > 0
                        ? "degraded"
                        : "unhealthy",
                Version = versions.Length == 1 ? versions[0] : "mixed",
                Instances = instances,
                Uptime = instances.Max(instance => instance.Uptime),
                LastCheck = instances.Max(instance => instance.LastHeartbeat),
                ResponseTime = null,
                Details = new
                {
                    Source = "heartbeat",
                    HealthyInstances = healthy,
                    TotalInstances = instances.Count
                }
            };
        }

        internal static async Task<ServiceStatusDto> BuildRedisStatusAsync(
            IConnectionMultiplexer? redis,
            CancellationToken cancellationToken,
            bool? configuredOverride = null)
        {
            var configured = configuredOverride
                ?? !string.IsNullOrWhiteSpace(RedisUrlParser.ResolveConnectionString());
            if (!configured)
            {
                return new ServiceStatusDto
                {
                    Id = "redis",
                    Name = "Redis",
                    Status = "degraded",
                    LastCheck = DateTime.UtcNow,
                    Details = new
                    {
                        Configured = false,
                        Mode = "in-memory fallback",
                        Description = "Redis is not configured; cluster-wide ephemeral state is unavailable"
                    }
                };
            }

            if (redis == null)
            {
                return new ServiceStatusDto
                {
                    Id = "redis",
                    Name = "Redis",
                    Status = "unhealthy",
                    LastCheck = DateTime.UtcNow,
                    Details = new { Configured = true, Error = "Redis connection is unavailable" }
                };
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ping = await redis.GetDatabase().PingAsync();
                var endpoint = redis.GetEndPoints().FirstOrDefault();
                var version = endpoint == null ? null : redis.GetServer(endpoint).Version?.ToString();
                stopwatch.Stop();
                return new ServiceStatusDto
                {
                    Id = "redis",
                    Name = "Redis",
                    Status = "healthy",
                    Version = version,
                    LastCheck = DateTime.UtcNow,
                    ResponseTime = (int)stopwatch.ElapsedMilliseconds,
                    Details = new
                    {
                        Configured = true,
                        PingMilliseconds = Math.Round(ping.TotalMilliseconds, 1)
                    }
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                return new ServiceStatusDto
                {
                    Id = "redis",
                    Name = "Redis",
                    Status = "unhealthy",
                    LastCheck = DateTime.UtcNow,
                    ResponseTime = (int)stopwatch.ElapsedMilliseconds,
                    Details = new { Configured = true, Error = ex.Message }
                };
            }
        }

        internal static async Task<ServiceStatusDto> BuildMessagingStatusAsync(
            HealthCheckService healthCheckService,
            IConfiguration configuration,
            IHostEnvironment hostEnvironment,
            CancellationToken cancellationToken)
        {
            if (WolverineMessagingExtensions.UsesInMemoryTransport(configuration))
            {
                return new ServiceStatusDto
                {
                    Id = "messaging",
                    Name = "Messaging",
                    Status = hostEnvironment.IsDevelopment() ? "healthy" : "degraded",
                    LastCheck = DateTime.UtcNow,
                    Details = new
                    {
                        Backend = "Wolverine",
                        Transport = "in-memory",
                        Durable = false,
                        Description = hostEnvironment.IsDevelopment()
                            ? "In-memory transport is expected in Development"
                            : "In-memory transport is non-durable outside Development"
                    }
                };
            }

            try
            {
                var report = await healthCheckService.CheckHealthAsync(
                    registration => registration.Tags.Contains("messaging"),
                    cancellationToken);
                var hasChecks = report.Entries.Count > 0;
                return new ServiceStatusDto
                {
                    Id = "messaging",
                    Name = "Messaging",
                    Status = hasChecks ? MapHealthStatus(report.Status) : "unhealthy",
                    LastCheck = DateTime.UtcNow,
                    ResponseTime = (int)report.TotalDuration.TotalMilliseconds,
                    Details = new
                    {
                        Backend = "Wolverine",
                        Transport = "PostgreSQL",
                        Durable = true,
                        Checks = report.Entries.Select(entry => new
                        {
                            Name = entry.Key,
                            Status = entry.Value.Status.ToString(),
                            entry.Value.Description
                        }).ToArray(),
                        Error = hasChecks ? null : "No messaging readiness check is registered"
                    }
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ServiceStatusDto
                {
                    Id = "messaging",
                    Name = "Messaging",
                    Status = "unhealthy",
                    LastCheck = DateTime.UtcNow,
                    Details = new { Backend = "Wolverine", Error = ex.Message }
                };
            }
        }

        internal static async Task<ServiceStatusDto> BuildMediaStorageStatusAsync(
            IMediaStorageHealthProbe probe,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await probe.ProbeAsync(timeout.Token);
                stopwatch.Stop();
                return new ServiceStatusDto
                {
                    Id = "media-storage",
                    Name = "Media Storage",
                    Status = result.Status,
                    LastCheck = DateTime.UtcNow,
                    ResponseTime = (int)stopwatch.ElapsedMilliseconds,
                    Details = new
                    {
                        result.Mode,
                        result.Description,
                        result.Ephemeral,
                        result.Bucket,
                        result.Endpoint
                    }
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new ServiceStatusDto
                {
                    Id = "media-storage",
                    Name = "Media Storage",
                    Status = "unhealthy",
                    LastCheck = DateTime.UtcNow,
                    ResponseTime = (int)stopwatch.ElapsedMilliseconds,
                    Details = new
                    {
                        Mode = "s3",
                        Error = timeout.IsCancellationRequested
                            ? "S3 bucket probe timed out after 3 seconds"
                            : ex.Message
                    }
                };
            }
        }

        private static string NormalizeReportedStatus(string? status) =>
            status?.ToLowerInvariant() is "healthy" or "degraded" or "unhealthy"
                ? status.ToLowerInvariant()
                : "unknown";

        private static string WorstStatus(string left, string right) =>
            StatusRank(left) >= StatusRank(right) ? left : right;

        private static int StatusRank(string status) => status switch
        {
            "unhealthy" => 3,
            "degraded" => 2,
            "unknown" => 1,
            _ => 0
        };

        private static string MapHealthStatus(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus status) => status switch
        {
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy => "healthy",
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded => "degraded",
            _ => "unhealthy"
        };

        private static async Task<(bool IsHealthy, int ResponseTime)> CheckDatabaseHealth(
            ConduitDbContext dbContext,
            CancellationToken cancellationToken)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                await dbContext.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
                stopwatch.Stop();
                return (true, (int)stopwatch.ElapsedMilliseconds);
            }
            catch
            {
                return (false, -1);
            }
        }

        /// <summary>
        /// Best-effort database server version. <see cref="System.Data.Common.DbConnection.ServerVersion"/>
        /// requires an <b>open</b> connection; the preceding SELECT 1 / uptime probes let EF close the
        /// pooled connection again, so we must reopen before reading it. Returns <c>null</c> on any
        /// failure rather than throwing — a version string is informational and must never fail the
        /// whole health response (previously surfaced as an HTTP 400 for the entire endpoint).
        /// </summary>
        private static async Task<string?> GetDatabaseServerVersionAsync(
            ConduitDbContext dbContext,
            CancellationToken cancellationToken)
        {
            try
            {
                var connection = dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken);
                }

                return connection.ServerVersion;
            }
            catch
            {
                return null; // best effort — version is informational only
            }
        }

        /// <summary>
        /// Best-effort PostgreSQL server uptime via <c>pg_postmaster_start_time()</c>. Returns
        /// <c>null</c> on any failure or for a non-PostgreSQL provider, replacing the previous
        /// hardcoded 30-day placeholder.
        /// </summary>
        private static async Task<TimeSpan?> GetDatabaseUptimeAsync(
            ConduitDbContext dbContext,
            CancellationToken cancellationToken)
        {
            try
            {
                var providerName = dbContext.Database.ProviderName ?? string.Empty;
                if (!providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var seconds = await dbContext.Database
                    .SqlQueryRaw<double>(
                        "SELECT EXTRACT(EPOCH FROM (now() - pg_postmaster_start_time()))::double precision AS \"Value\"")
                    .FirstOrDefaultAsync(cancellationToken);

                return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
            }
            catch
            {
                return null; // best effort — uptime is informational only
            }
        }

    }
}
