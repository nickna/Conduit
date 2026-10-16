using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;
using ConduitLLM.SchemaStartupFixtures;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Npgsql;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>Real HTTP, migrated PostgreSQL claims, provider transport, and production ledger settlement.</summary>
[Collection("Postgres advisory locks")]
public sealed class FunctionIdempotencyPersistenceParityTests(PostgresLockTestContainerFixture postgres)
{
    private const string ExecutePath = "v1/conduit/functions/execute";
    private const decimal Price = 0.12500001m;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExtraPrecisionProviderCostSettlesOnceAndReplaysAcrossHostsWithoutChangingTheSavedCost(
        bool runtime, bool interruptedSettlement)
    {
        const decimal reportedCost = 0.125000015m;
        const decimal billedCost = 0.12500002m;
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime);
        fixture.Provider.ReportCost("precision", reportedCost);
        fixture.FailSettlement = interruptedSettlement;
        var body = fixture.Body("precision");
        var first = await fixture.PostAsync(0, "precision-key", body);
        if (interruptedSettlement)
            first.AssertError(HttpStatusCode.ServiceUnavailable, "idempotency_unavailable");
        else
            first.AssertStatus(HttpStatusCode.OK);

        var claim = await fixture.WaitForClaimAsync("precision-key", FunctionExecutionClaimState.Completed);
        using (var saved = JsonDocument.Parse(claim.ResultJson!))
            Assert.Equal(reportedCost, saved.RootElement.GetProperty("actualCost").GetDecimal());
        if (interruptedSettlement)
        {
            Assert.Null(claim.BilledAtUtc);
            await fixture.AssertAccountingAsync(fixture.Primary, 0);
        }
        else
        {
            Assert.Equal(claim.ExecutionId, first.Id);
            Assert.Equal(reportedCost, first.Body.GetProperty("cost").GetProperty("actual").GetDecimal());
        }

