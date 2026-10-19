using ConduitLLM.Persistence;

using Npgsql;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Executes a coalesced write, acknowledging IDs only after commit.</summary>
internal sealed class NpgsqlRequestLogBatchWriter(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlRequestLogRuntimeStore _single = new(dataSource);

    internal async Task<int[]> WriteAsync(RequestLogRuntimeRecord[] records, CancellationToken cancellationToken)
    {
        if (records.Length is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(records));
        foreach (var record in records) NpgsqlRequestLogRuntimeStore.Validate(record);
        if (records.Length == 1)
            return [await _single.WriteAsync(records[0], cancellationToken)];

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var commitStarted = false;
        try
        {
            var ids = new int[records.Length];
            await using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var record in records)
            {
                var command = new NpgsqlBatchCommand(NpgsqlRequestLogRuntimeStore.InsertSql);
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
                    if (await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("Request log insert returned more than one ID.");
                }
                if (await reader.NextResultAsync(cancellationToken))
                    throw new InvalidOperationException("Request log batch returned unexpected generated IDs.");
            }

            // No exception after this point is eligible for automatic isolation:
            // a failed acknowledgment can follow a successful COMMIT.
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
            return ids;
        }
        catch (Exception error) when (!commitStarted)
        {
            try
            {
                // Caller cancellation cannot prevent the confirmation required
                // before retrying rows from a rejected SQL batch.
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Request log rollback was not confirmed.", error, rollbackError);
            }

            if (error is PostgresException)
                throw new RequestLogBatchRolledBackException(error);
            // Cancellation, network/read errors and invalid RETURNING results
            // are not SQL-poison signals, even with a confirmed rollback.
            throw;
        }
    }
}

/// <summary>Only created after a SQL rejection and a confirmed explicit rollback.</summary>
internal sealed class RequestLogBatchRolledBackException(Exception cause)
    : Exception("Request log SQL batch was rejected and its rollback was confirmed.", cause);
