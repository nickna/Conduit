using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;

using Npgsql;
using NpgsqlTypes;

/// <summary>Owns the isolated PostgreSQL rows used by native function HTTP proofs.</summary>
internal static class NativeFunctionFixture
{
    internal const string ApiKey = "native-aot-function-key-32-bytes";

    internal static async Task<NativeFunctionFixtureSnapshot> CreateAsync(
        NpgsqlDataSource source, Guid owner, Uri providerUri, string protectedKey, CancellationToken ct)
    {
        var names = RequireOwner(source, owner);
        ArgumentNullException.ThrowIfNull(providerUri);
        if (!providerUri.IsAbsoluteUri || providerUri.Scheme != Uri.UriSchemeHttp || !providerUri.IsLoopback ||
            providerUri.AbsolutePath != "/" || providerUri.UserInfo.Length != 0 ||
            providerUri.Query.Length != 0 || providerUri.Fragment.Length != 0)
            throw new ArgumentException("The function provider must use a loopback HTTP base address.", nameof(providerUri));
        if (string.IsNullOrWhiteSpace(protectedKey) || !protectedKey.StartsWith("enc:v1:", StringComparison.Ordinal) ||
            protectedKey.Length <= 7 || protectedKey.Length > 2000 || protectedKey.Contains('\r') || protectedKey.Contains('\n'))
            throw new ArgumentException("A protected, storable function credential is required.", nameof(protectedKey));

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockOwnerAsync(connection, transaction, names, ct);
        if (await ReadOwnedAsync(connection, transaction, names, ct) is not null)
            throw new InvalidOperationException("The native function fixture owner already exists.");

        await using (var command = Command(connection, transaction, names, CreateSql))
        {
            command.Parameters.AddWithValue("providerUrl", NpgsqlDbType.Varchar,
                providerUri.GetLeftPart(UriPartial.Authority));
            command.Parameters.AddWithValue("protectedKey", NpgsqlDbType.Varchar, protectedKey);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Native function fixture creation returned no identifiers.");
            for (var index = 0; index < 7; index++)
                if (reader.GetInt32(index) <= 0)
                    throw new InvalidOperationException("Native function fixture identifiers must be positive.");
            if (await reader.ReadAsync(ct))
                throw new InvalidOperationException("Native function fixture creation returned ambiguous identifiers.");
        }

        var state = await ReadOwnedAsync(connection, transaction, names, ct)
            ?? throw new InvalidOperationException("Native function fixture creation was incomplete.");
        await transaction.CommitAsync(ct);
        return state.Snapshot;
    }

    internal static async Task<NativeFunctionFixtureSnapshot?> ReadAsync(
        NpgsqlDataSource source, Guid owner, CancellationToken ct)
    {
        var names = RequireOwner(source, owner);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockOwnerAsync(connection, transaction, names, ct);
        var state = await ReadOwnedAsync(connection, transaction, names, ct);
        await transaction.CommitAsync(ct);
        return state?.Snapshot;
    }

    /// <summary>Called after the fixture's hosts stop; deletes only a complete, verified owned graph.</summary>
    internal static async Task<NativeFunctionFixtureSnapshot?> DeleteAsync(
        NpgsqlDataSource source, Guid owner, CancellationToken ct)
    {
        var names = RequireOwner(source, owner);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockOwnerAsync(connection, transaction, names, ct);
        var state = await ReadOwnedAsync(connection, transaction, names, ct);
        if (state is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        await using (var command = Command(connection, transaction, names, DeleteDependentsSql))
        {
            AddIdentifiers(command, state);
            await command.ExecuteNonQueryAsync(ct);
        }
        foreach (var sql in DeleteParentsSql)
        {
            await using var command = Command(connection, transaction, names, sql);
            AddIdentifiers(command, state);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Native function fixture ownership changed during cleanup.");
        }
        if (await ReadOwnedAsync(connection, transaction, names, ct) is not null)
            throw new InvalidOperationException("Native function fixture cleanup left owned rows.");
        await transaction.CommitAsync(ct);
        return state.Snapshot;
    }

    private static Names RequireOwner(NpgsqlDataSource source, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (owner == Guid.Empty) throw new ArgumentException("A nonempty fixture owner GUID is required.", nameof(owner));
        return new Names(owner);
    }

    private static async Task LockOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Names names, CancellationToken ct)
    {
        // Serialize create/read/delete for the same owner, including before any row exists.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("NativeFunctionFixture:" + names.Stem));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@ownerLock)";
        command.Parameters.AddWithValue("ownerLock", NpgsqlDbType.Bigint, BinaryPrimitives.ReadInt64BigEndian(digest));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, Names names, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("stem", NpgsqlDbType.Varchar, names.Stem);
        command.Parameters.AddWithValue("groupName", NpgsqlDbType.Varchar, names.GroupName);
        command.Parameters.AddWithValue("keyName", NpgsqlDbType.Varchar, names.KeyName);
        command.Parameters.AddWithValue("keyHash", NpgsqlDbType.Varchar, names.VirtualKeyHash);
        command.Parameters.AddWithValue("configurationName", NpgsqlDbType.Varchar, names.ConfigurationName);
        command.Parameters.AddWithValue("costName", NpgsqlDbType.Varchar, names.CostName);
        command.Parameters.AddWithValue("credentialName", NpgsqlDbType.Varchar, names.CredentialName);
        command.Parameters.AddWithValue("ipName", NpgsqlDbType.Varchar, names.IpName);
        command.Parameters.AddWithValue("description", NpgsqlDbType.Varchar, names.Description);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        return command;
    }

