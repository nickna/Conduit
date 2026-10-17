using System.Data;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;
using NpgsqlTypes;

internal static class GlobalSettingMutationPersistenceProbe
{
    private static readonly DateTime Created = new(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly DateTime Updated = new(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly string[] Operations = ["create-default", "create-supplied", "update", "upsert-insert", "upsert-update"];
    private const string Table = "\"GlobalSettings\"";

    internal static async Task RunAsync(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        var schema = settings.SearchPath;
        Require(schema is not null && schema.StartsWith(PersistenceProbeLifecycle.SchemaPrefix, StringComparison.Ordinal) &&
            schema.Length == PersistenceProbeLifecycle.SchemaPrefix.Length + 32 &&
            Guid.TryParseExact(schema.AsSpan(PersistenceProbeLifecycle.SchemaPrefix.Length), "N", out _), "owned GUID schema");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None
        };
        ActivitySource.AddActivityListener(listener);
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            await ExecuteAsync(connectionString, $"""
                CREATE TABLE "{schema}"."NativeGlobalSettingAudit" (
                    backend integer NOT NULL, setting_id integer NOT NULL, created_at timestamptz NOT NULL,
                    updated_at timestamptz NOT NULL, transaction_at timestamptz NOT NULL,
                    statement_at timestamptz NOT NULL, observed_at timestamptz NOT NULL);
                CREATE FUNCTION "{schema}"."NativeGlobalSettingAuditWrite"() RETURNS trigger LANGUAGE plpgsql AS $audit$
                BEGIN
                    INSERT INTO "{schema}"."NativeGlobalSettingAudit" VALUES
                        (pg_backend_pid(), NEW."Id", NEW."CreatedAt", NEW."UpdatedAt",
                         transaction_timestamp(), statement_timestamp(), clock_timestamp());
                    RETURN NEW;
                END $audit$;
                CREATE TRIGGER "NativeGlobalSettingAuditWrite" AFTER INSERT OR UPDATE ON "{schema}"."GlobalSettings"
                    FOR EACH ROW EXECUTE FUNCTION "{schema}"."NativeGlobalSettingAuditWrite"();
                """, deadline.Token);
            foreach (var operation in Operations)
            {
                await SuccessfulAsync(connectionString, operation, deadline.Token);
                await PreCancelledAsync(connectionString, operation, deadline.Token);
                await PoolCancelledAsync(connectionString, operation, deadline.Token);
                await SqlCancelledAsync(connectionString, operation, 1, deadline.Token);
                if (operation.StartsWith("upsert", StringComparison.Ordinal))
                    await SqlCancelledAsync(connectionString, operation, 2, deadline.Token);
            }
            foreach (var operation in new[] { "create-default", "update" })
                foreach (var duplicate in new[] { false, true })
                    await FailedWriteAsync(connectionString, operation, duplicate, deadline.Token);
            await AbsentWinnerAsync(connectionString, deadline.Token);
            foreach (var mode in new[] { "null-description", "equal-description", "changed-value", "changed-description" })
                await ConcurrentFieldsAsync(connectionString, mode, deadline.Token);
            foreach (var replacement in new[] { false, true })
                await DeletedOriginalAsync(connectionString, replacement, deadline.Token);
            foreach (var inserted in new[] { false, true })
                await UpsertClockAsync(connectionString, inserted, deadline.Token);
            await IdentityContractsAsync(connectionString, deadline.Token);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            foreach (var sql in new[]
            {
                $"TRUNCATE \"{schema}\".\"GlobalSettings\" RESTART IDENTITY",
                $"DROP TRIGGER IF EXISTS \"NativeGlobalSettingAuditWrite\" ON \"{schema}\".\"GlobalSettings\"",
                $"DROP FUNCTION IF EXISTS \"{schema}\".\"NativeGlobalSettingAuditWrite\"()",
                $"DROP TABLE IF EXISTS \"{schema}\".\"NativeGlobalSettingAudit\""
            })
            {
                using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await AttemptAsync(() => ExecuteAsync(connectionString, sql, final.Token), cleanup);
            }
        }
        Rethrow(primary, cleanup, "fixture");
        Console.WriteLine("Global setting mutation persistence passed: success=5; preCancelled=5; occupiedPool=5; sqlWaits=7; failedWrites=4; races=7; clocks=2; caller fields, independent rows, original identity, client timestamp bindings and single-slot recovery; exact-clock no-delta branch source-only.");
        Console.WriteLine("Global setting identity persistence passed: success=6; preCancelled=8; occupiedPool=8; sqlWaits=8; failedWrites=10; sequenceCases=3; explicit and generated IDs, original caller identities, supplied and default CreatedAt including PostgreSQL -infinity, observed cancellation and single-slot recovery.");
    }

