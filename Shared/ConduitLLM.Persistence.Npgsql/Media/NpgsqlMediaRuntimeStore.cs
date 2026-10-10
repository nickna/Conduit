using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql store for Gateway media ownership and quota flows.
/// </summary>
public sealed partial class NpgsqlMediaRuntimeStore : IMediaRuntimeStore
{
    private const string Columns = """
        "Id", "StorageKey", "VirtualKeyId", "MediaType", "ContentType",
        "SizeBytes", "ContentHash", "Provider", "Model", "Prompt", "StorageUrl",
        "PublicUrl", "ExpiresAt", "CreatedAt", "LastAccessedAt", "AccessCount",
        "DeletedAt"
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlMediaRuntimeStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<Guid> CreateAsync(
        MediaRuntimeRecord media,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ValidateMedia(media);
        if (media.Id == Guid.Empty)
            media.Id = Guid.NewGuid();
        if (media.CreatedAt == default)
            media.CreatedAt = DateTime.UtcNow;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "MediaRecords" (
                "Id", "StorageKey", "VirtualKeyId", "MediaType", "ContentType",
                "SizeBytes", "ContentHash", "Provider", "Model", "Prompt", "StorageUrl",
                "PublicUrl", "ExpiresAt", "CreatedAt", "LastAccessedAt", "AccessCount",
                "DeletedAt")
            VALUES (
                @id, @storageKey, @virtualKeyId, @mediaType, @contentType,
                @sizeBytes, @contentHash, @provider, @model, @prompt, @storageUrl,
                @publicUrl, @expiresAt, @createdAt, @lastAccessedAt, @accessCount,
                @deletedAt)
            ON CONFLICT ("StorageKey") DO NOTHING
            RETURNING "Id"
            """;
        AddRecordParameters(command, media);
        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        if (insertedId is Guid id)
            return id;

        var existing = await GetByStorageKeyAsync(
            media.StorageKey,
            includeDeleted: true,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Storage key '{media.StorageKey}' conflicted but could not be read.");
        if (existing.VirtualKeyId != media.VirtualKeyId)
        {
            throw new InvalidOperationException(
                $"Storage key '{media.StorageKey}' already belongs to a different virtual key.");
        }
        media.Id = existing.Id;
        media.CreatedAt = existing.CreatedAt;
        return existing.Id;
    }

