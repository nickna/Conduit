using System.Diagnostics;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class BillingAuditPersistenceParityTests
{
    [Theory]
    [InlineData(100, 1)]
    [InlineData(129, 2)]
    public async Task WriteBatchAsync_OrderedDefaultRows_BoundsClientExecutionsAndPreservesOrderAsync(
        int rowCount, int expectedExecutions)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var rows = Enumerable.Range(0, rowCount).Select(index => Populated() with
        {
            Id = Guid.Empty,
            Timestamp = default,
            RequestId = $"pipelined-{index:D3}",
            EventType = index - 50
        }).ToArray();
        var originals = rows.ToArray();
        var writer = database.Writer(native: true);
        var before = DateTime.UtcNow.AddSeconds(-1);
        using var executions = new BillingInsertClientExecutions();

        Assert.Empty(await writer.WriteBatchAsync([], database.Token));
        Assert.Equal(0, executions.Commands);
        var committed = await writer.WriteBatchAsync(rows, database.Token);

        // Npgsql traces one client execution for a batch. This asserts the
        // client API budget, not a physical packet or PostgreSQL statement count.
        Assert.Equal(expectedExecutions, executions.Commands);
        Assert.Equal(rowCount, executions.InsertStatements);
        Assert.Equal(originals, rows);
        Assert.Equal(rowCount, committed.Count);
        Assert.Equal(rowCount, committed.Select(row => row.Id).Distinct().Count());
        Assert.Single(committed.Select(row => row.Timestamp).Distinct());
        Assert.Equal((long)rowCount, await database.CountAsync());
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.NotEqual(Guid.Empty, committed[index].Id);
            Assert.InRange(committed[index].Timestamp, before, DateTime.UtcNow.AddSeconds(1));
            Assert.Equal(DateTimeKind.Utc, committed[index].Timestamp.Kind);
            Assert.Equal(rows[index] with
            {
                Id = committed[index].Id,
                Timestamp = committed[index].Timestamp
            }, committed[index]);
            await AssertRowAsync(database, committed[index]);
        }
    }

    [Fact]
    public async Task WriteBatchAsync_EmptyReturningRow_RollsBackTheWholeBatchAndPermitsReuseAsync()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        await using (var command = database.Source.CreateCommand("""
            CREATE FUNCTION omit_audit_returning_row() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RequestId" = 'omit-returning' THEN RETURN NULL; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER omit_audit_returning_row BEFORE INSERT ON "BillingAuditEvents"
                FOR EACH ROW EXECUTE FUNCTION omit_audit_returning_row();
            """))
        {
            await command.ExecuteNonQueryAsync(database.Token);
        }
        var rows = Enumerable.Range(0, 3).Select(index => Populated() with
        {
            Id = Guid.Empty,
            Timestamp = default,
            RequestId = index == 1 ? "omit-returning" : $"preserved-{index}"
        }).ToArray();
        var originals = rows.ToArray();
        var writer = database.Writer(native: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteBatchAsync(rows, database.Token));

        Assert.Equal("Billing audit insert did not return its generated values.", exception.Message);
        Assert.Equal(originals, rows);
        Assert.Equal(0L, await database.CountAsync());
        rows[1] = rows[1] with { RequestId = "restored-returning" };
        var committed = await writer.WriteBatchAsync(rows, database.Token);
        Assert.Equal(3L, await database.CountAsync());
        Assert.Equal(3, committed.Select(row => row.Id).Distinct().Count());
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.Equal(Guid.Empty, rows[index].Id);
            Assert.Equal(default, rows[index].Timestamp);
            Assert.Equal(rows[index] with
            {
                Id = committed[index].Id,
                Timestamp = committed[index].Timestamp
            }, committed[index]);
            await AssertRowAsync(database, committed[index]);
        }
    }

    private sealed class BillingInsertClientExecutions : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity _activity;
        private readonly ActivityTraceId _traceId = ActivityTraceId.CreateRandom();
        private int _commands;
        private int _insertStatements;

        public BillingInsertClientExecutions()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name == "Npgsql" && activity.TraceId == _traceId &&
                        activity.GetTagItem("db.query.text") is string sql)
                    {
                        var inserts = sql.Split("INSERT INTO \"BillingAuditEvents\"", StringSplitOptions.None).Length - 1;
                        if (inserts > 0)
                        {
                            Interlocked.Increment(ref _commands);
                            Interlocked.Add(ref _insertStatements, inserts);
                        }
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
            _activity = new Activity("billing audit client execution contract")
                .SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(_traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
                .Start();
        }

        public int Commands => Volatile.Read(ref _commands);
        public int InsertStatements => Volatile.Read(ref _insertStatements);

        public void Dispose()
        {
            _activity.Dispose();
            _listener.Dispose();
        }
    }
}
