using System.Net;

namespace ConduitLLM.Tests.Gateway.Metrics;

public sealed class NativeDependencyRecoveryProbeTests
{
    [Fact]
    public async Task HealthyOutageRecovery_RequiresExactMatricesAndRestoresBoth()
    {
        using var fixture = new Fixture();
        await fixture.Probe.RunAsync();
        Assert.Equal(new[] { "redis/block", "redis/restore", "postgres/block", "postgres/restore" }, fixture.Handler.Controls.Take(4));
        Assert.Equal(new[] { "postgres/restore", "redis/restore" }, fixture.Handler.Controls.Skip(4).Order());
        Assert.Contains("admin:503/200", fixture.Handler.Health);
        Assert.Contains("primary:503/200", fixture.Handler.Health);
        Assert.Contains("secondary:503/200", fixture.Handler.Health);
        Assert.False(fixture.Handler.RedisBlocked);
        Assert.False(fixture.Handler.PostgresBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLivenessOrHttpTimeout_CannotCountAsReady503(bool timeout)
    {
        using var fixture = new Fixture();
        fixture.Handler.FailHealthDuringRedis = true;
        fixture.Handler.ThrowTimeout = timeout;
        if (timeout) await Assert.ThrowsAsync<TaskCanceledException>(() => fixture.Probe.RunAsync());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Probe.RunAsync());
        Assert.Equal(new[] { "redis/block", "redis/restore", "postgres/restore" }, fixture.Handler.Controls);
        Assert.False(fixture.Handler.RedisBlocked);
        Assert.False(fixture.Handler.PostgresBlocked);
        Assert.DoesNotContain("postgres/block", fixture.Handler.Controls);
    }

    [Fact]
    public async Task AlternatingHostReadiness_CannotAccumulateSuccessAcrossMatrices()
    {
        using var fixture = new Fixture();
        fixture.Handler.AlternateRedisReadiness = true;
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Probe.RunAsync());
        Assert.Contains("Non-matching matrices", failure.Message);
        Assert.DoesNotContain("postgres/block", fixture.Handler.Controls);
    }

    [Fact]
    public async Task IncompleteRedisRecovery_CannotBeginPostgresOutage()
    {
        using var fixture = new Fixture();
        fixture.Handler.FailRedisRecovery = true;
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Probe.RunAsync());
        Assert.Contains("Recovery never converged", failure.Message);
        Assert.DoesNotContain("postgres/block", fixture.Handler.Controls);
        Assert.Contains("postgres/restore", fixture.Handler.Controls);
    }

    [Fact]
    public async Task FailedBlockAcknowledgment_StillRestoresBothDependencies()
    {
        using var fixture = new Fixture();
        fixture.Handler.FailBlockAcknowledgment = true;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Probe.RunAsync());
        Assert.Contains("redis/block returned 500", failure.Message);
        Assert.Contains("redis/restore", fixture.Handler.Controls);
        Assert.Contains("postgres/restore", fixture.Handler.Controls);
        Assert.False(fixture.Handler.RedisBlocked);
    }

    [Fact]
    public async Task CleanupFailures_PreserveOriginalFailureAndDispatchBothRestores()
    {
        using var fixture = new Fixture();
        fixture.Handler.FailHealthDuringRedis = true;
        fixture.Handler.FailRedisRestore = true;
        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Probe.RunAsync());
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains("health response", failure.InnerExceptions[0].Message);
        Assert.Contains("redis/restore", failure.InnerExceptions[1].Message);
        Assert.Equal(new[] { "redis/block", "redis/restore", "postgres/restore" }, fixture.Handler.Controls);
    }

    private sealed class Fixture : IDisposable
    {
        public Handler Handler { get; } = new();
        private readonly List<HttpClient> _clients = [];
        public NativeDependencyRecoveryProbe Probe { get; }

        public Fixture()
        {
            HttpClient Client(string host)
            {
                var client = new HttpClient(Handler, disposeHandler: false) { BaseAddress = new Uri($"http://{host}/") };
                _clients.Add(client);
                return client;
            }
            Probe = new NativeDependencyRecoveryProbe(Client("admin"), Client("primary"), Client("secondary"), Client("control"), "owned-token",
                phaseTimeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.Zero);
        }

        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Controls { get; } = [];
        public List<string> Health { get; } = [];
        public bool RedisBlocked { get; private set; }
        public bool PostgresBlocked { get; private set; }
        public bool FailHealthDuringRedis { get; set; }
        public bool ThrowTimeout { get; set; }
        public bool FailRedisRestore { get; set; }
        public bool AlternateRedisReadiness { get; set; }
        public bool FailRedisRecovery { get; set; }
        public bool FailBlockAcknowledgment { get; set; }
        private bool _redisWasBlocked;
        private int _redisMatrices;
        private int _recoveryMatrices;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath.Trim('/');
            if (host == "control")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("Bearer owned-token", request.Headers.Authorization?.ToString());
                Controls.Add(path);
                if (path == "redis/restore" && FailRedisRestore) return Reply(HttpStatusCode.InternalServerError);
                if (path.StartsWith("redis/")) RedisBlocked = path.EndsWith("/block");
                _redisWasBlocked |= RedisBlocked;
                if (path.StartsWith("postgres/")) PostgresBlocked = path.EndsWith("/block");
                if (path == "redis/block" && FailBlockAcknowledgment) return Reply(HttpStatusCode.InternalServerError);
                return Reply(HttpStatusCode.NoContent);
            }
            var readiness = PostgresBlocked || (RedisBlocked && host != "admin") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            if (RedisBlocked && AlternateRedisReadiness && path == "health/ready" && host != "admin")
            {
                if (host == "primary" && ++_redisMatrices == 3) throw new HttpRequestException("Non-matching matrices observed.");
                readiness = (_redisMatrices % 2 == 1) == (host == "primary") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            }
            if (!RedisBlocked && _redisWasBlocked && FailRedisRecovery && host == "primary" && path == "health/ready")
            {
                if (++_recoveryMatrices == 3) throw new HttpRequestException("Recovery never converged.");
                readiness = HttpStatusCode.ServiceUnavailable;
            }
            if (RedisBlocked && host == "primary" && FailHealthDuringRedis)
            {
                if (ThrowTimeout) throw new TaskCanceledException("HTTP request timed out.");
                if (path == "health/live") return Reply(HttpStatusCode.InternalServerError);
            }
            Health.Add($"{host}:{(int)readiness}/200");
            return Reply(path == "health/ready" ? readiness : HttpStatusCode.OK);
        }

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status) => Task.FromResult(new HttpResponseMessage(status));
    }
}
