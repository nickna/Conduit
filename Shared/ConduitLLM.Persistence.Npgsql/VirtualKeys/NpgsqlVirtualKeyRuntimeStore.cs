using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql store for Gateway virtual-key requests.
/// </summary>
public sealed class NpgsqlVirtualKeyRuntimeStore : IVirtualKeyRuntimeStore
{
    private const string SelectRuntimeKey = """
        SELECT
            key."Id", key."KeyName", key."KeyHash", key."Description", key."IsEnabled",
            key."VirtualKeyGroupId", key."ExpiresAt", key."CreatedAt", key."UpdatedAt",
            key."Metadata", key."AllowedModels", key."RateLimitRpm", key."RateLimitRpd",
            key."RateLimitTpm", key."MaxParallelRequests", key."RateLimitPriority",
            key."ModelRateLimits", key."RowVersion",
            key_group."Id", key_group."ExternalGroupId", key_group."GroupName",
            key_group."Balance", key_group."LifetimeCreditsAdded", key_group."LifetimeSpent",
            key_group."CreatedAt", key_group."UpdatedAt", key_group."MediaRetentionPolicyId",
            key_group."RateLimitRpm", key_group."RateLimitRpd", key_group."RateLimitTpm",
            key_group."MaxParallelRequests", key_group."RowVersion"
        FROM "VirtualKeys" AS key
        INNER JOIN "VirtualKeyGroups" AS key_group
            ON key_group."Id" = key."VirtualKeyGroupId"
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlVirtualKeyRuntimeStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<VirtualKeyRuntimeRecord?> GetByHashAsync(
        string keyHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectRuntimeKey} WHERE key.\"KeyHash\" = @keyHash";
        command.Parameters.AddWithValue("keyHash", NpgsqlDbType.Varchar, keyHash);
        return await ReadSingleAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VirtualKeyRuntimeRecord?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectRuntimeKey} WHERE key.\"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        return await ReadSingleAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<VirtualKeyBalanceAdjustmentResult> AdjustBalanceAsync(
        VirtualKeyBalanceAdjustment adjustment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ValidateAdjustment(adjustment);

        try
        {
            return await ExecuteTransactionAsync(async (connection, transaction, token) =>
            {
                if (!await LockGroupAsync(
                    connection,
                    transaction,
                    adjustment.GroupId,
                    token))
                {
                    throw new InvalidOperationException(
                        $"Virtual key group {adjustment.GroupId} not found");
                }

                if (adjustment.IdempotencyKey is not null)
                {
                    var duplicate = await ReadLedgerByIdempotencyKeyAsync(
                        connection,
                        transaction,
                        adjustment.IdempotencyKey,
                        token);
                    if (duplicate is not null)
                    {
                        ValidateDuplicate(duplicate, adjustment);
                        return await ReadCurrentStateAsync(
                            connection,
                            transaction,
                            adjustment.GroupId,
                            applied: false,
                            token);
                    }
                }

                var state = await ApplyBalanceUpdateAsync(
                    connection,
                    transaction,
                    adjustment,
                    token);
                await InsertLedgerEntryAsync(
                    connection,
                    transaction,
                    adjustment,
                    state.NewBalance,
                    token);
                return state;
            }, cancellationToken);
        }
        catch (PostgresException exception) when (
            adjustment.IdempotencyKey is not null &&
            exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            exception.ConstraintName?.Contains(
                "IdempotencyKey",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            var winner = await ReadLedgerByIdempotencyKeyAsync(
                connection,
                transaction: null,
                adjustment.IdempotencyKey,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The winning idempotency ledger row could not be read.");
            ValidateDuplicate(winner, adjustment);
            return await ReadCurrentStateAsync(
                connection,
                transaction: null,
                adjustment.GroupId,
                applied: false,
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetKeyHashesByGroupIdAsync(
        int groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "KeyHash"
            FROM "VirtualKeys"
            WHERE "VirtualKeyGroupId" = @groupId
            ORDER BY "Id"
            """;
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, groupId);

        var hashes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hashes.Add(reader.GetString(0));
        }
        return hashes;
    }

    /// <inheritdoc />
    public async Task<bool> TouchAsync(
        int id,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "VirtualKeys"
            SET "UpdatedAt" = @updatedAt
            WHERE "Id" = @id
            """;
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, updatedAt);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<TResult> ExecuteTransactionAsync<TResult>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await operation(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (PostgresException exception) when (
                attempt < 3 && exception.SqlState is "40001" or "40P01")
            {
                await transaction.RollbackAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private static async Task<bool> LockGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM \"VirtualKeyGroups\" WHERE \"Id\" = @groupId FOR UPDATE";
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, groupId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<VirtualKeyBalanceAdjustmentResult> ApplyBalanceUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VirtualKeyBalanceAdjustment adjustment,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE "VirtualKeyGroups"
            SET "Balance" = "Balance" + @amount,
                "LifetimeCreditsAdded" = "LifetimeCreditsAdded" + @creditAmount,
                "LifetimeSpent" = "LifetimeSpent" + @debitAmount,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @groupId
            RETURNING "Balance", "LifetimeSpent"
            """;
        AddAdjustmentAmounts(command, adjustment);
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, DateTime.UtcNow);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Virtual key group {adjustment.GroupId} not found");
        }
        return new VirtualKeyBalanceAdjustmentResult(
            reader.GetDecimal(0),
            reader.GetDecimal(1),
            Applied: true);
    }

    private static async Task InsertLedgerEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VirtualKeyBalanceAdjustment adjustment,
        decimal balanceAfter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "VirtualKeyGroupTransactions" (
                "VirtualKeyGroupId", "TransactionType", "Amount", "BalanceAfter",
                "ReferenceType", "ReferenceId", "Description", "InitiatedBy",
                "InitiatedByUserId", "IdempotencyKey", "BillingWindowStartUtc",
                "CreatedAt", "IsDeleted", "DeletedAt")
            VALUES (
                @groupId, @transactionType, @absoluteAmount, @balanceAfter,
                @referenceType, @referenceId, @description, @initiatedBy,
                NULL, @idempotencyKey, @billingWindowStartUtc,
                @createdAt, false, NULL)
            """;
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, adjustment.GroupId);
        command.Parameters.AddWithValue(
            "transactionType",
            NpgsqlDbType.Integer,
            adjustment.Amount > 0 ? 1 : 2);
        command.Parameters.AddWithValue(
            "absoluteAmount",
            NpgsqlDbType.Numeric,
            Math.Abs(adjustment.Amount));
        command.Parameters.AddWithValue("balanceAfter", NpgsqlDbType.Numeric, balanceAfter);
        command.Parameters.AddWithValue(
            "referenceType",
            NpgsqlDbType.Integer,
            (int)adjustment.ReferenceType);
        AddNullableText(command, "referenceId", adjustment.ReferenceId);
        AddNullableText(
            command,
            "description",
            adjustment.Description ??
                (adjustment.Amount > 0 ? "Credits added" : "Usage deducted"));
        command.Parameters.AddWithValue(
            "initiatedBy",
            NpgsqlDbType.Varchar,
            adjustment.InitiatedBy ?? "System");
        AddNullableText(command, "idempotencyKey", adjustment.IdempotencyKey);
        AddNullableTimestamp(command, "billingWindowStartUtc", adjustment.BillingWindowStartUtc);
        command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<VirtualKeyBalanceAdjustmentResult> ReadCurrentStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int groupId,
        bool applied,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Balance", "LifetimeSpent"
            FROM "VirtualKeyGroups"
            WHERE "Id" = @groupId
            """;
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new VirtualKeyBalanceAdjustmentResult(
                reader.GetDecimal(0),
                reader.GetDecimal(1),
                applied)
            : throw new InvalidOperationException($"Virtual key group {groupId} not found");
    }

    private static async Task<LedgerIdentity?> ReadLedgerByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "VirtualKeyGroupId", "TransactionType", "Amount", "ReferenceType",
                   "ReferenceId", "BillingWindowStartUtc"
            FROM "VirtualKeyGroupTransactions"
            WHERE "IdempotencyKey" = @idempotencyKey
            """;
        command.Parameters.AddWithValue("idempotencyKey", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LedgerIdentity(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetDecimal(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5))
            : null;
    }

    private static async Task<VirtualKeyRuntimeRecord?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRuntimeKey(reader) : null;
    }

    private static VirtualKeyRuntimeRecord ReadRuntimeKey(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        KeyName = reader.GetString(1),
        KeyHash = reader.GetString(2),
        Description = GetNullableString(reader, 3),
        IsEnabled = reader.GetBoolean(4),
        VirtualKeyGroupId = reader.GetInt32(5),
        ExpiresAt = GetNullableDateTime(reader, 6),
        CreatedAt = reader.GetDateTime(7),
        UpdatedAt = reader.GetDateTime(8),
        Metadata = GetNullableString(reader, 9),
        AllowedModels = GetNullableString(reader, 10),
        RateLimitRpm = GetNullableInt32(reader, 11),
        RateLimitRpd = GetNullableInt32(reader, 12),
        RateLimitTpm = GetNullableInt32(reader, 13),
        MaxParallelRequests = GetNullableInt32(reader, 14),
        RateLimitPriority = GetNullableInt32(reader, 15),
        ModelRateLimits = GetNullableString(reader, 16),
        RowVersion = GetNullableBytes(reader, 17),
        Group = new VirtualKeyGroupRuntimeRecord
        {
            Id = reader.GetInt32(18),
            ExternalGroupId = GetNullableString(reader, 19),
            GroupName = reader.GetString(20),
            Balance = reader.GetDecimal(21),
            LifetimeCreditsAdded = reader.GetDecimal(22),
            LifetimeSpent = reader.GetDecimal(23),
            CreatedAt = reader.GetDateTime(24),
            UpdatedAt = reader.GetDateTime(25),
            MediaRetentionPolicyId = GetNullableInt32(reader, 26),
            RateLimitRpm = GetNullableInt32(reader, 27),
            RateLimitRpd = GetNullableInt32(reader, 28),
            RateLimitTpm = GetNullableInt32(reader, 29),
            MaxParallelRequests = GetNullableInt32(reader, 30),
            RowVersion = GetNullableBytes(reader, 31)
        }
    };

    private static void ValidateAdjustment(VirtualKeyBalanceAdjustment adjustment)
    {
        if (adjustment.GroupId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(adjustment), "Group ID must be positive.");
        }
        if (adjustment.IdempotencyKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(adjustment.IdempotencyKey);
        }
    }

    private static void ValidateDuplicate(
        LedgerIdentity existing,
        VirtualKeyBalanceAdjustment adjustment)
    {
        var expectedType = adjustment.Amount > 0 ? 1 : 2;
        if (existing.GroupId != adjustment.GroupId ||
            existing.TransactionType != expectedType ||
            existing.Amount != Math.Abs(adjustment.Amount) ||
            existing.ReferenceType != (int)adjustment.ReferenceType ||
            existing.ReferenceId != adjustment.ReferenceId ||
            existing.BillingWindowStartUtc != adjustment.BillingWindowStartUtc)
        {
            throw new VirtualKeyBalanceConflictException(
                $"Idempotency key '{adjustment.IdempotencyKey}' was reused with different balance-adjustment data.");
        }
    }

    private static void AddAdjustmentAmounts(
        NpgsqlCommand command,
        VirtualKeyBalanceAdjustment adjustment)
    {
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, adjustment.GroupId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, adjustment.Amount);
        command.Parameters.AddWithValue(
            "creditAmount",
            NpgsqlDbType.Numeric,
            adjustment.Amount > 0 ? adjustment.Amount : 0m);
        command.Parameters.AddWithValue(
            "debitAmount",
            NpgsqlDbType.Numeric,
            adjustment.Amount <= 0 ? Math.Abs(adjustment.Amount) : 0m);
    }

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar)
        {
            Value = value is null ? DBNull.Value : value
        });

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTime? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt32(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTime? GetNullableDateTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static byte[]? GetNullableBytes(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<byte[]>(ordinal);

    private sealed record LedgerIdentity(
        int GroupId,
        int TransactionType,
        decimal Amount,
        int ReferenceType,
        string? ReferenceId,
        DateTime? BillingWindowStartUtc);
}
