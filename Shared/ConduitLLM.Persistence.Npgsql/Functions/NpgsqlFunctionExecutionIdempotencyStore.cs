using System.Globalization;

using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// PostgreSQL is the shared owner of synchronous execution claims and completed results.
/// No lease or cleanup can make a possibly dispatched provider request executable again.
/// </summary>
public sealed class NpgsqlFunctionExecutionIdempotencyStore : IFunctionExecutionIdempotencyStore
{
    private const string Columns = """
        "VirtualKeyId", "KeyHash", "Fingerprint", "ExecutionId", "BillingGroupId",
        "CreatedAtUtc", "State", "ResultJson", "BilledAtUtc"
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IVirtualKeyRuntimeStore _virtualKeys;

    public NpgsqlFunctionExecutionIdempotencyStore(
        NpgsqlDataSource dataSource,
        IVirtualKeyRuntimeStore virtualKeys)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _virtualKeys = virtualKeys ?? throw new ArgumentNullException(nameof(virtualKeys));
    }

    public async Task<FunctionExecutionClaim> ClaimAsync(
        int virtualKeyId, string keyHash, string fingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(keyHash, nameof(keyHash));
        ValidateHash(fingerprint, nameof(fingerprint));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            // INSERT and the losing SELECT deliberately use separate statement snapshots.
            // A concurrent winner may be invisible to the INSERT's original snapshot even
            // after its unique-key conflict has finished waiting for that winner to commit.
            command.CommandText = $"""
                INSERT INTO "FunctionExecutionClaims" ({Columns})
                SELECT @virtualKeyId, @keyHash, @fingerprint, @executionId,
                    key."VirtualKeyGroupId", @createdAt, 'claimed', NULL, NULL
                FROM "VirtualKeys" AS key
                WHERE key."Id" = @virtualKeyId
                ON CONFLICT ("VirtualKeyId", "KeyHash") DO NOTHING
                RETURNING {Columns}
                """;
            AddKey(command, virtualKeyId, keyHash);
            command.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Varchar, fingerprint);
            command.Parameters.AddWithValue("executionId", NpgsqlDbType.Uuid, Guid.NewGuid());
            command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                return ReadClaim(reader, isOwner: true);
        }

        return await FindAsync(connection, virtualKeyId, keyHash, cancellationToken)
            ?? throw new InvalidOperationException($"Virtual key {virtualKeyId} was not found when claiming execution.");
    }

    public async Task<FunctionExecutionClaim?> FindAsync(
        int virtualKeyId, string keyHash, CancellationToken cancellationToken = default)
    {
        ValidateHash(keyHash, nameof(keyHash));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await FindAsync(connection, virtualKeyId, keyHash, cancellationToken);
    }

    public Task<bool> MarkDispatchedAsync(
        FunctionExecutionClaim claim, CancellationToken cancellationToken = default) =>
        UpdateStateAsync(claim, """
            UPDATE "FunctionExecutionClaims" SET "State" = 'dispatching'
            WHERE "VirtualKeyId" = @virtualKeyId AND "KeyHash" = @keyHash
                AND "ExecutionId" = @executionId AND "Fingerprint" = @fingerprint
                AND "State" = 'claimed'
            """, resultJson: null, cancellationToken);

    public Task<bool> CompleteAsync(
        FunctionExecutionClaim claim, string resultJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        return UpdateStateAsync(claim, """
            UPDATE "FunctionExecutionClaims" SET "State" = 'completed', "ResultJson" = @resultJson
            WHERE "VirtualKeyId" = @virtualKeyId AND "KeyHash" = @keyHash
                AND "ExecutionId" = @executionId AND "Fingerprint" = @fingerprint
                AND "State" = 'dispatching'
            """, resultJson, cancellationToken);
    }

    public Task<bool> MarkUncertainAsync(
        FunctionExecutionClaim claim, CancellationToken cancellationToken = default) =>
        UpdateStateAsync(claim, """
            UPDATE "FunctionExecutionClaims" SET "State" = 'uncertain'
            WHERE "VirtualKeyId" = @virtualKeyId AND "KeyHash" = @keyHash
                AND "ExecutionId" = @executionId AND "Fingerprint" = @fingerprint
                AND "State" IN ('claimed', 'dispatching')
            """, resultJson: null, cancellationToken);

    public async Task SettleBillingAsync(
        FunctionExecutionClaim claim, decimal cost, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentOutOfRangeException.ThrowIfNegative(cost);
        // Read immutable billing ownership from the database instead of trusting a caller's
        // old record or a virtual key whose group may have changed since the original claim.
        var persisted = await FindAsync(claim.VirtualKeyId, claim.KeyHash, cancellationToken);
        if (persisted is null || persisted.ExecutionId != claim.ExecutionId ||
            !string.Equals(persisted.Fingerprint, claim.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The function execution claim does not match its persisted identity.");
        if (persisted.State != FunctionExecutionClaimState.Completed || persisted.ResultJson is null)
            throw new InvalidOperationException("Only a durably completed function execution can be billed.");
        if (persisted.BilledAtUtc is not null)
            return;

        // Match the batch billing unit precision without changing the provider's exact
        // saved cost. Applying this at settlement also recovers older completed claims
        // whose snapshots contain more precision than the ledger can represent.
        var billedCost = decimal.Round(cost, 8, MidpointRounding.AwayFromZero);
        if (billedCost > 0)
        {
            await _virtualKeys.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
                persisted.BillingGroupId,
                -billedCost,
                $"Function execution {persisted.ExecutionId:N}",
                "function-execution",
                VirtualKeyBalanceReferenceType.VirtualKey,
                persisted.VirtualKeyId.ToString(CultureInfo.InvariantCulture),
                $"function-execution:{persisted.ExecutionId:N}",
                new DateTime(persisted.CreatedAtUtc.Year, persisted.CreatedAtUtc.Month,
                    persisted.CreatedAtUtc.Day, persisted.CreatedAtUtc.Hour, 0, 0, DateTimeKind.Utc)),
                cancellationToken);
        }

        // The ledger is committed before this marker. If this write or its acknowledgement
        // fails, a replay safely retries the same ledger key before marking the claim billed.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "FunctionExecutionClaims" SET "BilledAtUtc" = @billedAt
            WHERE "VirtualKeyId" = @virtualKeyId AND "KeyHash" = @keyHash
                AND "ExecutionId" = @executionId AND "Fingerprint" = @fingerprint
                AND "State" = 'completed' AND "BilledAtUtc" IS NULL
            """;
        AddIdentity(command, persisted);
        command.Parameters.AddWithValue("billedAt", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> UpdateStateAsync(
        FunctionExecutionClaim claim, string sql, string? resultJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddIdentity(command, claim);
        if (resultJson is not null)
            command.Parameters.AddWithValue("resultJson", NpgsqlDbType.Jsonb, resultJson);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<FunctionExecutionClaim?> FindAsync(
        NpgsqlConnection connection, int virtualKeyId, string keyHash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM "FunctionExecutionClaims"
            WHERE "VirtualKeyId" = @virtualKeyId AND "KeyHash" = @keyHash
            """;
        AddKey(command, virtualKeyId, keyHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadClaim(reader, isOwner: false) : null;
    }

    private static FunctionExecutionClaim ReadClaim(NpgsqlDataReader reader, bool isOwner) => new(
        reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
        reader.GetInt32(4), reader.GetDateTime(5), reader.GetString(6) switch
        {
            "claimed" => FunctionExecutionClaimState.Claimed,
            "dispatching" => FunctionExecutionClaimState.Dispatching,
            "completed" => FunctionExecutionClaimState.Completed,
            "uncertain" => FunctionExecutionClaimState.Uncertain,
            _ => throw new InvalidOperationException("Unknown persisted function execution claim state.")
        },
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetDateTime(8), isOwner);

    private static void AddIdentity(NpgsqlCommand command, FunctionExecutionClaim claim)
    {
        AddKey(command, claim.VirtualKeyId, claim.KeyHash);
        command.Parameters.AddWithValue("executionId", NpgsqlDbType.Uuid, claim.ExecutionId);
        command.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Varchar, claim.Fingerprint);
    }

    private static void AddKey(NpgsqlCommand command, int virtualKeyId, string keyHash)
    {
        command.Parameters.AddWithValue("virtualKeyId", NpgsqlDbType.Integer, virtualKeyId);
        command.Parameters.AddWithValue("keyHash", NpgsqlDbType.Varchar, keyHash);
    }

    private static void ValidateHash(string hash, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, parameterName);
        if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Expected a lowercase SHA-256 hexadecimal digest.", parameterName);
    }
}