    private static async Task CaseAsync(string connectionString, Func<NpgsqlDataSource, Gate, NpgsqlGlobalSettingRepository, int, Task> work, CancellationToken token,
        params Target[] targets)
    {
        await SeedAsync(connectionString, token);
        var gate = new Gate(targets);
        NpgsqlDataSource? source = null;
        Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 1, MinPoolSize = 0, Multiplexing = false, Timeout = 20,
            CommandTimeout = 20, CancellationTimeout = 1000, ApplicationName = "native_global_mutation_" + Guid.NewGuid().ToString("N")
        }.ConnectionString);
        builder.UseLoggerFactory(gate).EnableParameterLogging(false);
        builder.ConfigureTracing(options => { options.EnablePhysicalOpenTracing(false); options.ConfigureCommandFilter(gate.BeforeSend); });
        source = builder.Build();
        var pid = await PingAsync(source, token);
        gate.Arm();
        await work(source, gate, new NpgsqlGlobalSettingRepository(source), pid);
        Require(await PingAsync(source, token) == pid, "same physical slot after case");
        }
        catch (Exception error) { primary = error; }
        finally
        {
            gate.ReleaseAll();
            if (source is not null) await AttemptAsync(() => source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), cleanup);
            try { gate.Dispose(); } catch (Exception error) { cleanup.Add(error); }
        }
        Rethrow(primary, cleanup, "owned source and gate");
    }

    private static async Task SuccessfulAsync(string cs, string operation, CancellationToken token)
    {
        await CaseAsync(cs, async (source, gate, repository, pid) =>
        {
            var entity = Entity(operation); var before = DateTime.UtcNow;
            Require(await InvokeAsync(repository, operation, entity, token), "successful mutation");
            var after = DateTime.UtcNow; var rows = await RowsAsync(cs, token);
            var row = rows.Single(row => row.Key == (operation == "upsert-insert" ? "inserted" : entity.Key));
            if (!operation.StartsWith("upsert", StringComparison.Ordinal))
            {
                AssertStamped(operation, entity, before, after);
                Require(row.Id == entity.Id && row.CreatedAt == Wire(entity.CreatedAt) && row.UpdatedAt == Wire(entity.UpdatedAt), "successful caller timestamps persisted exactly");
            }
            else
            {
                var write = gate.Entries.Single(entry => entry.Ordinal == 2);
                var stamp = DistinctStamp(write);
                Require(stamp >= before && stamp <= after && row.UpdatedAt == Wire(stamp), "upsert exact client parameter");
                Require(row.CreatedAt == (operation == "upsert-insert" ? Wire(stamp) : Created), "upsert created timestamp");
            }
            Require(row.Value == (operation.StartsWith("upsert", StringComparison.Ordinal) ? "requested" : "changed") &&
                row.Description == (operation == "upsert-update" ? "description" : null), "all successful values and description");
            var audit = await AuditAsync(cs, pid, token);
            Require(audit.Length == 1 && audit[0].Id == row.Id && audit[0].CreatedAt == row.CreatedAt && audit[0].UpdatedAt == row.UpdatedAt, "successful independent trigger readback");
        }, token);
    }

    private static async Task PreCancelledAsync(string cs, string operation, CancellationToken token)
    {
        await CaseAsync(cs, async (_, gate, repository, _) =>
        {
            var entity = Entity(operation); var snapshot = Snapshot(entity); var before = await RowsAsync(cs, token);
            using var caller = new CancellationTokenSource(); caller.Cancel();
            await CancelledAsync(InvokeAsync(repository, operation, entity, caller.Token), caller.Token);
            Require(Snapshot(entity) == snapshot && gate.Started == 0, "pre-cancel keeps all caller fields and starts no SQL");
            EqualRows(before, await RowsAsync(cs, token));
        }, token);
    }

    private static async Task PoolCancelledAsync(string cs, string operation, CancellationToken token)
    {
        await CaseAsync(cs, async (source, gate, repository, _) =>
        {
            var entity = Entity(operation); var beforeRows = await RowsAsync(cs, token);
            using var caller = new CancellationTokenSource(); NpgsqlConnection? held = null; Task? pending = null;
            Exception? primary = null; var failures = new List<Exception>();
            try
            {
                held = await source.OpenConnectionAsync(token); var before = DateTime.UtcNow;
                pending = InvokeAsync(repository, operation, entity, caller.Token); var after = DateTime.UtcNow;
                Require(!pending.IsCompleted && gate.Started == 0, "actual sole held lease before dispatch");
                if (!operation.StartsWith("upsert", StringComparison.Ordinal)) AssertStamped(operation, entity, before, after);
                var stamped = Snapshot(entity); caller.Cancel(); await CancelledAsync(pending, caller.Token);
                Require(Snapshot(entity) == stamped, "later pool cancellation retains mutation");
            }
            catch (Exception error) { primary = error; }
            finally
            {
                caller.Cancel(); await DrainAsync(pending, failures);
                if (held is not null) await AttemptAsync(() => held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
                await DrainAsync(pending, failures);
            }
            Rethrow(primary, failures, "occupied pool cancellation"); EqualRows(beforeRows, await RowsAsync(cs, token));
        }, token);
    }

    private static async Task SqlCancelledAsync(string cs, string operation, int ordinal, CancellationToken token)
    {
        var kind = ordinal == 1 && operation.StartsWith("upsert", StringComparison.Ordinal) ? "SELECT" :
            operation == "update" || operation == "upsert-update" ? "UPDATE" : "INSERT";
        await CaseAsync(cs, async (source, gate, repository, pid) =>
        {
            var entity = Entity(operation); var beforeRows = await RowsAsync(cs, token);
            using var caller = new CancellationTokenSource();
            NpgsqlConnection? blocker = null; NpgsqlTransaction? transaction = null; Task? pending = null;
            Exception? primary = null; var failures = new List<Exception>();
            try
            {
                blocker = await OpenUnpooledAsync(cs, "mutation_blocker", token); transaction = await blocker.BeginTransactionAsync(token);
                var before = DateTime.UtcNow;
                pending = Task.Run(() => InvokeAsync(repository, operation, entity, caller.Token), CancellationToken.None);
                var entry = await gate.AtAsync(ordinal, token);
                Require(entry.Ordinal == ordinal && entry.CompletedBefore == ordinal - 1, "target follows completed prior statement");
                await LockAsync(blocker, transaction, token); gate.Release(ordinal);
                await WaitLockAsync(cs, source, pid, blocker.ProcessID, kind == "SELECT" ? "AccessShareLock" : "RowExclusiveLock", pending, token);
                if (!operation.StartsWith("upsert", StringComparison.Ordinal)) AssertStamped(operation, entity, before, DateTime.UtcNow);
                var snapshot = Snapshot(entity); caller.Cancel(); await CancelledAsync(pending, caller.Token);
                Require(Snapshot(entity) == snapshot && await PingAsync(source, token) == pid, "cancel returns sole lease while table remains locked");
            }
            catch (Exception error) { primary = error; }
            finally
            {
                gate.ReleaseAll(); caller.Cancel(); await DrainAsync(pending, failures);
                if (transaction is not null) await AttemptAsync(() => transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)), failures);
                if (blocker is not null) await AttemptAsync(() => blocker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
                await DrainAsync(pending, failures);
                if (transaction is not null) await AttemptAsync(() => transaction.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
            }
            Rethrow(primary, failures, "observed SQL cancellation"); EqualRows(beforeRows, await RowsAsync(cs, token));
        }, token, new Target(ordinal, kind));
    }

    private static async Task FailedWriteAsync(string cs, string operation, bool duplicate, CancellationToken token)
    {
        await CaseAsync(cs, async (_, gate, repository, _) =>
        {
            var rows = await RowsAsync(cs, token); var entity = Entity(operation);
            entity.Key = duplicate ? "other" : new string('k', 101); var before = DateTime.UtcNow;
            try { await InvokeAsync(repository, operation, entity, token); throw new InvalidOperationException("Expected failed setting write."); }
            catch (PostgresException error) { Require(error.SqlState == (duplicate ? "23505" : "22001"), "actual failed write SQLSTATE"); }
            AssertStamped(operation, entity, before, DateTime.UtcNow);
            Require(entity.Id == (operation == "update" ? 100 : 0) && gate.Started == 1, "failed write retains original caller identity");
            EqualRows(rows, await RowsAsync(cs, token));
        }, token);
    }

    private static Task AbsentWinnerAsync(string cs, CancellationToken token) => RaceAsync(cs, true, async (_, _, _) =>
        await ExecuteAsync(cs, "INSERT INTO \"GlobalSettings\" VALUES (300, 'inserted', 'winner', 'winner description', @created, @updated)", token),
        async (repository, _, callerToken) =>
        {
            try { await repository.UpsertAsync("inserted", "loser", null, callerToken); throw new InvalidOperationException("Expected absent-row conflict."); }
            catch (PostgresException error) { Require(error.SqlState == "23505", "read-empty plain insert conflict"); }
            var row = (await RowsAsync(cs, token)).Single(row => row.Id == 300);
            Require(row == new Row(300, "inserted", "winner", "winner description", Created, Updated), "committed winner unchanged");
        }, token);

    private static Task ConcurrentFieldsAsync(string cs, string mode, CancellationToken token) => RaceAsync(cs, false,
        async (_, _, _) => await ExecuteAsync(cs, "UPDATE \"GlobalSettings\" SET \"Value\"='winner', \"Description\"='winner description', \"CreatedAt\"=@created - interval '1 day' WHERE \"Id\"=100", token),
        async (repository, gate, callerToken) =>
        {
            var value = mode == "changed-value" ? "requested" : "before";
            var description = mode == "null-description" ? null : mode == "changed-description" ? "requested description" : "description";
            Require(await repository.UpsertAsync("seed", value, description, callerToken), "existing upsert returns changed");
            var row = (await RowsAsync(cs, token)).Single(row => row.Id == 100);
            Require(row.Value == (mode == "changed-value" ? "requested" : "winner") &&
                row.Description == (mode == "changed-description" ? "requested description" : "winner description") && row.CreatedAt == Created.AddDays(-1),
                "only snapshot-different fields overwrite concurrent values");
            Require(row.UpdatedAt == Wire(DistinctStamp(gate.Entries.Single(entry => entry.Ordinal == 2))), "upsert exact timestamp parameter");
        }, token);

    private static Task DeletedOriginalAsync(string cs, bool replacement, CancellationToken token) => RaceAsync(cs, false,
        async (_, _, _) => await ExecuteAsync(cs, "DELETE FROM \"GlobalSettings\" WHERE \"Id\"=100;" +
            (replacement ? "INSERT INTO \"GlobalSettings\" VALUES (400, 'seed', 'replacement', 'replacement description', @created, @updated)" : ""), token),
        async (repository, _, callerToken) =>
        {
            try { await repository.UpsertAsync("seed", "requested", null, callerToken); throw new InvalidOperationException("Expected disappeared original ID."); }
            catch (DBConcurrencyException) { }
            var matching = (await RowsAsync(cs, token)).Where(row => row.Key == "seed").ToArray();
            Require(replacement ? matching.SequenceEqual([new Row(400, "seed", "replacement", "replacement description", Created, Updated)]) : matching.Length == 0,
                "original-ID miss never retargets replacement key");
        }, token);

    private static async Task RaceAsync(string cs, bool insert, Func<NpgsqlDataSource, Gate, int, Task> interleave,
        Func<NpgsqlGlobalSettingRepository, Gate, CancellationToken, Task> operation, CancellationToken token)
    {
        await CaseAsync(cs, async (source, gate, repository, pid) =>
        {
            using var caller = new CancellationTokenSource(); Task? pending = null; Exception? primary = null; var failures = new List<Exception>();
            try
            {
                pending = Task.Run(() => operation(repository, gate, caller.Token), CancellationToken.None);
                var entry = await gate.AtAsync(2, token);
                Require(entry.CompletedBefore == 1 && gate.Started == 2, "race after completed read and before sole write");
                await interleave(source, gate, pid); gate.Release(2); await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Require(gate.Started == 2, "race has no re-read or retry");
            }
            catch (Exception error) { primary = error; }
            finally { gate.ReleaseAll(); caller.Cancel(); await DrainAsync(pending, failures); }
            Rethrow(primary, failures, "controlled writer interleaving");
        }, token, new Target(2, insert ? "INSERT" : "UPDATE"));
    }

    private static async Task UpsertClockAsync(string cs, bool insert, CancellationToken token)
    {
        await CaseAsync(cs, async (source, gate, repository, pid) =>
        {
            NpgsqlConnection? blocker = null; NpgsqlTransaction? transaction = null; Task<bool>? pending = null;
            using var caller = new CancellationTokenSource(); Exception? primary = null; var failures = new List<Exception>();
            try
            {
                blocker = await OpenUnpooledAsync(cs, "clock_blocker", token); transaction = await blocker.BeginTransactionAsync(token);
                await LockAsync(blocker, transaction, token);
                pending = Task.Run(() => repository.UpsertAsync(insert ? "inserted" : "seed", "requested", null, caller.Token), CancellationToken.None);
                await gate.AtAsync(1, token); gate.Release(1);
                var readTransaction = await WaitLockAsync(cs, source, pid, blocker.ProcessID, "AccessShareLock", pending, token);
                var releaseBefore = DateTime.UtcNow;
                await transaction.RollbackAsync(token); await transaction.DisposeAsync(); transaction = null;
                var write = await gate.AtAsync(2, token); var stamp = DistinctStamp(write);
                Require(write.CompletedBefore == 1 && stamp >= releaseBefore && stamp <= write.ClientUtc, "stamp after blocked read completes");
                Require(await TransactionStartAsync(cs, source, pid, token) is null, "no transaction spans read and write");
                await ServerAfterAsync(cs, stamp.AddSeconds(1), token);
                gate.Release(2); Require(await pending.WaitAsync(TimeSpan.FromSeconds(5)), "clock upsert success");
                var audit = (await AuditAsync(cs, pid, token)).Single();
                Require(audit.UpdatedAt == Wire(stamp) && audit.TransactionAt > Wire(stamp) && audit.TransactionAt != readTransaction &&
                    audit.StatementAt >= audit.TransactionAt && audit.ObservedAt >= audit.StatementAt &&
                    audit.CreatedAt == (insert ? Wire(stamp) : Created), "exact client binding distinct from actual server transaction clock");
                Console.WriteLine($"Global setting upsert clock observed: inserted={insert}; boundUtc={stamp:O}; readTransactionUtc={readTransaction:O}; writeTransactionUtc={audit.TransactionAt:O}; releaseBeforeUtc={releaseBefore:O}.");
            }
            catch (Exception error) { primary = error; }
            finally
            {
                gate.ReleaseAll(); caller.Cancel(); await DrainAsync(pending, failures);
                if (transaction is not null) await AttemptAsync(() => transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)), failures);
                if (blocker is not null) await AttemptAsync(() => blocker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
                await DrainAsync(pending, failures);
                if (transaction is not null) await AttemptAsync(() => transaction.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), failures);
            }
            Rethrow(primary, failures, "upsert clock");
        }, token, new Target(1, "SELECT"), new Target(2, insert ? "INSERT" : "UPDATE"));
    }

    private static readonly IdentityInput[] IdentityInputs =
    [
        new("create-generated",true,0,default),new("create-positive",true,900,default),new("create-negative",true,-900,Created),
        new("update-zero-supplied",false,0,Created),new("update-zero-default",false,0,default),new("update-existing",false,100,Created),
        new("update-missing-positive",false,900,Created),new("update-missing-negative",false,-900,Created)
    ];

    private static async Task IdentityContractsAsync(string cs,CancellationToken token)
    {
        foreach(var input in IdentityInputs)
        {
            if(!input.Name.StartsWith("update-missing",StringComparison.Ordinal))await IdentitySuccessAsync(cs,input,token);
            await IdentityPreCancelledAsync(cs,input,token);
            await IdentityCancelledAsync(cs,input,false,token);
            await IdentityCancelledAsync(cs,input,true,token);
        }
        foreach(var failure in new[]
        {
            new IdentityFailure(IdentityInputs[6],"identity",null),new IdentityFailure(IdentityInputs[7],"identity",null),
            new IdentityFailure(new("duplicate-id",true,100,Created),"identity","23505"),
            new IdentityFailure(new("duplicate-key-explicit",true,900,Created),"seed","23505"),
            new IdentityFailure(new("duplicate-key-create-zero",true,0,default),"seed","23505"),
            new IdentityFailure(IdentityInputs[3],"seed","23505"),new IdentityFailure(IdentityInputs[4],"seed","23505"),
            new IdentityFailure(IdentityInputs[5],"other","23505"),
            new IdentityFailure(IdentityInputs[1],new string('k',101),"22001"),new IdentityFailure(IdentityInputs[4],new string('k',101),"22001")
        })await IdentityFailedAsync(cs,failure,token);
        // Explicit inserts do not advance the identity sequence. After explicit
        // key1, the first generated insert fails at1 and the next can recover at2.
        await CaseAsync(cs,async(source,gate,repository,_)=>
        {
            var initialSequence=await IdentitySequenceAsync(cs,token);Require(initialSequence==new IdentitySequence(1,false),"initial owned identity sequence state");
            var explicitOne=IdentityEntity(new("explicit-one",true,1,Created));
            Require(await repository.CreateAsync(explicitOne,token)==1,"explicit sequence collision seed");
            var explicitSequence=await IdentitySequenceAsync(cs,token);Require(explicitSequence==initialSequence,"explicit1 does not advance sequence");
            var generated=IdentityEntity(IdentityInputs[0]);generated.Key="generated-sequence";
            var before=await RowsAsync(cs,token);var lower=DateTime.UtcNow;
            try{await repository.CreateAsync(generated,token);throw new InvalidOperationException("Expected generated identity collision.");}
            catch(PostgresException error){Require(error.SqlState=="23505","actual generated primary-key collision");}
            Require(generated.Id==0,"generated collision retains zero caller ID");IdentityStamped(IdentityInputs[0],generated,lower,DateTime.UtcNow);
            var failedSequence=await IdentitySequenceAsync(cs,token);Require(failedSequence==new IdentitySequence(1,true),"failed generated insert consumes actual nextval");
            EqualRows(before,await RowsAsync(cs,token));
            var retrySnapshot=IdentitySnapshot(generated);var retryBefore=DateTime.UtcNow;
            Require(await repository.CreateAsync(generated,token)==2 && generated.Id==2,"sequence gap and same caller recover at next generated ID");
            IdentityStamped(IdentityInputs[0] with{CreatedAt=retrySnapshot.Fields.CreatedAt},generated,retryBefore,DateTime.UtcNow);
            Require(generated.Key==retrySnapshot.Fields.Key && generated.Value==retrySnapshot.Fields.Value && generated.Description==retrySnapshot.Fields.Description &&
                generated.CreatedAt.Kind==retrySnapshot.CreatedKind && generated.UpdatedAt.Kind==DateTimeKind.Utc,"successful sequence retry caller fields/kinds/ID");
            var retryRow=(await RowsAsync(cs,token)).Single(row=>row.Id==2);
            Require(retryRow==new Row(generated.Id,generated.Key,generated.Value,generated.Description,Wire(generated.CreatedAt),Wire(generated.UpdatedAt)) &&
                retryRow.CreatedAt.Kind==DateTimeKind.Utc && retryRow.UpdatedAt.Kind==DateTimeKind.Utc,"sequence retry complete independent six-field row and kinds");
            var retrySequence=await IdentitySequenceAsync(cs,token);Require(retrySequence==new IdentitySequence(2,true),"retry actual sequence state");
            Console.WriteLine($"Global setting identity sequence observed: case=collision; initialLast={initialSequence.LastValue}; initialCalled={initialSequence.IsCalled}; explicitLast={explicitSequence.LastValue}; explicitCalled={explicitSequence.IsCalled}; failedLast={failedSequence.LastValue}; failedCalled={failedSequence.IsCalled}; retryLast={retrySequence.LastValue}; retryCalled={retrySequence.IsCalled}; retryId={generated.Id}.");
        },token);
    }

    private static async Task IdentitySuccessAsync(string cs,IdentityInput input,CancellationToken token)
    {
        await CaseAsync(cs,async(source,gate,repository,pid)=>
        {
            gate.AllowDefaultCreatedAt=DefaultIdentityCreated(input);
            var initialSequence=input.Create && input.Id!=0?await IdentitySequenceAsync(cs,token):null;
            var entity=IdentityEntity(input);var before=DateTime.UtcNow;
            Require(await InvokeIdentityAsync(repository,input,entity,token),"successful identity branch");
            IdentityStamped(input,entity,before,DateTime.UtcNow);
            Require(input.Id!=0?entity.Id==input.Id:entity.Id==1,"explicit original or fresh generated ID");
            var rows=await RowsAsync(cs,token);var row=rows.Single(row=>row.Id==entity.Id);
            Require(rows.Length==(input.Id==100?2:3) && row.Key==entity.Key && row.Value==entity.Value && row.Description==entity.Description &&
                row.CreatedAt==Wire(entity.CreatedAt) && row.UpdatedAt==Wire(entity.UpdatedAt),"six independent identity fields");
            if(DefaultIdentityCreated(input))
            {
                Require(entity.CreatedAt==DateTime.MinValue && entity.CreatedAt.Kind==DateTimeKind.Unspecified &&
                    row.CreatedAt==DateTime.MinValue && row.CreatedAt.Kind==DateTimeKind.Unspecified,"default CreatedAt value AND Kind preserved");
                Require(await CreatedTextAsync(cs,entity.Id,token)=="-infinity","actual default PostgreSQL timestamp sentinel");
            }
            else Require(row.CreatedAt.Kind==DateTimeKind.Utc,"non-sentinel stored creation UTC");
            Require(row.UpdatedAt.Kind==DateTimeKind.Utc,"stored update UTC");
            var audit=(await AuditAsync(cs,pid,token)).Single();Require(audit.Id==entity.Id && audit.CreatedAt==row.CreatedAt && audit.UpdatedAt==row.UpdatedAt,"independent identity write audit");
            if(input.Create && input.Id!=0)
            {
                var initial=initialSequence??throw new InvalidOperationException("Missing explicit identity sequence observation.");
                Require(initial==new IdentitySequence(1,false),"initial positive/negative explicit sequence state");
                var explicitSequence=await IdentitySequenceAsync(cs,token);Require(explicitSequence==initial,"explicit positive/negative sequence remains uncalled");
                var generated=IdentityEntity(IdentityInputs[0]);generated.Key="generated-after-explicit";
                Require(await repository.CreateAsync(generated,token)==1 && generated.Id==1,"explicit positive/negative insert does not advance sequence");
                var generatedSequence=await IdentitySequenceAsync(cs,token);Require(generatedSequence==new IdentitySequence(1,true),"actual generated sequence after explicit ID");
                Console.WriteLine($"Global setting identity sequence observed: case={(input.Id>0?"positive":"negative")}; initialLast={initial.LastValue}; initialCalled={initial.IsCalled}; explicitLast={explicitSequence.LastValue}; explicitCalled={explicitSequence.IsCalled}; generatedLast={generatedSequence.LastValue}; generatedCalled={generatedSequence.IsCalled}; generatedId={generated.Id}.");
            }
        },token);
    }

    private static async Task IdentityPreCancelledAsync(string cs,IdentityInput input,CancellationToken token)
    {
        await CaseAsync(cs,async(_,gate,repository,_)=>
        {
            var entity=IdentityEntity(input);var snapshot=IdentitySnapshot(entity);var rows=await RowsAsync(cs,token);
            using var caller=new CancellationTokenSource();caller.Cancel();
            await CancelledAsync(InvokeIdentityAsync(repository,input,entity,caller.Token),caller.Token);
            Require(IdentitySnapshot(entity)==snapshot && gate.Started==0,"identity pre-cancel exact fields/kinds/ID and SQL0");
            EqualRows(rows,await RowsAsync(cs,token));
        },token);
    }

    private static async Task IdentityCancelledAsync(string cs,IdentityInput input,bool sql,CancellationToken token)
    {
        var kind=input.Create||input.Id==0?"INSERT":"UPDATE";
        await CaseAsync(cs,async(source,gate,repository,pid)=>
        {
            gate.AllowDefaultCreatedAt=DefaultIdentityCreated(input);
            var entity=IdentityEntity(input);var rows=await RowsAsync(cs,token);
            using var caller=new CancellationTokenSource();NpgsqlConnection? held=null,blocker=null;NpgsqlTransaction? transaction=null;Task? pending=null;
            Exception? primary=null;var failures=new List<Exception>();
            try
            {
                if(sql){blocker=await OpenUnpooledAsync(cs,"identity_blocker",token);transaction=await blocker.BeginTransactionAsync(token);}
                else held=await source.OpenConnectionAsync(token);
                var before=DateTime.UtcNow;
                pending=sql?Task.Run(()=>InvokeIdentityAsync(repository,input,entity,caller.Token),CancellationToken.None):InvokeIdentityAsync(repository,input,entity,caller.Token);
                if(sql)
                {
                    var entry=await gate.AtAsync(1,token);Require(entry.CompletedBefore==0,"first identity write phase");
                    await LockAsync(blocker!,transaction!,token);gate.Release(1);
                    await WaitLockAsync(cs,source,pid,blocker!.ProcessID,"RowExclusiveLock",pending,token);
                }
                else Require(!pending.IsCompleted && gate.Started==0,"identity waits behind actual sole lease");
                IdentityStamped(input,entity,before,DateTime.UtcNow);Require(entity.Id==input.Id,"pending identity still original");
                var admitted=IdentitySnapshot(entity);caller.Cancel();await CancelledAsync(pending,caller.Token);
                Require(IdentitySnapshot(entity)==admitted,"later cancellation retains exact admitted fields/kinds/original ID");
                if(sql)Require(await PingAsync(source,token)==pid,"identity SQL cancellation releases slot before unlock");
            }
            catch(Exception error){primary=error;}
            finally
            {
                gate.ReleaseAll();caller.Cancel();await DrainAsync(pending,failures);
                if(held is not null)await AttemptAsync(()=>held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),failures);
                if(transaction is not null)await AttemptAsync(()=>transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)),failures);
                if(blocker is not null)await AttemptAsync(()=>blocker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),failures);
                await DrainAsync(pending,failures);
                if(transaction is not null)await AttemptAsync(()=>transaction.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),failures);
            }
            Rethrow(primary,failures,"identity cancellation");EqualRows(rows,await RowsAsync(cs,token));
        },token,sql ? new[] { new Target(1,kind) } : Array.Empty<Target>());
    }

    private static async Task IdentityFailedAsync(string cs,IdentityFailure failure,CancellationToken token)
    {
        await CaseAsync(cs,async(_,gate,repository,_)=>
        {
            var input=failure.Input;gate.AllowDefaultCreatedAt=DefaultIdentityCreated(input);
            var entity=IdentityEntity(input);entity.Key=failure.Key;var rows=await RowsAsync(cs,token);var before=DateTime.UtcNow;
            try{await InvokeIdentityAsync(repository,input,entity,token);throw new InvalidOperationException("Expected failed identity write.");}
            catch(DBConcurrencyException)when(failure.SqlState is null){}
            catch(PostgresException error)when(failure.SqlState is not null){Require(error.SqlState==failure.SqlState,"actual identity write SQLSTATE");}
            IdentityStamped(input,entity,before,DateTime.UtcNow);Require(entity.Id==input.Id && gate.Started==1,"failed write keeps original explicit or zero ID");
            EqualRows(rows,await RowsAsync(cs,token));
        },token);
    }

    private static GlobalSetting IdentityEntity(IdentityInput input)=>new(){Id=input.Id,Key="identity-"+input.Name,Value="identity value",Description=null,CreatedAt=input.CreatedAt,UpdatedAt=Updated};
    private static async Task<bool> InvokeIdentityAsync(NpgsqlGlobalSettingRepository repository,IdentityInput input,GlobalSetting entity,CancellationToken token)
    {if(!input.Create)return await repository.UpdateAsync(entity,token);var result=await repository.CreateAsync(entity,token);return result!=0 && result==entity.Id;}
    private static bool DefaultIdentityCreated(IdentityInput input)=>!input.Create && input.Id==0 && input.CreatedAt==default;
    private static IdentityCaller IdentitySnapshot(GlobalSetting entity)=>new(Snapshot(entity),entity.CreatedAt.Kind,entity.UpdatedAt.Kind);
    private static void IdentityStamped(IdentityInput input,GlobalSetting entity,DateTime before,DateTime after)
    {
        Require(entity.UpdatedAt.Kind==DateTimeKind.Utc && entity.UpdatedAt>=before && entity.UpdatedAt<=after,"identity admitted update UTC");
        if(input.Create && input.CreatedAt==default)Require(entity.CreatedAt==entity.UpdatedAt && entity.CreatedAt.Kind==DateTimeKind.Utc,"only Create applies unset creation fallback");
        else Require(entity.CreatedAt==input.CreatedAt && entity.CreatedAt.Kind==input.CreatedAt.Kind,"Update preserves supplied/default CreatedAt value and Kind");
    }
    private static async Task<string> CreatedTextAsync(string cs,int id,CancellationToken token)
    {
        await using var connection=await OpenUnpooledAsync(cs,"native_identity_text",token);await using var command=connection.CreateCommand();
        command.CommandText="SELECT \"CreatedAt\"::text FROM \"GlobalSettings\" WHERE \"Id\"=@id";command.Parameters.AddWithValue("id",id);
        return (string)(await command.ExecuteScalarAsync(token)??throw new InvalidOperationException("Missing identity timestamp row."));
    }
    private static async Task<IdentitySequence> IdentitySequenceAsync(string cs,CancellationToken token)
    {
        await using var connection=await OpenUnpooledAsync(cs,"native_identity_sequence",token);await using var command=connection.CreateCommand();
        var schema=new NpgsqlConnectionStringBuilder(cs).SearchPath!;
        command.CommandText="SELECT n.nspname,c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE c.oid=pg_catalog.pg_get_serial_sequence(@table,'Id')::regclass AND c.relkind='S'";
        command.Parameters.AddWithValue("table",$"\"{schema}\".\"GlobalSettings\"");string sequence;
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
            Require(await reader.ReadAsync(token) && reader.GetString(0)==schema,"actual sequence belongs to owned schema");sequence=reader.GetString(1);
            Require(sequence=="GlobalSettings_Id_seq" && !await reader.ReadAsync(token),"exact single owned identity sequence");
        }
        command.Parameters.Clear();command.CommandText=$"SELECT last_value,is_called FROM \"{schema}\".\"{sequence}\"";
        await using(var reader=await command.ExecuteReaderAsync(token))
        {Require(await reader.ReadAsync(token),"actual sequence state row");var state=new IdentitySequence(reader.GetInt64(0),reader.GetBoolean(1));Require(!await reader.ReadAsync(token),"single sequence state");return state;}
    }
    private sealed record IdentityInput(string Name,bool Create,int Id,DateTime CreatedAt);
    private sealed record IdentityFailure(IdentityInput Input,string Key,string? SqlState);
    private sealed record IdentityCaller(Row Fields,DateTimeKind CreatedKind,DateTimeKind UpdatedKind);
    private sealed record IdentitySequence(long LastValue,bool IsCalled);

    private static GlobalSetting Entity(string operation) => new()
    {
        Id = operation == "update" ? 100 : 0, Key = operation == "update" || operation == "upsert-update" ? "seed" : "created",
        Value = "changed", Description = null, CreatedAt = operation == "create-default" ? default : Created, UpdatedAt = Updated
    };
    private static async Task<bool> InvokeAsync(NpgsqlGlobalSettingRepository repository, string operation, GlobalSetting entity, CancellationToken token) => operation switch
    {
        "create-default" or "create-supplied" => await repository.CreateAsync(entity, token) > 0,
        "update" => await repository.UpdateAsync(entity, token),
        "upsert-insert" => await repository.UpsertAsync("inserted", "requested", null, token),
        "upsert-update" => await repository.UpsertAsync("seed", "requested", null, token),
        _ => throw new InvalidOperationException("Unknown native setting operation.")
    };
    private static Row Snapshot(GlobalSetting entity) => new(entity.Id, entity.Key, entity.Value, entity.Description, entity.CreatedAt, entity.UpdatedAt);
    private static void AssertStamped(string operation, GlobalSetting entity, DateTime before, DateTime after)
    {
        Require(entity.UpdatedAt.Kind == DateTimeKind.Utc && entity.UpdatedAt >= before && entity.UpdatedAt <= after &&
            (operation == "create-default" ? entity.CreatedAt == entity.UpdatedAt : entity.CreatedAt == Created), "caller mutation boundary");
    }
    private static DateTime Wire(DateTime value) => new(value.Ticks / 10 * 10, DateTimeKind.Utc);
    private static DateTime DistinctStamp(Entry entry)
    {
        var values = entry.Timestamps.Distinct().ToArray();
        Require(values.Length == 1 && values[0].Kind == DateTimeKind.Utc, "one distinct bound UTC write timestamp"); return values[0];
    }
    private static async Task CancelledAsync(Task task, CancellationToken expected)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Expected caller cancellation."); }
        catch (OperationCanceledException error) { Require(task.IsCanceled && error.CancellationToken == expected, "actual canceled task and exact caller cancellation token"); }
    }
    private static async Task DrainAsync(Task? task, List<Exception> failures)
    { if (task is not null) try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) when (task.IsCanceled) { } catch (Exception error) { failures.Add(error); } }
    private static async Task AttemptAsync(Func<Task> action, List<Exception> failures)
    { try { await action(); } catch (Exception error) { failures.Add(error); } }
    private static void Rethrow(Exception? primary, List<Exception> cleanup, string stage)
    {
        if (primary is not null && cleanup.Count == 0) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary is not null) cleanup.Insert(0, primary);
        if (cleanup.Count > 0) throw new AggregateException("Native global-setting " + stage + " failed.", cleanup);
    }
    private static void Require(bool condition, string contract)
    { if (!condition) throw new InvalidOperationException("Native global-setting mutation contract failed: " + contract + "."); }
    private static void EqualRows(Row[] expected, Row[] actual) => Require(actual.SequenceEqual(expected), "independent full database state unchanged");
    private static async Task<int> PingAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1"; Require(await command.ExecuteScalarAsync(token) is 1, "SELECT1 slot recovery"); return connection.ProcessID;
    }
    private static async Task<NpgsqlConnection> OpenUnpooledAsync(string cs, string application, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
        { Pooling = false, ApplicationName = application, Timeout = 10, CommandTimeout = 15, CancellationTimeout = 1000 }.ConnectionString);
        try { await connection.OpenAsync(token); return connection; } catch { await connection.DisposeAsync(); throw; }
    }
    private static async Task ExecuteAsync(string cs, string sql, CancellationToken token)
    {
        await using var connection = await OpenUnpooledAsync(cs, "native_global_oracle", token); await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("@created", StringComparison.Ordinal)) command.Parameters.AddWithValue("created", Created);
        if (sql.Contains("@updated", StringComparison.Ordinal)) command.Parameters.AddWithValue("updated", Updated);
        await command.ExecuteNonQueryAsync(token);
    }
    private static Task SeedAsync(string cs, CancellationToken token) => ExecuteAsync(cs, """
        TRUNCATE "GlobalSettings" RESTART IDENTITY;
        INSERT INTO "GlobalSettings" VALUES
            (100,'seed','before','description',@created,@updated),(200,'other','other value','other description',@created,@updated);
        TRUNCATE "NativeGlobalSettingAudit";
        """, token);
    private static async Task<Row[]> RowsAsync(string cs, CancellationToken token)
    {
        await using var connection = await OpenUnpooledAsync(cs, "native_global_rows", token); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\",\"Key\",\"Value\",\"Description\",\"CreatedAt\",\"UpdatedAt\" FROM \"GlobalSettings\" ORDER BY \"Id\"";
        await using var reader = await command.ExecuteReaderAsync(token); var rows = new List<Row>();
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4), reader.GetDateTime(5)));
        return rows.ToArray();
    }
    private static async Task<Audit[]> AuditAsync(string cs, int pid, CancellationToken token)
    {
        await using var connection = await OpenUnpooledAsync(cs, "native_global_audit", token); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_id,created_at,updated_at,transaction_at,statement_at,observed_at FROM \"NativeGlobalSettingAudit\" WHERE backend=@pid ORDER BY observed_at";
        command.Parameters.AddWithValue("pid", pid); await using var reader = await command.ExecuteReaderAsync(token); var rows = new List<Audit>();
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetInt32(0),reader.GetDateTime(1),reader.GetDateTime(2),reader.GetDateTime(3),reader.GetDateTime(4),reader.GetDateTime(5)));
        return rows.ToArray();
    }
    private static async Task LockAsync(NpgsqlConnection blocker, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = blocker.CreateCommand(); command.Transaction = transaction; command.CommandText = "LOCK TABLE \"GlobalSettings\" IN ACCESS EXCLUSIVE MODE";
        await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<DateTime> WaitLockAsync(string cs, NpgsqlDataSource source, int pid, int blocker, string mode, Task pending, CancellationToken token)
    {
        await using var connection = await OpenUnpooledAsync(cs, "native_global_observer", token); await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT activity.xact_start FROM pg_catalog.pg_stat_activity activity
            JOIN pg_catalog.pg_locks waiting ON waiting.pid=activity.pid
            JOIN pg_catalog.pg_locks held ON held.relation=waiting.relation AND held.pid=@blocker
                AND held.granted AND held.mode='AccessExclusiveLock'
            WHERE activity.pid=@pid AND activity.application_name=@application AND activity.datname=current_database()
                AND activity.state='active' AND activity.wait_event_type='Lock' AND @blocker=ANY(pg_catalog.pg_blocking_pids(activity.pid))
                AND waiting.locktype='relation' AND NOT waiting.granted AND waiting.mode=@mode
                AND waiting.relation=pg_catalog.to_regclass(@relation)
            """;
        var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        command.Parameters.AddWithValue("pid",pid);command.Parameters.AddWithValue("blocker",blocker);command.Parameters.AddWithValue("mode",mode);
        command.Parameters.AddWithValue("application",settings.ApplicationName!);command.Parameters.AddWithValue("relation",$"\"{settings.SearchPath}\".\"GlobalSettings\"");
        while (true)
        {
            if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Setting operation ended before its exact SQL lock wait."); }
            if (await command.ExecuteScalarAsync(token) is DateTime start) return start;
            await Task.Delay(10, token);
        }
    }
    private static async Task<DateTime?> TransactionStartAsync(string cs, NpgsqlDataSource source, int pid, CancellationToken token)
    {
        await using var connection = await OpenUnpooledAsync(cs, "native_global_transaction", token); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT xact_start FROM pg_catalog.pg_stat_activity WHERE pid=@pid AND application_name=@application AND datname=current_database()";
        command.Parameters.AddWithValue("pid",pid);command.Parameters.AddWithValue("application",new NpgsqlConnectionStringBuilder(source.ConnectionString).ApplicationName!);
        await using var reader=await command.ExecuteReaderAsync(token);Require(await reader.ReadAsync(token),"exact warmed backend remains");
        return reader.IsDBNull(0) ? null : reader.GetDateTime(0);
    }
    private static async Task ServerAfterAsync(string cs, DateTime target, CancellationToken token)
    {
        using var local = CancellationTokenSource.CreateLinkedTokenSource(token);local.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection = await OpenUnpooledAsync(cs, "native_global_clock", local.Token);await using var command=connection.CreateCommand();command.CommandText="SELECT clock_timestamp()";
        while ((DateTime)(await command.ExecuteScalarAsync(local.Token))! < target) await Task.Delay(10,local.Token);
    }

    private sealed record Row(int Id,string Key,string Value,string? Description,DateTime CreatedAt,DateTime UpdatedAt);
    private sealed record Audit(int Id,DateTime CreatedAt,DateTime UpdatedAt,DateTime TransactionAt,DateTime StatementAt,DateTime ObservedAt);
    private sealed record Target(int Ordinal,string Kind);
    private sealed record Entry(int Ordinal,int CompletedBefore,DateTime ClientUtc,DateTime[] Timestamps);
    private sealed class Gate : ILoggerFactory, ILogger
    {
        private readonly Dictionary<int,(Target Target,ManualResetEventSlim Release,TaskCompletionSource<Entry> Entered)> _targets;
        private readonly List<Entry> _entries=[];private int _armed,_started,_completed;
        internal Gate(Target[] targets) => _targets=targets.ToDictionary(target=>target.Ordinal,target=>(target,new ManualResetEventSlim(false),new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously)));
        internal int Started=>Volatile.Read(ref _started);
        internal bool AllowDefaultCreatedAt {get;set;}
        internal Entry[] Entries {get{lock(_entries)return _entries.ToArray();}}
        internal void Arm()=>Volatile.Write(ref _armed,1);
        internal Task<Entry> AtAsync(int ordinal,CancellationToken token)=>_targets[ordinal].Entered.Task.WaitAsync(token);
        internal void Release(int ordinal)=>_targets[ordinal].Release.Set();
        internal void ReleaseAll(){foreach(var item in _targets.Values)item.Release.Set();}
        internal bool BeforeSend(NpgsqlCommand command)
        {
            if(Volatile.Read(ref _armed)==0 || !command.CommandText.Contains(Table,StringComparison.Ordinal))return false;
            var ordinal=Interlocked.Increment(ref _started);
            // Inspect typed UTC scalar values directly in this owned command;
            // never enable parameter logging or retain arbitrary field values.
            var dateParameters=command.Parameters.Cast<NpgsqlParameter>().Where(parameter=>parameter.Value is DateTime).ToArray();
            Require(dateParameters.All(parameter=>parameter.NpgsqlDbType==NpgsqlDbType.TimestampTz &&
                (((DateTime)parameter.Value!).Kind==DateTimeKind.Utc || AllowDefaultCreatedAt && parameter.ParameterName=="createdAt" &&
                    (DateTime)parameter.Value! == DateTime.MinValue && ((DateTime)parameter.Value!).Kind==DateTimeKind.Unspecified)),
                "every DateTime is explicit UTC TimestampTz except armed identity createdAt default sentinel");
            var times=dateParameters.Select(parameter=>(DateTime)parameter.Value!).ToArray();
            var entry=new Entry(ordinal,Volatile.Read(ref _completed),DateTime.UtcNow,times);lock(_entries)_entries.Add(entry);
            if(_targets.TryGetValue(ordinal,out var gate))
            {
                Require(command.CommandText.TrimStart().StartsWith(gate.Target.Kind,StringComparison.OrdinalIgnoreCase),"exact intended before-send SQL kind");
                gate.Entered.TrySetResult(entry);
                if(!gate.Release.Wait(TimeSpan.FromSeconds(15)))throw new TimeoutException("Native setting before-send gate was not released.");
            }
            return false;
        }
        public ILogger CreateLogger(string categoryName)=>categoryName=="Npgsql.Command"?this:NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider){}
        public bool IsEnabled(LogLevel level)=>level>=LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {
            if(state is not IEnumerable<KeyValuePair<string,object?>> fields)return;var values=fields.ToArray();
            Require(!values.Any(field=>field.Key=="Parameters"),"never log parameter values");
            if(Volatile.Read(ref _armed)!=0 && eventId.Id==NpgsqlEventId.CommandExecutionCompleted &&
                values.FirstOrDefault(field=>field.Key=="CommandText").Value is string sql && sql.Contains(Table,StringComparison.Ordinal))Interlocked.Increment(ref _completed);
        }
        public void Dispose(){ReleaseAll();foreach(var item in _targets.Values)item.Release.Dispose();}
    }
}