    private static void AddIdentifiers(NpgsqlCommand command, OwnedState state)
    {
        var snapshot = state.Snapshot;
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, snapshot.GroupId);
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, snapshot.KeyId);
        command.Parameters.AddWithValue("configurationId", NpgsqlDbType.Integer, snapshot.ConfigurationId);
        command.Parameters.AddWithValue("costId", NpgsqlDbType.Integer, snapshot.CostId);
        command.Parameters.AddWithValue("credentialId", NpgsqlDbType.Integer, snapshot.CredentialId);
        command.Parameters.AddWithValue("mappingId", NpgsqlDbType.Integer, snapshot.MappingId);
        command.Parameters.AddWithValue("ipId", NpgsqlDbType.Integer, state.IpId);
    }

    private static async Task<OwnedState?> ReadOwnedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Names names, CancellationToken ct)
    {
        var rows = new Dictionary<string, OwnedRow>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction, names, ReadSql))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var kind = reader.GetString(0);
                var row = new OwnedRow(reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3));
                if (row.Id <= 0 || !reader.GetBoolean(4) || !rows.TryAdd(kind, row))
                    throw new InvalidOperationException("Native function fixture ownership is invalid or ambiguous.");
            }
        }
        if (rows.Count == 0) return null;
        if (rows.Count != 7 || !new[] { "group", "key", "configuration", "cost", "credential", "mapping", "ip" }.All(rows.ContainsKey))
            throw new InvalidOperationException("Native function fixture ownership is incomplete.");
        var group = rows["group"];
        var key = rows["key"];
        var configuration = rows["configuration"];
        var cost = rows["cost"];
        var credential = rows["credential"];
        var mapping = rows["mapping"];
        var ip = rows["ip"];
        if (key.FirstLink != group.Id || credential.FirstLink != configuration.Id ||
            mapping.FirstLink != configuration.Id || mapping.SecondLink != cost.Id || ip.FirstLink != key.Id)
            throw new InvalidOperationException("Native function fixture ownership links are invalid.");
        var state = new OwnedState(new NativeFunctionFixtureSnapshot(names.Owner, group.Id, key.Id, configuration.Id,
            cost.Id, credential.Id, mapping.Id, names.VirtualKey, names.VirtualKeyHash, names.ConfigurationName), ip.Id);
        await using (var command = Command(connection, transaction, names, ForeignReferencesSql))
        {
            AddIdentifiers(command, state);
            if (await command.ExecuteScalarAsync(ct) is not false)
                throw new InvalidOperationException("Native function fixture has dependencies with unrelated ownership.");
        }
        return state;
    }

    private sealed record OwnedRow(int Id, int? FirstLink, int? SecondLink);
    private sealed record OwnedState(NativeFunctionFixtureSnapshot Snapshot, int IpId);
    private sealed record Names(Guid Owner)
    {
        internal string Stem => $"native_function_{Owner:N}";
        internal string GroupName => $"Native function {Owner:N}";
        internal string KeyName => Stem + ":key";
        internal string ConfigurationName => Stem + ":exa";
        internal string CostName => Stem + ":cost";
        internal string CredentialName => Stem + ":credential";
        internal string IpName => Stem + ":ip";
        internal string Description => $"Native function fixture {Owner:N}";
        internal string VirtualKey => $"condt_native_function_{Owner:N}";
        internal string VirtualKeyHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(VirtualKey))).ToLowerInvariant();
    }

    private const string CreateSql = """
        WITH created_group AS (
            INSERT INTO "VirtualKeyGroups" ("ExternalGroupId", "GroupName", "Balance", "LifetimeCreditsAdded",
                "LifetimeSpent", "MediaRetentionPolicyId", "CreatedAt", "UpdatedAt")
            VALUES (@stem, @groupName, 100.0, 100.0, 0.0, NULL, @now, @now) RETURNING "Id"
        ), created_key AS (
            INSERT INTO "VirtualKeys" ("KeyName", "KeyHash", "Description", "IsEnabled", "VirtualKeyGroupId",
                "RateLimitRpm", "RateLimitRpd", "CreatedAt", "UpdatedAt")
            SELECT @keyName, @keyHash, @description, TRUE, "Id", 100, 1000, @now, @now FROM created_group RETURNING "Id"
        ), created_configuration AS (
            INSERT INTO "FunctionConfigurations" ("ProviderType", "ConfigurationName", "Purpose", "DefaultExecutionMode",
                "BaseUrl", "IsEnabled", "TimeoutSeconds", "CacheTtlMinutes", "MaxRetries", "ProviderSettings",
                "ParameterSchema", "Description", "CreatedAt", "UpdatedAt")
            VALUES (1, @configurationName, 1, 1, @providerUrl, TRUE, 30, NULL, 0, NULL,
                '{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}'::jsonb,
                @description, @now, @now) RETURNING "Id"
        ), created_credential AS (
            INSERT INTO "FunctionCredentials" ("ProviderType", "FunctionConfigurationId", "ApiKey", "BaseUrl",
                "Organization", "FunctionAccountGroup", "IsPrimary", "IsEnabled", "KeyName", "CreatedAt", "UpdatedAt")
            SELECT 1, "Id", @protectedKey, @providerUrl, NULL, 0, TRUE, TRUE, @credentialName, @now, @now
            FROM created_configuration RETURNING "Id"
        ), created_cost AS (
            INSERT INTO "FunctionCosts" ("CostName", "ProviderType", "Purpose", "Description", "BaseCost", "PricingModel",
                "CostPerExecution", "CostPerResult", "CostPerToken", "CostPerMinute", "TieredPricing", "PricingConfiguration",
                "IsActive", "EffectiveDate", "ExpiryDate", "Priority", "CreatedAt", "UpdatedAt")
            VALUES (@costName, 1, 1, @description, NULL, 1, 0.12500001, NULL, NULL, NULL, NULL, NULL,
                TRUE, @now - interval '1 day', NULL, 1, @now, @now) RETURNING "Id"
        ), created_mapping AS (
            INSERT INTO "FunctionCostMappings" ("FunctionConfigurationId", "FunctionCostId", "FunctionCostId1", "IsActive", "CreatedAt")
            SELECT configuration."Id", cost."Id", NULL, TRUE, @now
            FROM created_configuration configuration CROSS JOIN created_cost cost RETURNING "Id"
        ), created_ip AS (
            INSERT INTO "IpFilters" ("FilterType", "IpAddressOrCidr", "Name", "Description", "IsEnabled",
                "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "VirtualKeyId")
            SELECT 'whitelist', '127.0.0.1', @ipName, @description, TRUE, @now, @now, @stem, @stem, "Id"
            FROM created_key RETURNING "Id"
        )
        SELECT created_group."Id", created_key."Id", created_configuration."Id", created_cost."Id",
            created_credential."Id", created_mapping."Id", created_ip."Id"
        FROM created_group CROSS JOIN created_key CROSS JOIN created_configuration CROSS JOIN created_cost
            CROSS JOIN created_credential CROSS JOIN created_mapping CROSS JOIN created_ip
        """;

    private const string ReadSql = """
        WITH owned_groups AS (
            SELECT * FROM "VirtualKeyGroups" WHERE "ExternalGroupId" = @stem OR "GroupName" = @groupName FOR UPDATE
        ), owned_keys AS (
            SELECT * FROM "VirtualKeys" WHERE "KeyHash" = @keyHash OR "KeyName" = @keyName OR "Description" = @description
                OR "VirtualKeyGroupId" IN (SELECT "Id" FROM owned_groups) FOR UPDATE
        ), owned_configurations AS (
            SELECT * FROM "FunctionConfigurations" WHERE "ConfigurationName" = @configurationName OR "Description" = @description FOR UPDATE
        ), owned_costs AS (
            SELECT * FROM "FunctionCosts" WHERE "CostName" = @costName OR "Description" = @description FOR UPDATE
        ), owned_credentials AS (
            SELECT * FROM "FunctionCredentials" WHERE "KeyName" = @credentialName
                OR "FunctionConfigurationId" IN (SELECT "Id" FROM owned_configurations) FOR UPDATE
        ), owned_mappings AS (
            SELECT * FROM "FunctionCostMappings" WHERE "FunctionConfigurationId" IN (SELECT "Id" FROM owned_configurations)
                OR "FunctionCostId" IN (SELECT "Id" FROM owned_costs) OR "FunctionCostId1" IN (SELECT "Id" FROM owned_costs) FOR UPDATE
        ), owned_ip AS (
            SELECT * FROM "IpFilters" WHERE "CreatedBy" = @stem OR "UpdatedBy" = @stem OR "Name" = @ipName
                OR "VirtualKeyId" IN (SELECT "Id" FROM owned_keys) FOR UPDATE
        )
        SELECT 'group', "Id", NULL::integer, NULL::integer,
            COALESCE("ExternalGroupId" = @stem AND "GroupName" = @groupName, FALSE) FROM owned_groups
        UNION ALL SELECT 'key', "Id", "VirtualKeyGroupId", NULL,
            COALESCE("KeyHash" = @keyHash AND "KeyName" = @keyName AND "Description" = @description AND "IsEnabled"
                AND "RateLimitRpm" = 100 AND "RateLimitRpd" = 1000 AND "ExpiresAt" IS NULL, FALSE) FROM owned_keys
        UNION ALL SELECT 'configuration', "Id", NULL, NULL,
            COALESCE("ConfigurationName" = @configurationName AND "Description" = @description AND "ProviderType" = 1
                AND "Purpose" = 1 AND "DefaultExecutionMode" = 1 AND "IsEnabled" AND "TimeoutSeconds" = 30
                AND "BaseUrl" IS NOT NULL AND "MaxRetries" = 0 AND "CacheTtlMinutes" IS NULL AND "ProviderSettings" IS NULL
                AND "ParameterSchema" = '{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}'::jsonb,
                FALSE) FROM owned_configurations
        UNION ALL SELECT 'cost', "Id", NULL, NULL,
            COALESCE("CostName" = @costName AND "Description" = @description AND "ProviderType" = 1 AND "Purpose" = 1
                AND "PricingModel" = 1 AND "CostPerExecution" = 0.12500001 AND "IsActive" AND "EffectiveDate" <= @now
                AND "ExpiryDate" IS NULL AND "Priority" = 1 AND "BaseCost" IS NULL AND "CostPerResult" IS NULL
                AND "CostPerToken" IS NULL AND "CostPerMinute" IS NULL AND "TieredPricing" IS NULL
                AND "PricingConfiguration" IS NULL, FALSE) FROM owned_costs
        UNION ALL SELECT 'credential', "Id", "FunctionConfigurationId", NULL,
            COALESCE("KeyName" = @credentialName AND "ProviderType" = 1 AND "IsPrimary" AND "IsEnabled"
                AND "ApiKey" LIKE 'enc:v1:%' AND length("ApiKey") BETWEEN 8 AND 2000 AND "Organization" IS NULL
                AND "FunctionAccountGroup" = 0 AND "BaseUrl" =
                    (SELECT configuration."BaseUrl" FROM owned_configurations configuration
                        WHERE configuration."Id" = credential."FunctionConfigurationId"), FALSE)
            FROM owned_credentials credential
        UNION ALL SELECT 'mapping', "Id", "FunctionConfigurationId", "FunctionCostId",
            "IsActive" AND "FunctionCostId1" IS NULL FROM owned_mappings
        UNION ALL SELECT 'ip', "Id", "VirtualKeyId", NULL,
            COALESCE("CreatedBy" = @stem AND "UpdatedBy" = @stem AND "Name" = @ipName AND "Description" = @description
                AND "FilterType" = 'whitelist' AND "IpAddressOrCidr" = '127.0.0.1' AND "IsEnabled", FALSE) FROM owned_ip
        """;

    private const string ForeignReferencesSql = """
        WITH owned_executions AS (
            SELECT "Id" FROM "FunctionExecutions" WHERE "FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId
        )
        SELECT EXISTS (
            SELECT 1 FROM "FunctionExecutions" WHERE ("FunctionConfigurationId" = @configurationId OR "VirtualKeyId" = @keyId)
                AND NOT ("FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId)
        ) OR EXISTS (
            SELECT 1 FROM "FunctionCallAudits" WHERE
                ("FunctionConfigurationId" = @configurationId OR "VirtualKeyId" = @keyId OR "FunctionExecutionId" IN (SELECT "Id" FROM owned_executions))
                AND NOT ("FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId
                    AND ("FunctionExecutionId" IS NULL OR "FunctionExecutionId" IN (SELECT "Id" FROM owned_executions)))
        ) OR EXISTS (
            SELECT 1 FROM "FunctionExecutionAudits" WHERE
                ("FunctionExecutionId" IN (SELECT "Id" FROM owned_executions) OR "FunctionExecutionId1" IN (SELECT "Id" FROM owned_executions)
                    OR "VirtualKeyId" = @keyId)
                AND NOT ("FunctionExecutionId" IN (SELECT "Id" FROM owned_executions)
                    AND ("VirtualKeyId" IS NULL OR "VirtualKeyId" = @keyId)
                    AND ("FunctionExecutionId1" IS NULL OR "FunctionExecutionId1" IN (SELECT "Id" FROM owned_executions)))
        ) OR EXISTS (
            SELECT 1 FROM "BillingAuditEvents" WHERE ("VirtualKeyId" = @keyId OR "VirtualKeyGroupId" = @groupId)
                AND ("VirtualKeyId" IS NOT NULL AND "VirtualKeyId" <> @keyId OR "VirtualKeyGroupId" IS NOT NULL AND "VirtualKeyGroupId" <> @groupId)
        ) OR EXISTS (
            SELECT 1 FROM "AsyncTasks" WHERE "VirtualKeyId" = @keyId
        ) OR EXISTS (
            SELECT 1 FROM "MediaRecords" WHERE "VirtualKeyId" = @keyId
        ) OR EXISTS (
            SELECT 1 FROM "BatchOperationHistory" WHERE "VirtualKeyId" = @keyId
        ) OR EXISTS (
            SELECT 1 FROM "Notifications" WHERE "VirtualKeyId" = @keyId
        )
        """;

    private const string DeleteDependentsSql = """
        DELETE FROM "FunctionExecutionAudits" WHERE "FunctionExecutionId" IN (
            SELECT "Id" FROM "FunctionExecutions" WHERE "FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId);
        DELETE FROM "FunctionCallAudits" WHERE "FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId;
        DELETE FROM "FunctionExecutions" WHERE "FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId;
        DELETE FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId;
        DELETE FROM "BillingAuditEvents" WHERE "VirtualKeyId" = @keyId OR "VirtualKeyGroupId" = @groupId;
        DELETE FROM "PricingAuditEvents" WHERE "VirtualKeyId" = @keyId;
        DELETE FROM "VirtualKeySpendHistory" WHERE "VirtualKeyId" = @keyId;
        DELETE FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @groupId;
        """;

    private static readonly string[] DeleteParentsSql =
    [
        """DELETE FROM "IpFilters" WHERE "Id" = @ipId AND "VirtualKeyId" = @keyId AND "CreatedBy" = @stem AND "UpdatedBy" = @stem AND "Name" = @ipName""",
        """DELETE FROM "FunctionCostMappings" WHERE "Id" = @mappingId AND "FunctionConfigurationId" = @configurationId AND "FunctionCostId" = @costId AND "FunctionCostId1" IS NULL""",
        """DELETE FROM "FunctionCredentials" WHERE "Id" = @credentialId AND "FunctionConfigurationId" = @configurationId AND "KeyName" = @credentialName""",
        """DELETE FROM "FunctionConfigurations" WHERE "Id" = @configurationId AND "ConfigurationName" = @configurationName AND "Description" = @description""",
        """DELETE FROM "FunctionCosts" WHERE "Id" = @costId AND "CostName" = @costName AND "Description" = @description""",
        """DELETE FROM "VirtualKeys" WHERE "Id" = @keyId AND "VirtualKeyGroupId" = @groupId AND "KeyHash" = @keyHash AND "KeyName" = @keyName""",
        """DELETE FROM "VirtualKeyGroups" WHERE "Id" = @groupId AND "ExternalGroupId" = @stem AND "GroupName" = @groupName"""
    ];
}

internal sealed record NativeFunctionFixtureSnapshot(
    Guid Owner, int GroupId, int KeyId, int ConfigurationId, int CostId, int CredentialId, int MappingId,
    string VirtualKey, string VirtualKeyHash, string ConfigurationName);