    public async Task<MediaRuntimeRecord?> GetByStorageKeyAsync(
        string storageKey,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return null;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM "MediaRecords"
            WHERE "StorageKey" = @storageKey
                AND (@includeDeleted OR "DeletedAt" IS NULL)
            LIMIT 1
            """;
        command.Parameters.AddWithValue("storageKey", NpgsqlDbType.Varchar, storageKey);
        command.Parameters.AddWithValue("includeDeleted", NpgsqlDbType.Boolean, includeDeleted);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<bool> UpdateAccessStatsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            return false;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "MediaRecords"
            SET "AccessCount" = "AccessCount" + 1, "LastAccessedAt" = @now
            WHERE "Id" = @id
            """;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<MediaRuntimeRecord>> GetByVirtualKeyIdAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM "MediaRecords"
            WHERE "VirtualKeyId" = @virtualKeyId AND "DeletedAt" IS NULL
            ORDER BY "CreatedAt" DESC
            """;
        command.Parameters.AddWithValue("virtualKeyId", NpgsqlDbType.Integer, virtualKeyId);
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<MediaRuntimeStorageAggregate> GetAggregateStorageStatsAsync(
        int? virtualKeyGroupId = null,
        int virtualKeyLimit = 100,
        CancellationToken cancellationToken = default)
    {
        virtualKeyLimit = Math.Clamp(virtualKeyLimit, 1, 1000);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var filter = """
            m."DeletedAt" IS NULL AND (
                @groupId IS NULL OR EXISTS (
                    SELECT 1 FROM "VirtualKeys" k
                    WHERE k."Id" = m."VirtualKeyId"
                        AND k."VirtualKeyGroupId" = @groupId))
            """;
        int totalFiles;
        long totalSize;
        await using (var totals = connection.CreateCommand())
        {
            totals.CommandText = $"""
                SELECT count(*)::integer, COALESCE(sum(m."SizeBytes"), 0)::bigint
                FROM "MediaRecords" m WHERE {filter}
                """;
            AddNullableInt(totals, "groupId", virtualKeyGroupId);
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalFiles = reader.GetInt32(0);
            totalSize = reader.GetInt64(1);
        }

        var providers = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var providerCommand = connection.CreateCommand())
        {
            providerCommand.CommandText = $"""
                SELECT COALESCE(m."Provider", 'unknown'),
                    COALESCE(sum(m."SizeBytes"), 0)::bigint
                FROM "MediaRecords" m WHERE {filter}
                GROUP BY COALESCE(m."Provider", 'unknown')
                """;
            AddNullableInt(providerCommand, "groupId", virtualKeyGroupId);
            await using var reader = await providerCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                providers[reader.GetString(0)] = reader.GetInt64(1);
        }

        var mediaTypes = new List<MediaRuntimeTypeAggregate>();
        await using (var typeCommand = connection.CreateCommand())
        {
            typeCommand.CommandText = $"""
                SELECT m."MediaType", count(*)::integer,
                    COALESCE(sum(m."SizeBytes"), 0)::bigint
                FROM "MediaRecords" m WHERE {filter}
                GROUP BY m."MediaType" ORDER BY m."MediaType"
                """;
            AddNullableInt(typeCommand, "groupId", virtualKeyGroupId);
            await using var reader = await typeCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                mediaTypes.Add(new MediaRuntimeTypeAggregate(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        }

        var virtualKeys = new List<MediaRuntimeVirtualKeyAggregate>();
        await using (var keyCommand = connection.CreateCommand())
        {
            keyCommand.CommandText = $"""
                SELECT m."VirtualKeyId", COALESCE(sum(m."SizeBytes"), 0)::bigint
                FROM "MediaRecords" m WHERE {filter}
                GROUP BY m."VirtualKeyId"
                ORDER BY COALESCE(sum(m."SizeBytes"), 0) DESC, m."VirtualKeyId"
                LIMIT @limit
                """;
            AddNullableInt(keyCommand, "groupId", virtualKeyGroupId);
            keyCommand.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, virtualKeyLimit);
            await using var reader = await keyCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                virtualKeys.Add(new MediaRuntimeVirtualKeyAggregate(
                    reader.GetInt32(0), reader.GetInt64(1)));
        }

        return new MediaRuntimeStorageAggregate
        {
            TotalFiles = totalFiles,
            TotalSizeBytes = totalSize,
            ByProvider = providers,
            ByMediaType = mediaTypes,
            TopVirtualKeys = virtualKeys
        };
    }

    private static void ValidateMedia(MediaRuntimeRecord media)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(media.StorageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(media.MediaType);
        if (media.VirtualKeyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(media.VirtualKeyId));
    }

    private static void AddRecordParameters(NpgsqlCommand command, MediaRuntimeRecord media)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, media.Id);
        command.Parameters.AddWithValue("storageKey", NpgsqlDbType.Varchar, media.StorageKey);
        command.Parameters.AddWithValue("virtualKeyId", NpgsqlDbType.Integer, media.VirtualKeyId);
        command.Parameters.AddWithValue("mediaType", NpgsqlDbType.Varchar, media.MediaType);
        AddNullableText(command, "contentType", media.ContentType);
        AddNullableLong(command, "sizeBytes", media.SizeBytes);
        AddNullableText(command, "contentHash", media.ContentHash);
        AddNullableText(command, "provider", media.Provider);
        AddNullableText(command, "model", media.Model);
        AddNullableText(command, "prompt", media.Prompt);
        AddNullableText(command, "storageUrl", media.StorageUrl);
        AddNullableText(command, "publicUrl", media.PublicUrl);
        AddNullableTimestamp(command, "expiresAt", media.ExpiresAt);
        command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, media.CreatedAt);
        AddNullableTimestamp(command, "lastAccessedAt", media.LastAccessedAt);
        command.Parameters.AddWithValue("accessCount", NpgsqlDbType.Integer, media.AccessCount);
        AddNullableTimestamp(command, "deletedAt", media.DeletedAt);
    }

    private static async Task<MediaRuntimeRecord?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private static async Task<IReadOnlyList<MediaRuntimeRecord>> ReadManyAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<MediaRuntimeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(ReadRecord(reader));
        return records;
    }

    private static MediaRuntimeRecord ReadRecord(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        StorageKey = reader.GetString(1),
        VirtualKeyId = reader.GetInt32(2),
        MediaType = reader.GetString(3),
        ContentType = GetNullableString(reader, 4),
        SizeBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        ContentHash = GetNullableString(reader, 6),
        Provider = GetNullableString(reader, 7),
        Model = GetNullableString(reader, 8),
        Prompt = GetNullableString(reader, 9),
        StorageUrl = GetNullableString(reader, 10),
        PublicUrl = GetNullableString(reader, 11),
        ExpiresAt = GetNullableDateTime(reader, 12),
        CreatedAt = reader.GetDateTime(13),
        LastAccessedAt = GetNullableDateTime(reader, 14),
        AccessCount = reader.GetInt32(15),
        DeletedAt = GetNullableDateTime(reader, 16)
    };

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? GetNullableDateTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value is null ? DBNull.Value : value
        });

    private static void AddNullableLong(NpgsqlCommand command, string name, long? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTime? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static void AddNullableInt(NpgsqlCommand command, string name, int? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });
}