        // Also covers already completed snapshots written by older hosts before the
        // precision fix: replay must settle the saved raw cost without dispatching again.
        fixture.FailSettlement = false;
        var retries = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => fixture.PostAsync(index % 2, "precision-key", body)));
        foreach (var retry in retries)
        {
            retry.AssertStatus(HttpStatusCode.OK);
            Assert.True(retry.Replayed);
            Assert.Equal(claim.ExecutionId, retry.Id);
            Assert.Equal(reportedCost, retry.Body.GetProperty("cost").GetProperty("actual").GetDecimal());
        }
        var settled = await fixture.WaitForClaimAsync("precision-key", FunctionExecutionClaimState.Completed, billed: true);
        Assert.Equal(claim.ResultJson, settled.ResultJson);
        Assert.Equal(1, fixture.Provider.Count("precision"));
        Assert.Equal(1, fixture.Provider.Total);
        await fixture.AssertAccountingAsync(fixture.Primary, 1, billedCost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalReplayConflictsScopesAndIndependentOperationsPreserveProviderAndChargeCounts(bool runtime)
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime);
        var original = fixture.Body("canonical", """{"z":[true,null,"雪"],"a":{"second":2,"first":1}}""");
        var reordered = fixture.Body("canonical", """{"a":{"first":1,"second":2},"z":[true,null,"雪"]}""");
        var first = await fixture.PostAsync(0, "shared-key", original);
        first.AssertStatus(HttpStatusCode.OK);
        var executionId = first.Id;
        var replay = await fixture.PostAsync(1, "shared-key", reordered);
        replay.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(executionId, replay.Id);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Body.GetProperty("output").GetRawText(), replay.Body.GetProperty("output").GetRawText());
        Assert.Equal(1, fixture.Provider.Count("canonical"));
        await fixture.AssertAccountingAsync(fixture.Primary, 1);

        foreach (var (key, duplicate) in new[]
                 {
                     ("shared-key", "shared-key"),
                     ("duplicate-new-key", "another-duplicate-key")
                 })
        {
            var rejectedHeaders = await fixture.PostDuplicateHeadersAsync(1, key, duplicate, original);
            Assert.StartsWith("HTTP/1.1 400 ", rejectedHeaders);
            Assert.Contains("invalid_idempotency_key", rejectedHeaders);
        }
        Assert.Equal(1, fixture.Provider.Count("canonical"));
        Assert.Equal(1, await fixture.CountClaimsAsync());

        foreach (var conflict in new[]
                 {
                     fixture.Body("changed-query"),
                     fixture.Body("canonical", """{"z":[true,null,"changed"],"a":{"second":2,"first":1}}"""),
                     fixture.Body("canonical", """{"z":[true,null,"雪"],"a":{"second":2,"first":1}}""", """{"owner":"changed"}"""),
                     fixture.Body("canonical", configurationId: fixture.Other.ConfigurationId)
                 })
        {
            var rejected = await fixture.PostAsync(1, "shared-key", conflict);
            rejected.AssertError(HttpStatusCode.Conflict, "idempotency_conflict");
            Assert.Equal(executionId, rejected.HeaderId);
        }
        Assert.Equal(1, fixture.Provider.Total);

        var otherScope = await fixture.PostAsync(1, "shared-key", original, fixture.Other);
        otherScope.AssertStatus(HttpStatusCode.OK);
        Assert.NotEqual(executionId, otherScope.Id);
        var wrongOwner = await fixture.GetAsync(0, executionId, fixture.Other);
        wrongOwner.AssertStatus(HttpStatusCode.NotFound);
        await fixture.AssertAccountingAsync(fixture.Other, 1);

        var distinct = await fixture.PostAsync(1, "another-key", original);
        distinct.AssertStatus(HttpStatusCode.OK);
        Assert.NotEqual(executionId, distinct.Id);
        var unkeyed1 = await fixture.PostAsync(0, null, original);
        var unkeyed2 = await fixture.PostAsync(1, null, original);
        unkeyed1.AssertStatus(HttpStatusCode.OK);
        unkeyed2.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(5, new[] { executionId, otherScope.Id, distinct.Id, unkeyed1.Id, unkeyed2.Id }.Distinct().Count());
        Assert.Equal(5, fixture.Provider.Total);
        Assert.Equal(3, await fixture.CountClaimsAsync());
        // Unkeyed calls still leave billing to the calling middleware. This fixture's
        // production claim store settles only the two keyed operations for this key.
        await fixture.AssertAccountingAsync(fixture.Primary, 2);

        await using (var disable = fixture.Source.CreateCommand("""
                         UPDATE "FunctionConfigurations" SET "IsEnabled" = FALSE WHERE "Id" = @configurationId
                         """))
        {
            disable.Parameters.AddWithValue("configurationId", fixture.Primary.ConfigurationId);
            await disable.ExecuteNonQueryAsync(fixture.Token);
        }
        var disabledReplay = await fixture.PostAsync(1, "shared-key", reordered);
        disabledReplay.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(executionId, disabledReplay.Id);
        Assert.Equal(5, fixture.Provider.Total);
        await fixture.AssertAccountingAsync(fixture.Primary, 2);

        await using (var exhaust = fixture.Source.CreateCommand("""
                         UPDATE "VirtualKeyGroups" SET "Balance" = 0 WHERE "Id" = @groupId
                         """))
        {
            exhaust.Parameters.AddWithValue("groupId", fixture.Primary.GroupId);
            await exhaust.ExecuteNonQueryAsync(fixture.Token);
        }
        var exhaustedReplay = await fixture.PostAsync(0, "shared-key", reordered);
        exhaustedReplay.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(executionId, exhaustedReplay.Id);
        var trailingSlashReplay = await fixture.PostAsync(1, "shared-key", reordered, trailingSlash: true);
        trailingSlashReplay.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(executionId, trailingSlashReplay.Id);
        Assert.True(trailingSlashReplay.Replayed);
        (await fixture.PostAsync(0, "new-key-after-depletion", original))
            .AssertError(HttpStatusCode.PaymentRequired, "insufficient_balance");
        (await fixture.PostAsync(1, null, original))
            .AssertError(HttpStatusCode.PaymentRequired, "insufficient_balance");
        Assert.Equal(5, fixture.Provider.Total);
        Assert.Equal(3, await fixture.CountClaimsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoIndependentHttpHostsHaveOneDurableOwnerAndCompletedReplaySurvivesHostRestart(bool runtime)
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime);
        var gate = fixture.Provider.Block("concurrent");
        var body = fixture.Body("concurrent");
        var ownerTask = fixture.PostAsync(0, "concurrent-key", body);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var claim = await fixture.FindClaimAsync("concurrent-key");
            Assert.NotNull(claim);
            Assert.Equal(FunctionExecutionClaimState.Dispatching, claim.State);
            var contenders = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(index => fixture.PostAsync(index % 2, "concurrent-key", body)));
            foreach (var contender in contenders)
            {
                contender.AssertError(HttpStatusCode.Conflict, "idempotency_in_progress");
                Assert.Equal(claim.ExecutionId, contender.HeaderId);
                Assert.Equal($"/v1/conduit/functions/executions/{claim.ExecutionId}", contender.Location);
                Assert.Equal("3", contender.RetryAfter);
            }
            Assert.Equal(1, fixture.Provider.Count("concurrent"));
            Assert.Equal(1, await fixture.CountClaimsAsync());
        }
        finally { gate.Release.TrySetResult(); }

        var owner = await ownerTask;
        owner.AssertStatus(HttpStatusCode.OK);
        await fixture.AssertAccountingAsync(fixture.Primary, 1);
        await fixture.RestartAsync(0);
        var replay = await fixture.PostAsync(0, "concurrent-key", body);
        replay.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(owner.Id, replay.Id);
        Assert.True(replay.Replayed);
        Assert.Equal(1, fixture.Provider.Count("concurrent"));
        await fixture.AssertAccountingAsync(fixture.Primary, 1);
    }

    [Fact]
    public async Task ClientDisconnectAfterDispatchDoesNotCancelTheOperationOrChargeItsRetryAgain()
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime: true);
        var gate = fixture.Provider.Block("disconnect");
        var body = fixture.Body("disconnect");
        using var disconnect = new CancellationTokenSource();
        var abandoned = fixture.PostAsync(0, "disconnect-key", body, cancellationToken: disconnect.Token);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var claim = await fixture.FindClaimAsync("disconnect-key");
        Assert.NotNull(claim);
        disconnect.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
        gate.Release.TrySetResult();
        await fixture.WaitForClaimAsync("disconnect-key", FunctionExecutionClaimState.Completed, billed: true);

        var retry = await fixture.PostAsync(1, "disconnect-key", body);
        retry.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(claim.ExecutionId, retry.Id);
        Assert.Equal(1, fixture.Provider.Count("disconnect"));
        await fixture.AssertAccountingAsync(fixture.Primary, 1);
    }

    [Fact]
    public async Task ProviderDisconnectLeavesAnUncertainOutcomeThatNeverAutomaticallyDispatchesAgain()
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime: true);
        fixture.Provider.Abort("uncertain");
        var body = fixture.Body("uncertain");
        var first = await fixture.PostAsync(0, "uncertain-key", body);
        Assert.True((int)first.Status >= 400, $"Provider transport failure must not appear successful: {first.Body}");
        var claim = await fixture.WaitForClaimAsync("uncertain-key", FunctionExecutionClaimState.Uncertain);
        await fixture.RestartAsync(0);
        foreach (var index in new[] { 0, 1, 0 })
        {
            var retry = await fixture.PostAsync(index, "uncertain-key", body);
            retry.AssertError(HttpStatusCode.Conflict, "idempotency_outcome_unknown");
            Assert.Equal(claim.ExecutionId, retry.HeaderId);
        }
        Assert.Equal(1, fixture.Provider.Count("uncertain"));
        await fixture.AssertAccountingAsync(fixture.Primary, 0);
    }

    [Fact]
    public async Task KnownProviderRejectionReplaysTheOriginalHttpFailureWithoutRedispatchOrCharge()
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime: true);
        fixture.Provider.Reject("known-rejection");
        var body = fixture.Body("known-rejection");
        var first = await fixture.PostAsync(0, "known-rejection-key", body);
        first.AssertStatus(HttpStatusCode.BadRequest);
        Assert.NotNull(first.HeaderId);
        var replay = await fixture.PostAsync(1, "known-rejection-key", body);
        replay.AssertStatus(HttpStatusCode.BadRequest);
        Assert.Equal(first.HeaderId, replay.HeaderId);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Body.GetRawText(), replay.Body.GetRawText());
        var claim = await fixture.WaitForClaimAsync("known-rejection-key", FunctionExecutionClaimState.Completed, billed: true);
        Assert.Equal(first.HeaderId, claim.ExecutionId);
        Assert.Equal(1, fixture.Provider.Count("known-rejection"));
        await fixture.AssertAccountingAsync(fixture.Primary, 0);
    }

    [Fact]
    public async Task FailureToRecordTheProviderResultLeavesAnUncertainClaimWithoutAnotherProviderCall()
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime: true);
        fixture.FailCompletion = true;
        var body = fixture.Body("completion-failure");
        var first = await fixture.PostAsync(0, "completion-failure-key", body);
        Assert.True((int)first.Status >= 400, $"Completion persistence failure must not appear successful: {first.Body}");
        var claim = await fixture.WaitForClaimAsync("completion-failure-key", FunctionExecutionClaimState.Uncertain);
        fixture.FailCompletion = false;
        var retry = await fixture.PostAsync(1, "completion-failure-key", body);
        retry.AssertError(HttpStatusCode.Conflict, "idempotency_outcome_unknown");
        Assert.Equal(claim.ExecutionId, retry.HeaderId);
        Assert.Equal(1, fixture.Provider.Count("completion-failure"));
    }

    [Fact]
    public async Task CompletedResultRetriesAnInterruptedSettlementWithoutRedispatchOrDuplicateDebit()
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime: true);
        fixture.FailSettlement = true;
        var body = fixture.Body("settlement-failure");
        var first = await fixture.PostAsync(0, "settlement-failure-key", body);
        first.AssertError(HttpStatusCode.ServiceUnavailable, "idempotency_unavailable");
        var claim = await fixture.WaitForClaimAsync("settlement-failure-key", FunctionExecutionClaimState.Completed);
        Assert.Null(claim.BilledAtUtc);
        await fixture.AssertAccountingAsync(fixture.Primary, 0);
        fixture.FailSettlement = false;
        var retries = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => fixture.PostAsync(index % 2, "settlement-failure-key", body)));
        foreach (var retry in retries)
        {
            retry.AssertStatus(HttpStatusCode.OK);
            Assert.Equal(claim.ExecutionId, retry.Id);
        }
        Assert.Equal(1, fixture.Provider.Count("settlement-failure"));
        await fixture.AssertAccountingAsync(fixture.Primary, 1);
    }

    [Fact]
    public async Task MigratedSchemaEnforcesScopedKeyAndGlobalExecutionIdentityUniqueness()
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime: true);
        var store = new NpgsqlFunctionExecutionIdempotencyStore(fixture.Source, new NpgsqlVirtualKeyRuntimeStore(fixture.Source));
        var hash = new string('a', 64);
        var fingerprint = new string('b', 64);
        var first = await store.ClaimAsync(fixture.Primary.KeyId, hash, fingerprint, fixture.Token);
        var other = await store.ClaimAsync(fixture.Other.KeyId, hash, fingerprint, fixture.Token);
        Assert.True(first.IsOwner);
        Assert.True(other.IsOwner);
        Assert.NotEqual(first.ExecutionId, other.ExecutionId);

        foreach (var duplicateKey in new[] { true, false })
        {
            await using var command = fixture.Source.CreateCommand("""
                INSERT INTO "FunctionExecutionClaims"
                    ("VirtualKeyId", "KeyHash", "Fingerprint", "ExecutionId", "BillingGroupId", "CreatedAtUtc", "State")
                SELECT "VirtualKeyId", @keyHash, "Fingerprint", @executionId, "BillingGroupId", "CreatedAtUtc", "State"
                FROM "FunctionExecutionClaims" WHERE "ExecutionId" = @original
                """);
            command.Parameters.AddWithValue("keyHash", duplicateKey ? hash : new string('c', 64));
            command.Parameters.AddWithValue("executionId", duplicateKey ? Guid.NewGuid() : first.ExecutionId);
            command.Parameters.AddWithValue("original", first.ExecutionId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(fixture.Token));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
        Assert.Equal(2, await fixture.CountClaimsAsync());
        Assert.Equal(0, fixture.Provider.Total);
    }

    [Fact]
    public async Task NativeFixtureCleanupRemovesOnlyItsKeysDurableClaims()
    {
        await using var fixture = await Fixture.CreateAsync(postgres.ConnectionString, runtime: true);
        var first = await fixture.PostAsync(0, "fixture-claim", fixture.Body("fixture-first"));
        var other = await fixture.PostAsync(1, "fixture-claim",
            fixture.Body("fixture-other", configurationId: fixture.Other.ConfigurationId), fixture.Other);
        first.AssertStatus(HttpStatusCode.OK);
        other.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(2, await fixture.CountClaimsAsync());
        await fixture.StopHostsAsync();

        Assert.Equal(fixture.Primary, await NativeFunctionFixture.DeleteAsync(fixture.Source, fixture.Primary.Owner, fixture.Token));
        Assert.Null(await fixture.FindClaimAsync("fixture-claim"));
        Assert.Equal(1, await fixture.CountClaimsAsync());
        Assert.Equal(fixture.Other, await NativeFunctionFixture.ReadAsync(fixture.Source, fixture.Other.Owner, fixture.Token));
        await fixture.AssertAccountingAsync(fixture.Other, 1);
    }

    private sealed record Response(HttpStatusCode Status, JsonElement Body, string? Replay, Guid? HeaderId,
        string? Location, string? RetryAfter)
    {
        internal Guid Id => Body.GetProperty("id").GetGuid();
        internal bool Replayed => string.Equals(Replay, "true", StringComparison.OrdinalIgnoreCase);
        internal void AssertStatus(HttpStatusCode expected) => Assert.True(Status == expected,
            $"Expected {(int)expected}, received {(int)Status}: {Body.GetRawText()}");
        internal void AssertError(HttpStatusCode status, string code)
        {
            AssertStatus(status);
            Assert.Equal(code, Body.GetProperty("error").GetProperty("code").GetString());
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _management;
        private readonly SchemaStartupDatabase _database;
        private readonly bool _runtime;
        private readonly List<WebApplication> _apps = [];
        private readonly List<HttpClient> _clients = [];
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromMinutes(3));
        private readonly IFunctionCredentialProtector _protector = new FunctionCredentialProtector(
            new EphemeralDataProtectionProvider(), NullLogger<FunctionCredentialProtector>.Instance);
        internal NpgsqlDataSource Source { get; }
        internal CountingProvider Provider { get; }
        internal NativeFunctionFixtureSnapshot Primary { get; private set; } = null!;
        internal NativeFunctionFixtureSnapshot Other { get; private set; } = null!;
        internal bool FailCompletion;
        internal bool FailSettlement;
        internal CancellationToken Token => _deadline.Token;

        private Fixture(string management, SchemaStartupDatabase database, bool runtime, CountingProvider provider)
        {
            _management = management;
            _database = database;
            _runtime = runtime;
            Provider = provider;
            Source = NpgsqlDataSource.Create(database.ConnectionString);
        }

        internal static async Task<Fixture> CreateAsync(string management, bool runtime)
        {
            var database = await SchemaStartupDatabase.CreateAsync(management, previousSchema: false);
            CountingProvider? provider = null;
            Fixture? fixture = null;
            try
            {
                await using (var context = new ConduitDbContext(new DbContextOptionsBuilder<ConduitDbContext>()
                                 .UseNpgsql(database.ConnectionString).Options))
                    await context.Database.MigrateAsync();
                provider = await CountingProvider.StartAsync();
                fixture = new Fixture(management, database, runtime, provider);
                var protectedKey = fixture._protector.Protect(NativeFunctionFixture.ApiKey)!;
                fixture.Primary = await NativeFunctionFixture.CreateAsync(fixture.Source, Guid.NewGuid(),
                    provider.Address, protectedKey, fixture.Token);
                fixture.Other = await NativeFunctionFixture.CreateAsync(fixture.Source, Guid.NewGuid(),
                    provider.Address, protectedKey, fixture.Token);
                await fixture.AddHostAsync();
                await fixture.AddHostAsync();
                return fixture;
            }
            catch
            {
                if (fixture is not null) await fixture.DisposeAsync();
                else
                {
                    if (provider is not null) await provider.DisposeAsync();
                    await SchemaStartupDatabase.DropAsync(management, database.Name);
                }
                throw;
            }
        }

        internal string Body(string query, string? payload = null, string? metadata = null, int? configurationId = null) =>
            $$"""{"function_configuration_id":{{configurationId ?? Primary.ConfigurationId}},"parameters":{"query":{{JsonSerializer.Serialize(query)}},"payload":{{payload ?? "{}"}}},"metadata":{{metadata ?? "{\"owner\":\"fixture\"}"}}} """;

        internal async Task<Response> PostAsync(int host, string? key, string body,
            NativeFunctionFixtureSnapshot? identity = null, CancellationToken cancellationToken = default,
            bool trailingSlash = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ExecutePath + (trailingSlash ? "/" : ""))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (identity ?? Primary).VirtualKey);
            if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
            using var response = await _clients[host].SendAsync(request, linked.Token);
            return await ReadAsync(response, linked.Token);
        }

        internal async Task<string> PostDuplicateHeadersAsync(int host, string key, string duplicate, string body)
        {
            // HttpClient may combine custom header values into one comma-separated
            // line. Send two actual HTTP fields to exercise ASP.NET's header count.
            var address = _clients[host].BaseAddress!;
            var payload = Encoding.UTF8.GetBytes(body);
            var headers = $"POST /{ExecutePath} HTTP/1.1\r\nHost: {address.Authority}\r\n" +
                $"Authorization: Bearer {Primary.VirtualKey}\r\n" +
                $"Idempotency-Key: {key}\r\nIdempotency-Key: {duplicate}\r\n" +
                $"Content-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = new TcpClient();
            await client.ConnectAsync(address.Host, address.Port, deadline.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
            await stream.WriteAsync(payload, deadline.Token);
            await stream.FlushAsync(deadline.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(deadline.Token);
        }

        internal async Task<Response> GetAsync(int host, Guid id, NativeFunctionFixtureSnapshot identity)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/conduit/functions/executions/{id}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.VirtualKey);
            using var response = await _clients[host].SendAsync(request, Token);
            return await ReadAsync(response, Token);
        }

        private static async Task<Response> ReadAsync(HttpResponseMessage response, CancellationToken token)
        {
            var content = await response.Content.ReadAsStringAsync(token);
            Assert.False(string.IsNullOrWhiteSpace(content), $"HTTP {(int)response.StatusCode} returned an empty response.");
            using var body = JsonDocument.Parse(content);
            string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.Single() : null;
            return new Response(response.StatusCode, body.RootElement.Clone(), Header("Idempotency-Replayed"),
                Guid.TryParse(Header("Function-Execution-Id"), out var id) ? id : null,
                Header("Location"), Header("Retry-After"));
        }

        internal async Task RestartAsync(int index)
        {
            _clients[index].Dispose();
            await _apps[index].StopAsync(Token);
            await _apps[index].DisposeAsync();
            var (app, client) = await StartHostAsync();
            _apps[index] = app;
            _clients[index] = client;
        }

        private async Task AddHostAsync()
        {
            var (app, client) = await StartHostAsync();
            _apps.Add(app);
            _clients.Add(client);
        }

        private async Task<(WebApplication App, HttpClient Client)> StartHostAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            if (Environment.GetEnvironmentVariable("CONDUIT_IDEMPOTENCY_TEST_LOG") == "1") builder.Logging.AddConsole();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddMemoryCache();
            // Use the production policy registrations so the uncertain-outcome
            // assertion also detects any hidden provider HTTP retry.
            builder.Services.AddSharedHttpClients();
            builder.Services.AddHttpClient("ExaFunctionClient")
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
            builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(_database.ConnectionString));
            builder.Services.AddSingleton<IFunctionExecutionConfigurationStore, NpgsqlFunctionExecutionConfigurationStore>();
            builder.Services.AddSingleton<IFunctionExecutionRuntimeWriter, NpgsqlFunctionExecutionRuntimeWriter>();
            builder.Services.AddSingleton<IFunctionExecutionRuntimeReader, NpgsqlFunctionExecutionRuntimeReader>();
            builder.Services.AddSingleton<IFunctionExecutionIdempotencyStore>(services => new FaultStore(this,
                new NpgsqlFunctionExecutionIdempotencyStore(services.GetRequiredService<NpgsqlDataSource>(),
                    new NpgsqlVirtualKeyRuntimeStore(services.GetRequiredService<NpgsqlDataSource>()))));
            builder.Services.AddSingleton<IFunctionCostService>(services => new StoreBackedFunctionCostService(
                new NpgsqlFunctionPricingRuntimeStore(services.GetRequiredService<NpgsqlDataSource>()),
                services.GetRequiredService<IMemoryCache>(), NullLogger<StoreBackedFunctionCostService>.Instance));
            builder.Services.AddSingleton<IFunctionCostCalculationService, FunctionCostCalculationService>();
            builder.Services.AddSingleton(_protector);
            builder.Services.AddSingleton<IFunctionClientFactory>(services => new FunctionClientFactory(
                services.GetRequiredService<IFunctionExecutionConfigurationStore>(), _protector,
                NullLoggerFactory.Instance, services.GetRequiredService<IHttpClientFactory>()));
            builder.Services.AddSingleton<IFunctionExecutionService>(services => CreateExecutionService(services));
            var balance = new Mock<ConduitLLM.Core.Interfaces.IVirtualKeyRuntimeService>();
            balance.Setup(service => service.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(async (string key, string? _) =>
                {
                    var identity = key == Primary.VirtualKey ? Primary : Other;
                    await using var command = Source.CreateCommand("SELECT \"Balance\" FROM \"VirtualKeyGroups\" WHERE \"Id\" = @id");
                    command.Parameters.AddWithValue("id", identity.GroupId);
                    var amount = (decimal)(await command.ExecuteScalarAsync(Token))!;
                    return amount > 0
                        ? VirtualKeyValidationOutcome.Success(new VirtualKey { Id = identity.KeyId, VirtualKeyGroupId = identity.GroupId })
                        : VirtualKeyValidationOutcome.Failure("insufficient_balance", 402, "Insufficient balance");
                });
            builder.Services.AddSingleton(balance.Object);
            builder.Services.AddScoped<FunctionsEndpoints>(services => CreateHandler(services));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var header = context.Request.Headers.Authorization.ToString();
                var identity = header == "Bearer " + Primary.VirtualKey ? Primary
                    : header == "Bearer " + Other.VirtualKey ? Other : null;
                if (identity is not null)
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("VirtualKeyId", identity.KeyId.ToString())], "fixture"));
                    context.Items["VirtualKeyId"] = identity.KeyId;
                    context.Items["VirtualKey"] = identity.VirtualKey;
                }
                await next(context);
            });
            app.MapPost("/" + ExecutePath, async (HttpContext context, FunctionsEndpoints handler) =>
            {
                var body = await context.Request.ReadFromJsonAsync<FunctionsEndpoints.FunctionExecutionRequest>(
                    GatewayJsonOptions.Create(), context.RequestAborted);
                var key = context.Request.Headers.TryGetValue("Idempotency-Key", out var value) ? value.ToString() : null;
                return await handler.ExecuteFunction(body!, key, context.RequestAborted);
            }).AddEndpointFilter<RequireBalanceEndpointFilter>();
            app.MapGet("/v1/conduit/functions/executions/{id:guid}",
                (Guid id, HttpContext context, FunctionsEndpoints handler) => handler.GetExecution(id, context.RequestAborted));
            try { await app.StartAsync(Token); }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
            return (app, new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = AddressOf(app), Timeout = Timeout.InfiniteTimeSpan
            });
        }

        private FunctionExecutionService CreateExecutionService(IServiceProvider services)
        {
            var costs = services.GetRequiredService<IFunctionCostCalculationService>();
            var clients = services.GetRequiredService<IFunctionClientFactory>();
            var claims = services.GetRequiredService<IFunctionExecutionIdempotencyStore>();
            if (_runtime)
                return FunctionExecutionService.CreateWithRuntimePersistence(
                    services.GetRequiredService<IFunctionExecutionConfigurationStore>(),
                    services.GetRequiredService<IFunctionExecutionRuntimeWriter>(),
                    services.GetRequiredService<IFunctionExecutionRuntimeReader>(), costs, clients, _protector,
                    NullLogger<FunctionExecutionService>.Instance, claims);
            var factory = new ContextFactory(_database.ConnectionString);
            return FunctionExecutionService.CreateWithIdempotency(
                new FunctionConfigurationRepository(factory, NullLogger<FunctionConfigurationRepository>.Instance),
                new FunctionCredentialRepository(factory, NullLogger<FunctionCredentialRepository>.Instance),
                new FunctionExecutionRepository(factory, NullLogger<FunctionExecutionRepository>.Instance), costs, clients,
                _protector, NullLogger<FunctionExecutionService>.Instance, claims);
        }

        private FunctionsEndpoints CreateHandler(IServiceProvider services)
        {
            var service = services.GetRequiredService<IFunctionExecutionService>();
            var validation = new FunctionParameterValidationService(NullLogger<FunctionParameterValidationService>.Instance);
            var events = Mock.Of<IEventPublisher>();
            var accessor = services.GetRequiredService<IHttpContextAccessor>();
            var errors = new ProviderErrorTranslator(new CustomerErrorOptions { Mode = CustomerErrorMode.External });
            return _runtime ? FunctionsEndpoints.CreateWithRuntimePersistence(service,
                services.GetRequiredService<IFunctionExecutionConfigurationStore>(), validation, events,
                accessor, NullLogger<FunctionsEndpoints>.Instance, errors)
                : new FunctionsEndpoints(service,
                    new FunctionConfigurationRepository(new ContextFactory(_database.ConnectionString),
                        NullLogger<FunctionConfigurationRepository>.Instance), validation, events,
                    accessor, NullLogger<FunctionsEndpoints>.Instance, errors);
        }

        internal async Task<int> CountClaimsAsync()
        {
            await using var command = Source.CreateCommand("SELECT count(*) FROM \"FunctionExecutionClaims\"");
            return Convert.ToInt32(await command.ExecuteScalarAsync(Token));
        }

        internal Task<FunctionExecutionClaim?> FindClaimAsync(string key) =>
            new NpgsqlFunctionExecutionIdempotencyStore(Source, new NpgsqlVirtualKeyRuntimeStore(Source))
                .FindAsync(Primary.KeyId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant(), Token);

        internal async Task<FunctionExecutionClaim> WaitForClaimAsync(string key, FunctionExecutionClaimState state, bool billed = false)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var claim = await FindClaimAsync(key);
                if (claim is not null && claim.State == state && (!billed || claim.BilledAtUtc.HasValue)) return claim;
                await Task.Delay(25, deadline.Token);
            }
        }

        internal async Task AssertAccountingAsync(NativeFunctionFixtureSnapshot identity, int operations, decimal unitPrice = Price)
        {
            await using var command = Source.CreateCommand("""
                SELECT group_row."Balance", group_row."LifetimeSpent",
                    (SELECT count(*) FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @groupId AND "TransactionType" = 2),
                    (SELECT COALESCE(sum("Amount"), 0) FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @groupId AND "TransactionType" = 2),
                    (SELECT count(*) FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @groupId AND "IdempotencyKey" LIKE 'function-execution:%')
                FROM "VirtualKeyGroups" group_row WHERE "Id" = @groupId
                """);
            command.Parameters.AddWithValue("groupId", identity.GroupId);
            await using var reader = await command.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(100m - operations * unitPrice, reader.GetDecimal(0));
            Assert.Equal(operations * unitPrice, reader.GetDecimal(1));
            Assert.Equal(operations, reader.GetInt64(2));
            Assert.Equal(operations * unitPrice, reader.GetDecimal(3));
            Assert.Equal(operations, reader.GetInt64(4));
        }

        public async ValueTask DisposeAsync()
        {
            Provider.ReleaseAll();
            await StopHostsAsync();
            await Provider.DisposeAsync();
            await Source.DisposeAsync();
            _deadline.Dispose();
            await SchemaStartupDatabase.DropAsync(_management, _database.Name);
        }

        internal async Task StopHostsAsync()
        {
            foreach (var client in _clients) client.Dispose();
            foreach (var app in _apps)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            _clients.Clear();
            _apps.Clear();
        }
    }

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connectionString).Options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class FaultStore(Fixture fixture, IFunctionExecutionIdempotencyStore inner) : IFunctionExecutionIdempotencyStore
    {
        public Task<FunctionExecutionClaim> ClaimAsync(int keyId, string keyHash, string fingerprint, CancellationToken cancellationToken = default) =>
            inner.ClaimAsync(keyId, keyHash, fingerprint, cancellationToken);
        public Task<FunctionExecutionClaim?> FindAsync(int keyId, string keyHash, CancellationToken cancellationToken = default) =>
            inner.FindAsync(keyId, keyHash, cancellationToken);
        public Task<bool> MarkDispatchedAsync(FunctionExecutionClaim claim, CancellationToken cancellationToken = default) =>
            inner.MarkDispatchedAsync(claim, cancellationToken);
        public Task<bool> CompleteAsync(FunctionExecutionClaim claim, string json, CancellationToken cancellationToken = default) =>
            fixture.FailCompletion ? throw new IOException("Injected loss of completion persistence after provider dispatch")
                : inner.CompleteAsync(claim, json, cancellationToken);
        public Task<bool> MarkUncertainAsync(FunctionExecutionClaim claim, CancellationToken cancellationToken = default) =>
            inner.MarkUncertainAsync(claim, cancellationToken);
        public Task SettleBillingAsync(FunctionExecutionClaim claim, decimal cost, CancellationToken cancellationToken = default) =>
            fixture.FailSettlement ? throw new IOException("Injected settlement outage after durable completion")
                : inner.SettleBillingAsync(claim, cost, cancellationToken);
    }

    private sealed class CountingProvider : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ProviderGate> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _aborted = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _rejected = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, decimal> _reportedCosts = new(StringComparer.Ordinal);
        private readonly WebApplication _app;
        internal Uri Address => AddressOf(_app);
        internal int Total => _counts.Values.Sum();
        internal int Count(string query) => _counts.GetValueOrDefault(query);
        private CountingProvider(WebApplication app) => _app = app;

        internal static async Task<CountingProvider> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var provider = new CountingProvider(app);
            app.MapPost("/search", async context =>
            {
                if (context.Request.Headers["x-api-key"] != NativeFunctionFixture.ApiKey)
                {
                    context.Response.StatusCode = 401;
                    return;
                }
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                var query = document.RootElement.GetProperty("query").GetString()!;
                provider._counts.AddOrUpdate(query, 1, (_, count) => count + 1);
                if (provider._gates.TryGetValue(query, out var gate))
                {
                    gate.Started.TrySetResult();
                    await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                if (provider._aborted.ContainsKey(query))
                {
                    context.Abort();
                    return;
                }
                context.Response.ContentType = "application/json";
                if (provider._rejected.ContainsKey(query))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("""{"error":{"message":"counting provider private rejection"}}""");
                    return;
                }
                var response = NativeProviderStub.FunctionSuccessResponse;
                if (provider._reportedCosts.TryGetValue(query, out var reportedCost))
                    response = $$"""{"costDollars":{"total":{{reportedCost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},{{response[1..]}}""";
                await context.Response.WriteAsync(response);
            });
            await app.StartAsync();
            return provider;
        }

        internal ProviderGate Block(string query) => _gates.GetOrAdd(query, _ => new ProviderGate());
        internal void Abort(string query) => _aborted.TryAdd(query, 0);
        internal void Reject(string query) => _rejected.TryAdd(query, 0);
        internal void ReportCost(string query, decimal cost) => _reportedCosts[query] = cost;
        internal void ReleaseAll()
        {
            foreach (var gate in _gates.Values) gate.Release.TrySetResult();
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseAll();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class ProviderGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Uri AddressOf(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/");
}
