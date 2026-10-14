using System.Text.Json.Nodes;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

internal static class FunctionExecutionReaderPersistenceProbe
{
    private const int VirtualKeyId = 91511001;
    private const int OtherVirtualKeyId = 91511002;
    private static readonly DateTime Instant = new(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);

    internal static async Task RunAsync(NpgsqlDataSource source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        Require(new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize == 1, "single-slot source");
        var writer = new NpgsqlFunctionExecutionRuntimeWriter(source);
        var seeds = Enumerable.Range(0, 105).Select(index => Snapshot(index, VirtualKeyId)).ToArray();
        foreach (var seed in seeds) await writer.CreateAsync(seed, token);
        var other = Snapshot(105, OtherVirtualKeyId);
        await writer.CreateAsync(other, token);

        // This oracle deliberately selects and materializes every column itself;
        // neither production execution nor configuration row readers are reused.
        var expected = await ReadOracleAsync(source, VirtualKeyId, token);
        Require(expected.Count == seeds.Length, "owned oracle contains more than 100 executions");
        var expectedById = expected.ToDictionary(row => row.Id);
        var reader = new NpgsqlFunctionExecutionRuntimeReader(source);
        var listed = await reader.ListByVirtualKeyAsync(VirtualKeyId, token);
        AssertList(listed, expectedById);
        var isolated = await reader.ListByVirtualKeyAsync(OtherVirtualKeyId, token);
        Require(isolated.Count == 1 && isolated[0].Id == other.Id, "virtual-key isolation");
        AssertScalars(isolated[0], (await ReadOracleAsync(source, OtherVirtualKeyId, token)).Single());
        AssertGraph(isolated[0]);
        Require((await reader.ListByVirtualKeyAsync(int.MinValue, token)).Count == 0, "missing virtual key");
        Require(await reader.GetByIdAsync(Guid.Empty, token) is null &&
            await reader.GetByIdAsync(Guid.NewGuid(), token) is null, "empty and missing execution IDs");

        foreach (var seed in seeds.Take(3))
        {
            var row = await reader.GetByIdAsync(seed.Id, token)
                ?? throw new InvalidOperationException("Native execution reader omitted a seeded ID.");
            AssertScalars(row, expectedById[seed.Id]);
            AssertGraph(row);
            if (seed.FunctionConfigurationId == 12)
                Require(!row.FunctionConfiguration!.IsEnabled, "disabled configuration remains included");
            if (seed.FunctionConfigurationId == 13)
                Require((int)row.FunctionConfiguration!.ProviderType == 197 &&
                    (int)row.FunctionConfiguration.Purpose == -9 &&
                    (int)row.FunctionConfiguration.DefaultExecutionMode == 123, "unknown configuration enums");
        }

        var detached = listed.Single(row => row.Id == seeds[0].Id);
        detached.StatusMessage = "detached execution mutation";
        detached.FunctionConfiguration!.ConfigurationName = "detached configuration mutation";
        detached.FunctionConfiguration.Executions.Clear();
        var refreshed = await reader.GetByIdAsync(detached.Id, token)
            ?? throw new InvalidOperationException("Native execution reader omitted its detached row.");
        AssertScalars(refreshed, expectedById[detached.Id]);
        AssertGraph(refreshed);
        Require(!ReferenceEquals(refreshed, detached) &&
            !ReferenceEquals(refreshed.FunctionConfiguration, detached.FunctionConfiguration), "fresh detached graph");
        AssertList(await reader.ListByVirtualKeyAsync(VirtualKeyId, token), expectedById);

        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            cancelled.Cancel();
            await ExpectCancelledAsync(reader.GetByIdAsync(seeds[0].Id, cancelled.Token), cancelled);
            await ExpectCancelledAsync(reader.ListByVirtualKeyAsync(VirtualKeyId, cancelled.Token), cancelled);
        }
        await using (await source.OpenConnectionAsync(token))
        {
            foreach (var getById in new[] { true, false })
            {
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task read = getById ? reader.GetByIdAsync(seeds[0].Id, cancelled.Token)
                    : reader.ListByVirtualKeyAsync(VirtualKeyId, cancelled.Token);
                try
                {
                    Require(!read.IsCompleted, "reader waits on occupied single-slot pool");
                    cancelled.Cancel();
                    await ExpectCancelledAsync(read, cancelled);
                }
                finally
                {
                    cancelled.Cancel();
                    try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
                }
            }
        }
        var recovered = await reader.GetByIdAsync(seeds[0].Id, token)
            ?? throw new InvalidOperationException("Native execution reader failed pool recovery.");
        AssertScalars(recovered, expectedById[recovered.Id]);
        AssertGraph(recovered);
        AssertList(await reader.ListByVirtualKeyAsync(VirtualKeyId, token), expectedById);
        Console.WriteLine("Native function execution reader passed: 39 scalars, 105 ordered rows, key isolation, detached graph identity, cancellation and pool reuse.");
    }

    private static FunctionExecution Snapshot(int index, int virtualKeyId)
    {
        var populated = index % 2 == 0;
        return new FunctionExecution
        {
            Id = Guid.NewGuid(), FunctionConfigurationId = 11 + index % 3, VirtualKeyId = virtualKeyId,
            ExecutionMode = (ExecutionMode)123, State = (ExecutionState)(-9),
            // Equal timestamp pairs intentionally leave tie ordering unspecified.
            RequestedAt = Instant.AddTicks(index / 2 * 10),
            StartedAt = populated ? Instant.AddSeconds(1) : null, CompletedAt = populated ? Instant.AddSeconds(3) : null,
            Duration = populated ? TimeSpan.FromTicks(-12345670) : null,
            RequestJson = populated ? """{"quoted":"O'Hare 雪","nested":[1,null,false]}""" : null,
            ResponseJson = populated ? "null" : null, ErrorMessage = populated ? "O'Hare 雪" : null,
            EstimatedCost = populated ? 1234567890.12345678m : null, ActualCost = populated ? -0.00000001m : null,
            CostCalculationDetails = populated ? "[]" : null, RetryCount = -3,
            NextRetryAt = populated ? Instant.AddDays(1) : null, LeasedBy = populated ? "worker 雪" : null,
            LeaseExpiryTime = populated ? Instant.AddMinutes(5) : null, Version = populated ? int.MaxValue : int.MinValue,
            WebhookUrl = populated ? "https://example.test/O'Hare" : null, WebhookDelivered = populated,
            ProgressPercentage = populated ? -1 : null, StatusMessage = populated ? "" : null
        };
    }

    private static async Task<IReadOnlyList<FunctionExecution>> ReadOracleAsync(
        NpgsqlDataSource source, int virtualKeyId, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e."Id", e."FunctionConfigurationId", e."VirtualKeyId", e."ExecutionMode", e."State",
                e."RequestedAt", e."StartedAt", e."CompletedAt", e."Duration", e."RequestJson", e."ResponseJson",
                e."ErrorMessage", e."EstimatedCost", e."ActualCost", e."CostCalculationDetails", e."RetryCount",
                e."NextRetryAt", e."LeasedBy", e."LeaseExpiryTime", e."Version", e."WebhookUrl", e."WebhookDelivered",
                e."ProgressPercentage", e."StatusMessage", c."Id", c."ProviderType", c."ConfigurationName", c."Purpose",
                c."DefaultExecutionMode", c."BaseUrl", c."IsEnabled", c."TimeoutSeconds", c."CacheTtlMinutes", c."MaxRetries",
                c."ProviderSettings", c."ParameterSchema", c."Description", c."CreatedAt", c."UpdatedAt"
            FROM "FunctionExecutions" e JOIN "FunctionConfigurations" c ON c."Id" = e."FunctionConfigurationId"
            WHERE e."VirtualKeyId" = @virtualKey ORDER BY e."RequestedAt" DESC
            """;
        command.Parameters.AddWithValue("virtualKey", virtualKeyId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<FunctionExecution>();
        while (await reader.ReadAsync(token))
        {
            rows.Add(new FunctionExecution
            {
                Id = reader.GetGuid(0), FunctionConfigurationId = reader.GetInt32(1), VirtualKeyId = reader.GetInt32(2),
                ExecutionMode = (ExecutionMode)reader.GetInt32(3), State = (ExecutionState)reader.GetInt32(4),
                RequestedAt = reader.GetDateTime(5), StartedAt = Date(reader, 6), CompletedAt = Date(reader, 7),
                Duration = reader.IsDBNull(8) ? null : reader.GetTimeSpan(8), RequestJson = Text(reader, 9),
                ResponseJson = Text(reader, 10), ErrorMessage = Text(reader, 11),
                EstimatedCost = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                ActualCost = reader.IsDBNull(13) ? null : reader.GetDecimal(13), CostCalculationDetails = Text(reader, 14),
                RetryCount = reader.GetInt32(15), NextRetryAt = Date(reader, 16), LeasedBy = Text(reader, 17),
                LeaseExpiryTime = Date(reader, 18), Version = reader.GetInt32(19), WebhookUrl = Text(reader, 20),
                WebhookDelivered = reader.GetBoolean(21), ProgressPercentage = Int(reader, 22), StatusMessage = Text(reader, 23),
                FunctionConfiguration = new FunctionConfiguration
                {
                    Id = reader.GetInt32(24), ProviderType = (FunctionProviderType)reader.GetInt32(25),
                    ConfigurationName = reader.GetString(26), Purpose = (FunctionPurpose)reader.GetInt32(27),
                    DefaultExecutionMode = (ExecutionMode)reader.GetInt32(28), BaseUrl = Text(reader, 29),
                    IsEnabled = reader.GetBoolean(30), TimeoutSeconds = Int(reader, 31), CacheTtlMinutes = Int(reader, 32),
                    MaxRetries = Int(reader, 33), ProviderSettings = Text(reader, 34), ParameterSchema = Text(reader, 35),
                    Description = Text(reader, 36), CreatedAt = reader.GetDateTime(37), UpdatedAt = reader.GetDateTime(38)
                }
            });
        }
        return rows;
    }

    private static void AssertList(IReadOnlyList<FunctionExecution> rows, IReadOnlyDictionary<Guid, FunctionExecution> expected)
    {
        Require(rows.Count == expected.Count && rows.Select(row => row.Id).Distinct().Count() == rows.Count &&
            rows.All(row => expected.ContainsKey(row.Id)), "complete isolated list without paging");
        var configurations = new HashSet<FunctionConfiguration>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            AssertScalars(row, expected[row.Id]);
            AssertGraph(row);
            Require(configurations.Add(row.FunctionConfiguration!), "configuration instance is distinct per execution");
            if (index > 0) Require(rows[index - 1].RequestedAt >= row.RequestedAt, "requested-at descending order");
        }
    }

    private static void AssertScalars(FunctionExecution row, FunctionExecution expected)
    {
        Require(row.Id == expected.Id && row.FunctionConfigurationId == expected.FunctionConfigurationId &&
            row.VirtualKeyId == expected.VirtualKeyId && row.ExecutionMode == expected.ExecutionMode && row.State == expected.State &&
            SameDate(row.RequestedAt, expected.RequestedAt) && SameDate(row.StartedAt, expected.StartedAt) &&
            SameDate(row.CompletedAt, expected.CompletedAt) && row.Duration == expected.Duration &&
            SameJson(row.RequestJson, expected.RequestJson) && SameJson(row.ResponseJson, expected.ResponseJson) &&
            row.ErrorMessage == expected.ErrorMessage && row.EstimatedCost == expected.EstimatedCost && row.ActualCost == expected.ActualCost &&
            SameJson(row.CostCalculationDetails, expected.CostCalculationDetails) && row.RetryCount == expected.RetryCount &&
            SameDate(row.NextRetryAt, expected.NextRetryAt) && row.LeasedBy == expected.LeasedBy &&
            SameDate(row.LeaseExpiryTime, expected.LeaseExpiryTime) && row.Version == expected.Version &&
            row.WebhookUrl == expected.WebhookUrl && row.WebhookDelivered == expected.WebhookDelivered &&
            row.ProgressPercentage == expected.ProgressPercentage && row.StatusMessage == expected.StatusMessage, "24 execution scalars");
        var configuration = row.FunctionConfiguration ?? throw new InvalidOperationException("Execution configuration missing.");
        var expectedConfiguration = expected.FunctionConfiguration!;
        Require(configuration.Id == expectedConfiguration.Id && configuration.ProviderType == expectedConfiguration.ProviderType &&
            configuration.ConfigurationName == expectedConfiguration.ConfigurationName && configuration.Purpose == expectedConfiguration.Purpose &&
            configuration.DefaultExecutionMode == expectedConfiguration.DefaultExecutionMode && configuration.BaseUrl == expectedConfiguration.BaseUrl &&
            configuration.IsEnabled == expectedConfiguration.IsEnabled && configuration.TimeoutSeconds == expectedConfiguration.TimeoutSeconds &&
            configuration.CacheTtlMinutes == expectedConfiguration.CacheTtlMinutes && configuration.MaxRetries == expectedConfiguration.MaxRetries &&
            SameJson(configuration.ProviderSettings, expectedConfiguration.ProviderSettings) &&
            SameJson(configuration.ParameterSchema, expectedConfiguration.ParameterSchema) && configuration.Description == expectedConfiguration.Description &&
            SameDate(configuration.CreatedAt, expectedConfiguration.CreatedAt) && SameDate(configuration.UpdatedAt, expectedConfiguration.UpdatedAt),
            "15 configuration scalars");
    }

    private static void AssertGraph(FunctionExecution row)
    {
        var configuration = row.FunctionConfiguration ?? throw new InvalidOperationException("Execution configuration missing.");
        Require(configuration.Id == row.FunctionConfigurationId && configuration.Executions.Count == 1 &&
            ReferenceEquals(configuration.Executions.Single(), row) && configuration.CostMappings.Count == 0 && row.AuditEvents.Count == 0,
            "exact per-row inverse identity and unloaded cost/audit collections");
    }

    private static bool SameDate(DateTime? actual, DateTime? expected) => actual == expected &&
        (!actual.HasValue || actual.Value.Kind == DateTimeKind.Utc);
    private static bool SameJson(string? actual, string? expected) => actual is null || expected is null
        ? actual is null && expected is null : JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTime? Date(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    private static int? Int(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static async Task ExpectCancelledAsync(Task read, CancellationTokenSource cancellation)
    {
        try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        throw new InvalidOperationException("Native execution reader ignored cancellation.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Native function execution reader failed: {description}.");
    }
}
