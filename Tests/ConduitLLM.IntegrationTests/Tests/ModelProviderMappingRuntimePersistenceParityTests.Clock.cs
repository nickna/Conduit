using System.Diagnostics;

using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Persistence;

using Npgsql;
using NpgsqlTypes;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class ModelProviderMappingRuntimePersistenceParityTests
{
    [Theory]
    [InlineData(false, false, -1)]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, -1)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, -1)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, -1)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    public async Task ModelCostEligibilityPreservesExactWireBoundariesFiltersAndPriorityOrdering(
        bool typedNpgsql, bool expiryBoundary, int offsetMicroseconds)
    {
        await InSingleSlotSchemaAsync(async (schema, unusedSource, ids, token) =>
        {
            var candidates = await SeedClockCandidatesAsync(schema, ids, expiryBoundary, token, includeControls: true);
            var capture = new EligibilityBoundaryCapture(WithSearchPath(schema), schema,
                candidates.Identifier, candidates.CandidateId, expiryBoundary, offsetMicroseconds);
            ActivityListener? listener = null;
            NpgsqlDataSource? source = null;
            Task<ModelCostRuntimeRecord?>? pending = null;
            using var caller = new CancellationTokenSource();
            await IntegrationProofCleanup.RunAsync(async () =>
            {
                listener = new ActivityListener
                {
                    ShouldListenTo = activitySource => activitySource.Name == "Npgsql",
                    Sample = (ref ActivityCreationOptions<ActivityContext> sampling) => ActivitySamplingResult.None
                };
                ActivitySource.AddActivityListener(listener);
                var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
                {
                    ApplicationName = $"{schema}_exact", MinPoolSize = 0, MaxPoolSize = 1,
                    Timeout = 5, CommandTimeout = 30, CancellationTimeout = 1000
                }.ConnectionString);
                builder.ConfigureTracing(options =>
                {
                    options.EnablePhysicalOpenTracing(false);
                    options.ConfigureCommandFilter(capture.BeforeSend);
                });
                source = builder.Build();
                var store = CreateMatrixStore(source, typedNpgsql);
                pending = store.GetModelCostForIdentifierAsync(candidates.Identifier, caller.Token);
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                capture.Calls.Should().Be(1);
                capture.Completed.Should().Be(1, "a failed fixture callback must propagate instead of proving a predicate");
                var eligible = expiryBoundary ? offsetMicroseconds > 0 : offsetMicroseconds <= 0;
                result.Should().NotBeNull();
                result!.Id.Should().Be(eligible ? candidates.CandidateId : candidates.FallbackId);
                capture.StoredBoundary.Should().Be(capture.WireUtc.AddTicks(offsetMicroseconds * 10));
                _output.WriteLine($"clock-exact typed={typedNpgsql} expiry={expiryBoundary} offsetMicroseconds={offsetMicroseconds} " +
                    $"capturedUtc={capture.CapturedUtc:O} wireUtc={capture.WireUtc:O} storedBoundary={capture.StoredBoundary:O} " +
                    $"winner={result.Id} boundTimestampParameters={capture.TimestampParameterCount} callbackCompleted=1");
                await AssertSingleSlotReusableAsync(source, token);
            }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
                [async cleanup => { caller.Cancel(); if (pending is not null) await ReapSingleSlotReadAsync(pending, cleanup); },
                 async cleanup => { if (source is not null) await source.DisposeAsync().AsTask().WaitAsync(cleanup); },
                 cleanup => { listener?.Dispose(); return Task.CompletedTask; }],
                TimeSpan.FromSeconds(5), "Exact clock callback/source/listener owners could not all be drained."),
                "Exact eligibility boundary proof and cleanup failed.");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelCostEligibilityUsesInvocationUtcAcrossPoolWaits(bool expiryBoundary)
    {
        await InSingleSlotSchemaAsync(async (schema, source, ids, token) =>
        {
            var candidates = await SeedClockCandidatesAsync(schema, ids, expiryBoundary, token);
            var ef = CreateMatrixStore(source, typedNpgsql: false);
            var typed = CreateMatrixStore(source, typedNpgsql: true);
            NpgsqlConnection? held = null;
            Task<ModelCostRuntimeRecord?>? efRead = null;
            Task<ModelCostRuntimeRecord?>? typedRead = null;
            using var caller = new CancellationTokenSource();
            await IntegrationProofCleanup.RunAsync(async () =>
            {
                held = await source.OpenConnectionAsync(token);
                var heldPid = held.ProcessID;
                var boundary = UtcMicroseconds(DateTime.UtcNow.AddSeconds(6));
                await SetClockBoundaryAsync(schema, candidates.CandidateId, boundary, expiryBoundary, token);

                // Direct calls execute the synchronous portion through their first
                // incomplete await. The sole source slot is already owned here.
                var efBefore = DateTime.UtcNow;
                efRead = ef.GetModelCostForIdentifierAsync(candidates.Identifier, caller.Token);
                var efAfter = DateTime.UtcNow;
                var typedBefore = DateTime.UtcNow;
                typedRead = typed.GetModelCostForIdentifierAsync(candidates.Identifier, caller.Token);
                var typedAfter = DateTime.UtcNow;
                efRead.IsCompleted.Should().BeFalse();
                typedRead.IsCompleted.Should().BeFalse();
                efBefore.Should().BeOnOrBefore(efAfter);
                typedBefore.Should().BeOnOrBefore(typedAfter);
                efAfter.Should().BeBefore(boundary.AddSeconds(-3), "dispatch must retain a broad pre-boundary margin");
                typedAfter.Should().BeBefore(boundary.AddSeconds(-3), "a missed clock gate fails instead of retrying");
                _output.WriteLine($"clock-dispatch expiry={expiryBoundary} heldPid={heldPid} " +
                    $"boundary={boundary:O} efBefore={efBefore:O} efAfter={efAfter:O} " +
                    $"typedBefore={typedBefore:O} typedAfter={typedAfter:O} bothPending=true poolSize=1");

                await WaitForObservedUtcAsync(boundary.AddSeconds(1), token);
                var releaseBefore = DateTime.UtcNow;
                releaseBefore.Should().BeOnOrAfter(boundary.AddSeconds(1));
                await held.DisposeAsync();
                held = null;
                var releaseAfter = DateTime.UtcNow;
                await Task.WhenAll(efRead, typedRead).WaitAsync(TimeSpan.FromSeconds(5));
                var efResult = await efRead;
                var typedResult = await typedRead;
                var freshEf = await ef.GetModelCostForIdentifierAsync(candidates.Identifier, token);
                var freshTyped = await typed.GetModelCostForIdentifierAsync(candidates.Identifier, token);
                var entryWinner = expiryBoundary ? candidates.CandidateId : candidates.FallbackId;
                var laterWinner = expiryBoundary ? candidates.FallbackId : candidates.CandidateId;
                _output.WriteLine($"clock-results expiry={expiryBoundary} releaseBefore={releaseBefore:O} releaseAfter={releaseAfter:O} " +
                    $"entryWinner={entryWinner} laterWinner={laterWinner} efWinner={efResult?.Id} typedWinner={typedResult?.Id} " +
                    $"freshEfWinner={freshEf?.Id} freshTypedWinner={freshTyped?.Id}");
                freshEf.Should().NotBeNull();
                freshTyped.Should().NotBeNull();
                freshEf!.Id.Should().Be(laterWinner);
                freshTyped!.Id.Should().Be(laterWinner);
                efResult.Should().NotBeNull();
                typedResult.Should().NotBeNull();
                efResult!.Id.Should().Be(entryWinner);
                typedResult!.Id.Should().Be(entryWinner, "eligibility is sampled at invocation before waiting for a pool slot");
            }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
                [async cleanup => { caller.Cancel(); if (efRead is not null) await ReapSingleSlotReadAsync(efRead, cleanup); },
                 async cleanup => { caller.Cancel(); if (typedRead is not null) await ReapSingleSlotReadAsync(typedRead, cleanup); },
                 async cleanup => { if (held is not null) await held.DisposeAsync().AsTask().WaitAsync(cleanup); },
                 async cleanup => { if (efRead is not null) await ReapSingleSlotReadAsync(efRead, cleanup); },
                 async cleanup => { if (typedRead is not null) await ReapSingleSlotReadAsync(typedRead, cleanup); },
                 async cleanup =>
                    {
                        var pid = await AssertSingleSlotReusableAsync(source, cleanup);
                        _output.WriteLine($"clock-both-producers-joined single-slot-select1-recovery readerPid={pid}");
                    }], TimeSpan.FromSeconds(5), "Clock-boundary producers/held lease could not all be drained."),
                "Clock-boundary proof and producer cleanup failed.");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelCostEligibilityCancelsInSingleSlotPoolAndObservedSqlThenRecovers(bool typedNpgsql)
    {
        await InSingleSlotSchemaAsync(async (schema, source, ids, token) =>
        {
            var candidates = await SeedClockCandidatesAsync(schema, ids, expiryBoundary: false, token);
            var store = CreateMatrixStore(source, typedNpgsql);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.GetModelCostForIdentifierAsync(candidates.Identifier, cancelled.Token));
                Assert.Equal(cancelled.Token, cancellation.CancellationToken);
                foreach (var invalid in new[] { null, "", " \t\r\n" })
                    await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                        store.GetModelCostForIdentifierAsync(invalid!, cancelled.Token));
            }
            foreach (var poolWait in new[] { true, false })
                await CancelClockCostReadAsync(schema, source, store, candidates, poolWait, token);
            (await store.GetModelCostForIdentifierAsync("missing-clock", token)).Should().BeNull();
            await AssertSingleSlotReusableAsync(source, token);
        });
    }

    private async Task CancelClockCostReadAsync(string schema, NpgsqlDataSource source,
        ConduitLLM.Persistence.Interfaces.IModelProviderMappingRuntimeStore store,
        ClockCandidates candidates, bool poolWait, CancellationToken token)
    {
        var readerPid = await AssertSingleSlotReusableAsync(source, token);
        NpgsqlConnection? held = null;
        NpgsqlConnection? blocker = null;
        NpgsqlConnection? observer = null;
        NpgsqlTransaction? transaction = null;
        Task<ModelCostRuntimeRecord?>? pending = null;
        var released = false;
        using var caller = new CancellationTokenSource();
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            if (poolWait) held = await source.OpenConnectionAsync(token);
            else
            {
                blocker = await OpenSingleSlotObserverAsync(schema, "clock_blocker", token);
                observer = await OpenSingleSlotObserverAsync(schema, "clock_observer", token);
                transaction = await blocker.BeginTransactionAsync(token);
                await LockSingleSlotTableAsync(blocker, transaction, schema, "ModelCosts", token);
            }
            pending = store.GetModelCostForIdentifierAsync(candidates.Identifier, caller.Token);
            if (poolWait) pending.IsCompleted.Should().BeFalse("the exact sole source lease is held before dispatch");
            else await WaitForSingleSlotRelationWaitAsync(observer!, schema, "ModelCosts", readerPid,
                blocker!.ProcessID, pending, token);
            caller.Cancel();
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(caller.Token, cancellation.CancellationToken);
            if (held is not null) { await held.DisposeAsync(); held = null; }
            // In the SQL case this still runs while the exact table lock is held.
            await AssertSingleSlotReusableAsync(source, token);
            if (transaction is not null) { await transaction.RollbackAsync(token); released = true; }
            var recovered = await store.GetModelCostForIdentifierAsync(candidates.Identifier, token);
            recovered.Should().NotBeNull();
            recovered!.Id.Should().Be(candidates.FallbackId);
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
            [async cleanup => { caller.Cancel(); if (pending is not null) await ReapSingleSlotReadAsync(pending, cleanup); },
             async cleanup => { if (held is not null) await held.DisposeAsync().AsTask().WaitAsync(cleanup); },
             async cleanup => { if (transaction is not null && !released) await transaction.RollbackAsync(cleanup); },
             async cleanup => { if (blocker is not null) await blocker.DisposeAsync().AsTask().WaitAsync(cleanup); },
             async cleanup => { if (pending is not null) await ReapSingleSlotReadAsync(pending, cleanup); },
             async cleanup => { if (transaction is not null) await transaction.DisposeAsync().AsTask().WaitAsync(cleanup); },
             async cleanup => { if (observer is not null) await observer.DisposeAsync().AsTask().WaitAsync(cleanup); }],
            TimeSpan.FromSeconds(5), "Clock cancellation producers/lease/blocker could not all be drained."),
            "Clock eligibility cancellation and cleanup failed.");
    }

    private async Task<ClockCandidates> SeedClockCandidatesAsync(
        string schema, SeedIds ids, bool expiryBoundary, CancellationToken token, bool includeControls = false)
    {
        await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
        await using var context = new TestDbContextFactory(source).CreateDbContext();
        var now = DateTime.UtcNow;
        var identifier = expiryBoundary ? "clock-expiry" : "clock-effective";
        var fallback = NewSelectionCost("clock stable fallback", 1, now.AddDays(-3), null);
        var candidate = NewSelectionCost("clock crossing candidate", 10,
            expiryBoundary ? now.AddDays(-1) : now.AddDays(1), expiryBoundary ? now.AddDays(1) : null);
        context.ModelProviderTypeAssociations.AddRange(
            new ModelProviderTypeAssociation
            {
                ModelId = ids.ModelId, Identifier = identifier, Provider = (ProviderType)29000,
                ModelCost = fallback, IsEnabled = true
            },
            new ModelProviderTypeAssociation
            {
                ModelId = ids.ModelId, Identifier = identifier, Provider = (ProviderType)29001,
                ModelCost = candidate, IsEnabled = true
            });
        if (includeControls)
        {
            var older = NewSelectionCost("clock older same priority", 1, now.AddDays(-4), null);
            var lowerPriority = NewSelectionCost("clock newer lower priority", 0, now.AddHours(-1), null);
            var inactive = NewSelectionCost("clock inactive", 100, now.AddDays(-1), null);
            inactive.IsActive = false;
            var disabled = NewSelectionCost("clock disabled association", 101, now.AddDays(-1), null);
            var future = NewSelectionCost("clock future", 102, now.AddDays(1), null);
            var expired = NewSelectionCost("clock expired", 103, now.AddDays(-2), now.AddDays(-1));
            var wrongIdentifier = NewSelectionCost("clock wrong identifier", 104, now.AddDays(-1), null);
            var controls = new[] { older, lowerPriority, inactive, disabled, future, expired, wrongIdentifier };
            for (var index = 0; index < controls.Length; index++)
                context.ModelProviderTypeAssociations.Add(new ModelProviderTypeAssociation
                {
                    ModelId = ids.ModelId, Identifier = controls[index] == wrongIdentifier ? "clock-other" : identifier,
                    Provider = (ProviderType)(29002 + index), ModelCost = controls[index], IsEnabled = controls[index] != disabled
                });
        }
        await context.SaveChangesAsync(token);
        return new ClockCandidates(identifier, fallback.Id, candidate.Id);
    }

    private async Task SetClockBoundaryAsync(string schema, int costId, DateTime boundary,
        bool expiryBoundary, CancellationToken token)
    {
        await using var control = await OpenSingleSlotObserverAsync(schema, "clock_control", token);
        await using var command = control.CreateCommand();
        command.CommandText = $"UPDATE \"{schema}\".\"ModelCosts\" SET \"{(expiryBoundary ? "ExpiryDate" : "EffectiveDate")}\" = @boundary WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("boundary", NpgsqlDbType.TimestampTz, boundary);
        command.Parameters.AddWithValue("id", costId);
        (await command.ExecuteNonQueryAsync(token)).Should().Be(1);
    }

    private static DateTime UtcMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private static async Task WaitForObservedUtcAsync(DateTime target, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (DateTime.UtcNow < target)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(12))
                throw new TimeoutException("UTC eligibility boundary was not observed within the independent monotonic budget.");
            await Task.Delay(100, token);
        }
    }

    private sealed record ClockCandidates(string Identifier, int FallbackId, int CandidateId);

    // Npgsql10.0.3 invokes this public tracing filter after processing parameters
    // and before writing the command. It reads the real bound UTC parameter;
    // only the isolated fixture row changes on an independent unpooled session.
    private sealed class EligibilityBoundaryCapture(string connectionString, string schema,
        string identifier, int candidateId, bool expiryBoundary, int offsetMicroseconds)
    {
        private int _calls;
        public int Calls => _calls;
        public int Completed { get; private set; }
        public DateTime CapturedUtc { get; private set; }
        public DateTime WireUtc { get; private set; }
        public DateTime StoredBoundary { get; private set; }
        public int TimestampParameterCount { get; private set; }

        public bool BeforeSend(NpgsqlCommand command)
        {
            if (!command.Parameters.Cast<NpgsqlParameter>().Any(parameter => parameter.Value is string value && value == identifier))
                return false;
            Interlocked.Increment(ref _calls).Should().Be(1, "only the fixture's single target command may invoke this seam");
            // EF binds nullable/nonnullable uses separately; typed SQL reuses one
            // parameter. Both must carry one identical logical UTC snapshot.
            var parameters = command.Parameters.Cast<NpgsqlParameter>()
                .Where(parameter => parameter.Value is DateTime).ToArray();
            parameters.Should().NotBeEmpty();
            foreach (var parameter in parameters)
            {
                parameter.NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz);
                ((DateTime)parameter.Value!).Kind.Should().Be(DateTimeKind.Utc);
            }
            TimestampParameterCount = parameters.Length;
            CapturedUtc = Assert.Single(parameters.Select(parameter => (DateTime)parameter.Value!).Distinct());
            WireUtc = UtcMicroseconds(CapturedUtc);
            var boundary = WireUtc.AddTicks(offsetMicroseconds * 10);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var control = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, Timeout = 5, CommandTimeout = 5 }.ConnectionString);
            control.OpenAsync(deadline.Token).GetAwaiter().GetResult();
            using var update = control.CreateCommand();
            var column = expiryBoundary ? "ExpiryDate" : "EffectiveDate";
            update.CommandText = $"UPDATE \"{schema}\".\"ModelCosts\" SET \"{column}\" = @boundary WHERE \"Id\" = @id";
            update.Parameters.AddWithValue("boundary", NpgsqlDbType.TimestampTz, boundary);
            update.Parameters.AddWithValue("id", candidateId);
            update.ExecuteNonQueryAsync(deadline.Token).GetAwaiter().GetResult().Should().Be(1);
            using var readback = control.CreateCommand();
            readback.CommandText = $"SELECT \"{column}\" FROM \"{schema}\".\"ModelCosts\" WHERE \"Id\" = @id";
            readback.Parameters.AddWithValue("id", candidateId);
            StoredBoundary = (DateTime)readback.ExecuteScalarAsync(deadline.Token).GetAwaiter().GetResult()!;
            StoredBoundary.Should().Be(boundary);
            Completed++;
            return false; // Suppress tracing only; execute the unmodified query.
        }
    }
}
