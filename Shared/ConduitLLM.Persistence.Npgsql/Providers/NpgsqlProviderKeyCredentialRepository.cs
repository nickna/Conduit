using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql implementation of provider-credential
/// persistence and primary selection.
/// </summary>
public sealed class NpgsqlProviderKeyCredentialRepository : IProviderKeyCredentialRepository
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlProviderKeyCredentialRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<(List<ProviderKeyCredential> Items, int TotalCount)> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePagination(page, pageSize);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var totalCount = await CountAsync(connection, whereClause: null, addParameters: null, cancellationToken);
        var credentials = await ReadCredentialsAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.CredentialColumns} FROM \"ProviderKeyCredentials\" " +
            "ORDER BY \"ProviderId\", \"IsPrimary\" DESC, \"ProviderAccountGroup\" " +
            "OFFSET @offset LIMIT @limit",
            command => AddPagination(command, page, pageSize),
            cancellationToken);
        await LoadProvidersAsync(connection, credentials, cancellationToken);
        return (credentials, totalCount);
    }

    /// <inheritdoc />
    public async Task<(List<ProviderKeyCredential> Items, int TotalCount)> GetByProviderIdPaginatedAsync(
        int providerId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (pageNumber, pageSize) = NormalizePagination(pageNumber, pageSize);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var totalCount = await CountAsync(
            connection,
            "WHERE \"ProviderId\" = @providerId",
            command => command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId),
            cancellationToken);
        var credentials = await ReadCredentialsAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.CredentialColumns} FROM \"ProviderKeyCredentials\" " +
            "WHERE \"ProviderId\" = @providerId " +
            "ORDER BY \"IsPrimary\" DESC, \"ProviderAccountGroup\" OFFSET @offset LIMIT @limit",
            command =>
            {
                command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId);
                AddPagination(command, pageNumber, pageSize);
            },
            cancellationToken);
        await LoadProvidersAsync(connection, credentials, cancellationToken);
        return (credentials, totalCount);
    }

    /// <inheritdoc />
    public async Task<ProviderKeyCredential?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var credentials = await ReadCredentialsAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.CredentialColumns} FROM \"ProviderKeyCredentials\" " +
            "WHERE \"Id\" = @id",
            command => command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id),
            cancellationToken);
        await LoadProvidersAsync(connection, credentials, cancellationToken);
        return credentials.Count == 0 ? null : credentials[0];
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        ProviderKeyCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return await ExecuteTransactionAsync(async (connection, transaction, ct) =>
        {
            await LockProviderAsync(connection, transaction, credential.ProviderId, ct);
            var now = DateTime.UtcNow;
            if (credential.CreatedAt == default)
            {
                credential.CreatedAt = now;
            }
            credential.UpdatedAt = now;

            if (credential.IsEnabled && credential.IsPrimary)
            {
                await DemotePrimariesAsync(
                    connection,
                    transaction,
                    credential.ProviderId,
                    exceptCredentialId: null,
                    now,
                    ct);
            }
            else if (credential.IsEnabled && !await HasEnabledCredentialAsync(
                connection,
                transaction,
                credential.ProviderId,
                exceptCredentialId: null,
                ct))
            {
                credential.IsPrimary = true;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO "ProviderKeyCredentials" (
                    "ProviderId", "ProviderAccountGroup", "ApiKey", "BaseUrl", "SecretSettings",
                    "KeyName", "IsPrimary", "IsEnabled", "CreatedAt", "UpdatedAt")
                VALUES (
                    @providerId, @providerAccountGroup, @apiKey, @baseUrl, @secretSettings,
                    @keyName, @isPrimary, @isEnabled, @createdAt, @updatedAt)
                RETURNING "Id"
                """;
            ProviderPersistenceMappings.AddCredentialParameters(
                command,
                credential,
                includeId: false,
                includeCreatedAt: true);
            credential.Id = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            return credential.Id;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        ProviderKeyCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return await ExecuteTransactionAsync(async (connection, transaction, ct) =>
        {
            var providerId = await FindProviderIdAsync(connection, transaction, credential.Id, ct);
            if (!providerId.HasValue)
            {
                return false;
            }

            await LockProviderAsync(connection, transaction, providerId.Value, ct);
            var existing = await ReadExistingStateAsync(connection, transaction, credential.Id, ct);
            if (!existing.HasValue)
            {
                return false;
            }

            credential.ProviderId = providerId.Value;
            credential.CreatedAt = existing.Value.CreatedAt;
            credential.UpdatedAt = DateTime.UtcNow;

            if (credential.IsPrimary && credential.IsEnabled)
            {
                await DemotePrimariesAsync(
                    connection,
                    transaction,
                    providerId.Value,
                    credential.Id,
                    credential.UpdatedAt,
                    ct);
            }
            else if (!existing.Value.IsEnabled &&
                credential.IsEnabled &&
                !credential.IsPrimary &&
                !await HasEnabledCredentialAsync(
                    connection,
                    transaction,
                    providerId.Value,
                    credential.Id,
                    ct))
            {
                credential.IsPrimary = true;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE "ProviderKeyCredentials"
                SET "ProviderAccountGroup" = @providerAccountGroup,
                    "ApiKey" = @apiKey,
                    "BaseUrl" = @baseUrl,
                    "SecretSettings" = @secretSettings,
                    "KeyName" = @keyName,
                    "IsPrimary" = @isPrimary,
                    "IsEnabled" = @isEnabled,
                    "UpdatedAt" = @updatedAt
                WHERE "Id" = @id
                """;
            ProviderPersistenceMappings.AddCredentialParameters(
                command,
                credential,
                includeId: true,
                includeCreatedAt: false);
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default) =>
        await ExecuteTransactionAsync(async (connection, transaction, ct) =>
        {
            var providerId = await FindProviderIdAsync(connection, transaction, id, ct);
            if (!providerId.HasValue)
            {
                return false;
            }

            await LockProviderAsync(connection, transaction, providerId.Value, ct);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM \"ProviderKeyCredentials\" WHERE \"Id\" = @id";
            command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<ProviderKeyCredential?> GetPrimaryKeyAsync(
        int providerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var credentials = await ReadCredentialsAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.CredentialColumns} FROM \"ProviderKeyCredentials\" " +
            "WHERE \"ProviderId\" = @providerId AND \"IsPrimary\" AND \"IsEnabled\" LIMIT 1",
            command => command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId),
            cancellationToken);
        return credentials.Count == 0 ? null : credentials[0];
    }

    /// <inheritdoc />
    public async Task<List<ProviderKeyCredential>> GetEnabledKeysByProviderIdAsync(
        int providerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadCredentialsAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.CredentialColumns} FROM \"ProviderKeyCredentials\" " +
            "WHERE \"ProviderId\" = @providerId AND \"IsEnabled\" " +
            "ORDER BY \"IsPrimary\" DESC, \"ProviderAccountGroup\"",
            command => command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SetPrimaryKeyAsync(
        int providerId,
        int keyId,
        CancellationToken cancellationToken = default) =>
        await ExecuteTransactionAsync(async (connection, transaction, ct) =>
        {
            await LockProviderAsync(connection, transaction, providerId, ct);
            await using (var target = connection.CreateCommand())
            {
                target.Transaction = transaction;
                target.CommandText = """
                    SELECT 1 FROM "ProviderKeyCredentials"
                    WHERE "Id" = @keyId AND "ProviderId" = @providerId
                    FOR UPDATE
                    """;
                target.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, keyId);
                target.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId);
                if (await target.ExecuteScalarAsync(ct) is null)
                {
                    return false;
                }
            }

            var now = DateTime.UtcNow;
            await DemotePrimariesAsync(
                connection,
                transaction,
                providerId,
                exceptCredentialId: null,
                now,
                ct);

            await using var promote = connection.CreateCommand();
            promote.Transaction = transaction;
            promote.CommandText = """
                UPDATE "ProviderKeyCredentials"
                SET "IsPrimary" = true, "UpdatedAt" = @updatedAt
                WHERE "Id" = @keyId
                """;
            promote.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, now);
            promote.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, keyId);
            return await promote.ExecuteNonQueryAsync(ct) > 0;
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HasKeyCredentialsAsync(
        int providerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM \"ProviderKeyCredentials\" WHERE \"ProviderId\" = @providerId)";
        command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task<int> CountByProviderIdAsync(
        int providerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await CountAsync(
            connection,
            "WHERE \"ProviderId\" = @providerId",
            command => command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId),
            cancellationToken);
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

    private static async Task LockProviderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int providerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM \"Providers\" WHERE \"Id\" = @providerId FOR UPDATE";
        command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<int?> FindProviderIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int credentialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT \"ProviderId\" FROM \"ProviderKeyCredentials\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, credentialId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt32(value);
    }

    private static async Task<(bool IsEnabled, DateTime CreatedAt)?> ReadExistingStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int credentialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "IsEnabled", "CreatedAt"
            FROM "ProviderKeyCredentials"
            WHERE "Id" = @id
            FOR UPDATE
            """;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, credentialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetBoolean(0), reader.GetDateTime(1))
            : null;
    }

    private static async Task DemotePrimariesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int providerId,
        int? exceptCredentialId,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE "ProviderKeyCredentials"
            SET "IsPrimary" = false, "UpdatedAt" = @updatedAt
            WHERE "ProviderId" = @providerId
              AND "IsPrimary"
              AND (@exceptId IS NULL OR "Id" <> @exceptId)
            """;
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, updatedAt);
        command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId);
        command.Parameters.Add(new NpgsqlParameter("exceptId", NpgsqlDbType.Integer)
        {
            Value = exceptCredentialId.HasValue ? exceptCredentialId.Value : DBNull.Value
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasEnabledCredentialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int providerId,
        int? exceptCredentialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM "ProviderKeyCredentials"
                WHERE "ProviderId" = @providerId
                  AND "IsEnabled"
                  AND (@exceptId IS NULL OR "Id" <> @exceptId))
            """;
        command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId);
        command.Parameters.Add(new NpgsqlParameter("exceptId", NpgsqlDbType.Integer)
        {
            Value = exceptCredentialId.HasValue ? exceptCredentialId.Value : DBNull.Value
        });
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<List<ProviderKeyCredential>> ReadCredentialsAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? addParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        addParameters?.Invoke(command);
        var credentials = new List<ProviderKeyCredential>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            credentials.Add(ProviderPersistenceMappings.ReadCredential(reader));
        }
        return credentials;
    }

    private static async Task LoadProvidersAsync(
        NpgsqlConnection connection,
        IReadOnlyList<ProviderKeyCredential> credentials,
        CancellationToken cancellationToken)
    {
        if (credentials.Count == 0)
        {
            return;
        }

        var providerIds = credentials.Select(credential => credential.ProviderId).Distinct().ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ProviderPersistenceMappings.ProviderColumns} FROM \"Providers\" " +
            "WHERE \"Id\" = ANY(@providerIds)";
        command.Parameters.AddWithValue(
            "providerIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer,
            providerIds);

        var providers = new Dictionary<int, Provider>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var provider = ProviderPersistenceMappings.ReadProvider(reader);
                providers.Add(provider.Id, provider);
            }
        }

        foreach (var credential in credentials)
        {
            var provider = providers[credential.ProviderId];
            credential.Provider = provider;
            provider.ProviderKeyCredentials.Add(credential);
        }
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection,
        string? whereClause,
        Action<NpgsqlCommand>? addParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"ProviderKeyCredentials\" {whereClause}";
        addParameters?.Invoke(command);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void AddPagination(NpgsqlCommand command, int page, int pageSize)
    {
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, (page - 1) * pageSize);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize);
    }

    private static (int Page, int PageSize) NormalizePagination(int page, int pageSize)
    {
        if (page < 1)
        {
            page = 1;
        }
        if (pageSize < 1)
        {
            pageSize = DefaultPageSize;
        }
        return (page, Math.Min(pageSize, MaxPageSize));
    }
}
