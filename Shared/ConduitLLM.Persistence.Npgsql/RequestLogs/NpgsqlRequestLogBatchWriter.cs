using ConduitLLM.Persistence;

using Npgsql;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Executes a coalesced write, acknowledging IDs only after commit.</summary>
internal sealed class NpgsqlRequestLogBatchWriter(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlRequestLogRuntimeStore _single = new(dataSource);

    // Validate RETURNING on the server, before the implicit batch transaction can
    // commit. A trigger that suppresses a row must reject every insert in the
    // batch. Client-side cardinality validation alone would be too late.
    private const string BatchInsertSql = "WITH inserted AS (" +
        NpgsqlRequestLogRuntimeStore.InsertSql + """
        )
        SELECT min("Id"), 1 / ((count(*) = 1)::integer) FROM inserted
        """;

    internal async Task<int[]> WriteAsync(RequestLogRuntimeRecord[] records, CancellationToken cancellationToken)
    {
        if (records.Length is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(records));
        foreach (var record in records) NpgsqlRequestLogRuntimeStore.Validate(record);
        if (records.Length == 1)
            return [await _single.WriteAsync(records[0], cancellationToken)];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var ids = new int[records.Length];
            // One terminating Sync makes these commands one atomic implicit
            // transaction, including each server-side cardinality check. This
            // avoids a separate COMMIT round trip on the successful path.
            await using var batch = new NpgsqlBatch(connection) { EnableErrorBarriers = false };
            foreach (var record in records)
            {
                var command = new NpgsqlBatchCommand(BatchInsertSql) { AppendErrorBarrier = false };
                NpgsqlRequestLogRuntimeStore.AddParameters(command.Parameters, record);
                batch.BatchCommands.Add(command);
            }
            await using (var reader = await batch.ExecuteReaderAsync(cancellationToken))
            {
                for (var index = 0; index < records.Length; index++)
                {
                    if (index > 0 && !await reader.NextResultAsync(cancellationToken))
                        throw new InvalidOperationException("Request log batch did not return every generated ID.");
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("Request log insert did not return its generated ID.");
                    ids[index] = reader.GetInt32(0);
                    if (reader.GetInt32(1) != 1 || await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("Request log insert returned unexpected generated IDs.");
                }
                // Consume ReadyForQuery, including a deferred constraint failure
                // or lost commit acknowledgment, before acknowledging any ID.
                if (await reader.NextResultAsync(cancellationToken))
                    throw new InvalidOperationException("Request log batch returned unexpected generated IDs.");
            }
            return ids;
        }
        catch (PostgresException error)
        {
            try
            {
                // The received SQL rejection aborts this fixed, no-barrier
                // implicit transaction. Confirm recovery on the same physical
                // connection before allowing isolation; never infer rollback
                // from a transport error or from this ROLLBACK alone.
                await using var rollback = new NpgsqlCommand("ROLLBACK", connection);
                await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Request log rollback was not confirmed.", error, rollbackError);
            }

            // The guard and user triggers can both report division by zero.
            // Conservatively fail the entire batch without retrying either.
            if (error.SqlState == PostgresErrorCodes.DivisionByZero)
                throw new InvalidOperationException("Request log batch failed its server-side cardinality check or a division operation.", error);
            throw new RequestLogBatchRolledBackException(error);
        }
        // Cancellation, transport errors and unexpected client-side result
        // shapes may follow a commit. They never authorize automatic retries.
    }
}

/// <summary>Only created after a SQL rejection of an atomic batch and confirmed recovery.</summary>
internal sealed class RequestLogBatchRolledBackException(Exception cause)
    : Exception("Request log SQL batch was rejected and its rollback was confirmed.", cause);
