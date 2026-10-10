using ConduitLLM.Persistence;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

public sealed partial class NpgsqlMediaRuntimeStore
{
    public async Task<MediaRuntimeQuotaSnapshot?> GetQuotaSnapshotAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH default_policy AS (
                SELECT "Id", "MaxStorageSizeBytes", "MaxFileCount", "QuotaExceededBehavior"
                FROM "MediaRetentionPolicies"
                WHERE "IsDefault" AND "IsActive"
                ORDER BY "Id" LIMIT 1
            ), usage AS (
                SELECT owner."VirtualKeyGroupId" AS "GroupId",
                    count(media."Id")::integer AS "TotalFiles",
                    COALESCE(sum(media."SizeBytes"), 0)::bigint AS "TotalSizeBytes"
                FROM "VirtualKeys" owner
                LEFT JOIN "MediaRecords" media ON media."VirtualKeyId" = owner."Id"
                GROUP BY owner."VirtualKeyGroupId"
            )
            SELECT target."VirtualKeyGroupId",
                COALESCE(usage."TotalSizeBytes", 0)::bigint,
                COALESCE(usage."TotalFiles", 0)::integer,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."MaxStorageSizeBytes" ELSE fallback."MaxStorageSizeBytes" END,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."MaxFileCount" ELSE fallback."MaxFileCount" END,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."QuotaExceededBehavior"
                    ELSE COALESCE(fallback."QuotaExceededBehavior", 0) END
            FROM "VirtualKeys" target
            JOIN "VirtualKeyGroups" groups ON groups."Id" = target."VirtualKeyGroupId"
            LEFT JOIN "MediaRetentionPolicies" assigned
                ON assigned."Id" = groups."MediaRetentionPolicyId" AND assigned."IsActive"
            LEFT JOIN default_policy fallback ON true
            LEFT JOIN usage ON usage."GroupId" = target."VirtualKeyGroupId"
            WHERE target."Id" = @virtualKeyId
            """;
        command.Parameters.AddWithValue("virtualKeyId", NpgsqlDbType.Integer, virtualKeyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new MediaRuntimeQuotaSnapshot(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetInt32(5));
    }

    public async Task<IReadOnlyList<MediaRuntimeGroupQuotaUsage>> GetGroupQuotaUsagesAsync(
        int? virtualKeyGroupId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH default_policy AS (
                SELECT "Id", "Name", "MaxStorageSizeBytes", "MaxFileCount",
                    "QuotaExceededBehavior", "RespectRecentAccess", "RecentAccessWindowDays"
                FROM "MediaRetentionPolicies"
                WHERE "IsDefault" AND "IsActive"
                ORDER BY "Id" LIMIT 1
            ), usage AS (
                SELECT owner."VirtualKeyGroupId" AS "GroupId",
                    count(media."Id")::integer AS "TotalFiles",
                    COALESCE(sum(media."SizeBytes"), 0)::bigint AS "TotalSizeBytes"
                FROM "VirtualKeys" owner
                LEFT JOIN "MediaRecords" media ON media."VirtualKeyId" = owner."Id"
                GROUP BY owner."VirtualKeyGroupId"
            )
            SELECT groups."Id", groups."GroupName",
                COALESCE(assigned."Id", fallback."Id"),
                COALESCE(assigned."Name", fallback."Name"),
                COALESCE(usage."TotalSizeBytes", 0)::bigint,
                COALESCE(usage."TotalFiles", 0)::integer,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."MaxStorageSizeBytes" ELSE fallback."MaxStorageSizeBytes" END,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."MaxFileCount" ELSE fallback."MaxFileCount" END,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."QuotaExceededBehavior"
                    ELSE COALESCE(fallback."QuotaExceededBehavior", 0) END,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."RespectRecentAccess"
                    ELSE COALESCE(fallback."RespectRecentAccess", true) END,
                CASE WHEN assigned."Id" IS NOT NULL
                    THEN assigned."RecentAccessWindowDays"
                    ELSE COALESCE(fallback."RecentAccessWindowDays", 7) END
            FROM "VirtualKeyGroups" groups
            LEFT JOIN "MediaRetentionPolicies" assigned
                ON assigned."Id" = groups."MediaRetentionPolicyId" AND assigned."IsActive"
            LEFT JOIN default_policy fallback ON true
            LEFT JOIN usage ON usage."GroupId" = groups."Id"
            WHERE @groupId IS NULL OR groups."Id" = @groupId
            ORDER BY groups."Id"
            """;
        AddNullableInt(command, "groupId", virtualKeyGroupId);
        var usages = new List<MediaRuntimeGroupQuotaUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            usages.Add(new MediaRuntimeGroupQuotaUsage(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetBoolean(9),
                reader.GetInt32(10)));
        }
        return usages;
    }
}
