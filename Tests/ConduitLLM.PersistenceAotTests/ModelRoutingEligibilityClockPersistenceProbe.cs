using System.Diagnostics;
using System.Runtime.ExceptionServices;

using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

internal static class ModelRoutingEligibilityClockPersistenceProbe
{
    private const string EffectiveIdentifier = "native-clock-effective";
    private const string ExpiryIdentifier = "native-clock-expiry";

    internal static async Task RunAsync(string connectionString)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MinPoolSize = 0, MaxPoolSize = 1, ApplicationName = $"native_model_clock_{Guid.NewGuid():N}",
            Timeout = 20, CommandTimeout = 10, CancellationTimeout = 1000
        };
        var schema = builder.SearchPath;
        Require(schema is not null && schema.StartsWith(PersistenceProbeLifecycle.SchemaPrefix, StringComparison.Ordinal) &&
            schema.Length == PersistenceProbeLifecycle.SchemaPrefix.Length + 32 &&
            Guid.TryParseExact(schema.AsSpan(PersistenceProbeLifecycle.SchemaPrefix.Length), "N", out _), "exact caller-owned GUID schema");
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        var store = new NpgsqlModelProviderMappingRuntimeStore(source);
        NpgsqlConnection? held = await source.OpenConnectionAsync(deadline.Token);
        var heldPid = held.ProcessID;
        // Whole-second precision survives PostgreSQL timestamp serialization;
        // the boundary remains at least six seconds ahead of fixture setup.
        var boundary = new DateTime((DateTime.UtcNow.AddSeconds(6).Ticks / TimeSpan.TicksPerSecond + 1) * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        using var caller = new CancellationTokenSource();
        Task<ModelCostRuntimeRecord?>? effective = null, expiry = null;
        DateTime effectiveBefore = default, effectiveAfter = default, expiryBefore = default, expiryAfter = default;
        DateTime releaseBefore = default, releaseAfter = default;
        ModelCostRuntimeRecord?[] queued = [];
        Exception? primary = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            await SeedAsync(builder.ConnectionString, boundary, deadline.Token);
            effectiveBefore = DateTime.UtcNow;
            effective = store.GetModelCostForIdentifierAsync(EffectiveIdentifier, caller.Token);
            effectiveAfter = DateTime.UtcNow;
            expiryBefore = DateTime.UtcNow;
            expiry = store.GetModelCostForIdentifierAsync(ExpiryIdentifier, caller.Token);
            expiryAfter = DateTime.UtcNow;
            Require(effectiveBefore <= effectiveAfter && effectiveAfter <= expiryBefore && expiryBefore <= expiryAfter &&
                expiryAfter <= boundary.AddSeconds(-3), "both direct invocation brackets finish at least three seconds before the boundary");
            Require(!effective.IsCompleted && !expiry.IsCompleted, "both reads wait behind the actual sole held pool slot");
            await WaitUntilAfterAsync(boundary.AddSeconds(1), deadline.Token);
            Require(!effective.IsCompleted && !expiry.IsCompleted, "both reads remain queued until the held slot is released");
            releaseBefore = DateTime.UtcNow;
            Require(releaseBefore >= boundary.AddSeconds(1), "release begins at least one second after the boundary");
            await held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            held = null;
            releaseAfter = DateTime.UtcNow;
            queued = await Task.WhenAll(effective, expiry).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            caller.Cancel();
            await DrainAsync();
            if (held is not null)
            {
                try { await held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            // Release cannot be skipped by a failed first drain, and a newly
            // unblocked read must finish before the source can be disposed.
            await DrainAsync();
        }
        ThrowFailures(primary, cleanupFailures, "held-boundary reads");
        AssertCost(queued[0], 3001, 10, boundary.AddDays(-1), null);
        AssertCost(queued[1], 3011, 20, boundary.AddDays(-1), boundary);
        var freshEffective = await ReadBoundedAsync(store, EffectiveIdentifier, deadline.Token);
        var freshExpiry = await ReadBoundedAsync(store, ExpiryIdentifier, deadline.Token);
        AssertCost(freshEffective, 3003, 20, boundary, null);
        AssertCost(freshExpiry, 3012, 10, boundary.AddDays(-1), null);
        await AssertCancellationAndReuseAsync(source, store, deadline.Token);
        Console.WriteLine($"Model routing eligibility clock persistence passed: poolSize=1; heldBackendPid={heldPid}; boundaryUtc={boundary:O}; effectiveDispatchBeforeUtc={effectiveBefore:O}; effectiveDispatchAfterUtc={effectiveAfter:O}; expiryDispatchBeforeUtc={expiryBefore:O}; expiryDispatchAfterUtc={expiryAfter:O}; releaseBeforeUtc={releaseBefore:O}; releaseAfterUtc={releaseAfter:O}; effectiveQueuedId={queued[0]!.Id}; expiryQueuedId={queued[1]!.Id}; effectiveFreshId={freshEffective!.Id}; expiryFreshId={freshExpiry!.Id}; invocation snapshot, filtering, ranking, cancellation and slot reuse.");

        async Task DrainAsync()
        {
            foreach (var pending in new[] { effective, expiry })
            {
                if (pending is null) continue;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (pending.IsCanceled) { }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
        }
    }

    private static async Task WaitUntilAfterAsync(DateTime target, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var previous = DateTime.UtcNow;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var observed = DateTime.UtcNow;
            Require(observed >= previous, "UTC clock did not move backwards during the held interval");
            if (observed >= target) return;
            Require(watch.Elapsed < TimeSpan.FromSeconds(15), "UTC boundary crossed within the independent monotonic budget");
            previous = observed;
            await Task.Delay(50, token);
        }
    }

    private static async Task SeedAsync(string connectionString, DateTime boundary, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = "native_clock_fixture" }.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "ModelCosts" ("Id", "CostName", "PricingModel", "InputCostPerMillionTokens", "OutputCostPerMillionTokens",
                "CreatedAt", "UpdatedAt", "ModelType", "IsActive", "EffectiveDate", "ExpiryDate", "Priority", "SupportsBatchProcessing") VALUES
                (3001, 'clock-3001', 0, 1.25, 2.5, @created, @created, 'chat', true, @yesterday, NULL, 10, false),
                (3002, 'clock-3002', 0, 1.25, 2.5, @created, @created, 'chat', true, @older, NULL, 10, false),
                (3003, 'clock-3003', 0, 1.25, 2.5, @created, @created, 'chat', true, @boundary, NULL, 20, false),
                (3011, 'clock-3011', 0, 1.25, 2.5, @created, @created, 'chat', true, @yesterday, @boundary, 20, false),
                (3012, 'clock-3012', 0, 1.25, 2.5, @created, @created, 'chat', true, @yesterday, NULL, 10, false),
                (3013, 'clock-3013', 0, 1.25, 2.5, @created, @created, 'chat', true, @older, NULL, 10, false),
                (3021, 'disabled-association', 0, 1.25, 2.5, @created, @created, 'chat', true, @older, NULL, 100, false),
                (3022, 'inactive-cost', 0, 1.25, 2.5, @created, @created, 'chat', false, @older, NULL, 200, false),
                (3023, 'already-expired', 0, 1.25, 2.5, @created, @created, 'chat', true, @oldest, @older, 300, false),
                (3024, 'still-future', 0, 1.25, 2.5, @created, @created, 'chat', true, @tomorrow, NULL, 400, false);
            INSERT INTO "ModelIdentifiers" ("Id", "ModelId", "IsEnabled", "Identifier", "ModelCostId", "IsPrimary") VALUES
                (3301, 903, true, @effectiveIdentifier, 3001, false),
                (3302, 903, true, @effectiveIdentifier, 3002, false),
                (3303, 903, true, @effectiveIdentifier, 3003, false),
                (3311, 903, true, @expiryIdentifier, 3011, false),
                (3312, 903, true, @expiryIdentifier, 3012, false),
                (3313, 903, true, @expiryIdentifier, 3013, false),
                (3321, 903, false, @effectiveIdentifier, 3021, false),
                (3322, 903, true, @effectiveIdentifier, 3022, false),
                (3323, 903, true, @effectiveIdentifier, 3023, false),
                (3324, 903, true, @effectiveIdentifier, 3024, false),
                (3331, 903, false, @expiryIdentifier, 3021, false),
                (3332, 903, true, @expiryIdentifier, 3022, false),
                (3333, 903, true, @expiryIdentifier, 3023, false),
                (3334, 903, true, @expiryIdentifier, 3024, false);
            """;
        command.Parameters.AddWithValue("created", new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc));
        command.Parameters.AddWithValue("boundary", boundary);
        command.Parameters.AddWithValue("yesterday", boundary.AddDays(-1));
        command.Parameters.AddWithValue("older", boundary.AddDays(-2));
        command.Parameters.AddWithValue("oldest", boundary.AddDays(-3));
        command.Parameters.AddWithValue("tomorrow", boundary.AddDays(1));
        command.Parameters.AddWithValue("effectiveIdentifier", EffectiveIdentifier);
        command.Parameters.AddWithValue("expiryIdentifier", ExpiryIdentifier);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AssertCost(ModelCostRuntimeRecord? cost, int id, int priority, DateTime effective, DateTime? expiry)
    {
        Require(cost is not null && cost.Id == id && cost.CostName == $"clock-{id}" && cost.IsActive && cost.Priority == priority &&
            cost.EffectiveDate == effective && cost.ExpiryDate == expiry && cost.InputCostPerMillionTokens == 1.25m &&
            cost.OutputCostPerMillionTokens == 2.5m, "exact eligible winner, priority/effective ranking and timestamp values");
    }

    private static async Task<ModelCostRuntimeRecord?> ReadBoundedAsync(NpgsqlModelProviderMappingRuntimeStore store,
        string identifier, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        var pending = store.GetModelCostForIdentifierAsync(identifier, bounded.Token);
        Exception? primary = null;
        ModelCostRuntimeRecord? result = null;
        try { result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) { primary = exception; }
        bounded.Cancel();
        var cleanup = new List<Exception>();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (pending.IsCanceled) { }
        catch (Exception exception) { if (!ReferenceEquals(exception, primary)) cleanup.Add(exception); }
        ThrowFailures(primary, cleanup, "fresh eligibility read");
        return result;
    }

    private static async Task AssertCancellationAndReuseAsync(NpgsqlDataSource source, NpgsqlModelProviderMappingRuntimeStore store,
        CancellationToken token)
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectCancellationAsync(store.GetModelCostForIdentifierAsync(EffectiveIdentifier, cancelled.Token), cancelled);
        }
        var held = await source.OpenConnectionAsync(token);
        using var caller = new CancellationTokenSource();
        Task? pending = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            pending = store.GetModelCostForIdentifierAsync(EffectiveIdentifier, caller.Token);
            Require(!pending.IsCompleted, "eligibility read waits behind the occupied sole slot");
            caller.Cancel();
            await ExpectCancellationAsync(pending, caller);
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            caller.Cancel();
            await DrainAsync();
            try { await held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) { cleanup.Add(exception); }
            await DrainAsync();
        }
        ThrowFailures(primary, cleanup, "cancelled eligibility pool wait");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(TimeSpan.FromSeconds(5));
        await using (var connection = await source.OpenConnectionAsync(bound.Token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1";
            Require(await command.ExecuteScalarAsync(bound.Token) is 1, "single slot reusable after caller cancellation");
        }
        Require((await ReadBoundedAsync(store, EffectiveIdentifier, token))?.Id == 3003 &&
            (await ReadBoundedAsync(store, ExpiryIdentifier, token))?.Id == 3012, "post-cancellation eligibility recovery");

        async Task DrainAsync()
        {
            if (pending is null) return;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (pending.IsCanceled) { }
            catch (Exception exception) { cleanup.Add(exception); }
        }
    }

    private static async Task ExpectCancellationAsync(Task pending, CancellationTokenSource caller)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException exception) when (caller.IsCancellationRequested)
        {
            Require(exception.CancellationToken == caller.Token, "exact caller cancellation token");
            return;
        }
        throw new InvalidOperationException("Native eligibility read ignored caller cancellation.");
    }

    private static void ThrowFailures(Exception? primary, List<Exception> cleanup, string operation)
    {
        if (primary is not null)
        {
            if (cleanup.Count != 0) throw new AggregateException($"Native {operation} and cleanup failed.", new[] { primary }.Concat(cleanup));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count != 0) throw new AggregateException($"Native {operation} cleanup failed.", cleanup);
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Native model eligibility clock contract failed: {contract}.");
    }
}
