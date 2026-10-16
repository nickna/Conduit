using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.PersistenceConverterTests;

// This fixed inventory is shared by the conventional-model PostgreSQL contract and
// the isolated published compiled-model probe. Conversion is always performed by
// the actual property's type-mapping converter, never by these fixture factories.
internal static class PersistedValueConverterContract
{
    internal const int DescriptorCount = 31;
    internal const string SchemaPrefix = "converter_contract_";

    internal sealed record ConversionCase(string Name, object? Model, object? Provider);

    internal sealed record Descriptor(
        Type EntityType, string Property, Type PropertyType, Type ModelType,
        Type ProviderType, bool Nullable, string StoreType, NpgsqlDbType ParameterType,
        IReadOnlyList<ConversionCase> Cases)
    {
        internal string Key => EntityType.Name + "." + Property;

        internal string Table => EntityType.Name switch
        {
            "BillingAuditEvent" => "BillingAuditEvents",
            "FunctionCallAudit" => "FunctionCallAudits",
            "FunctionConfiguration" => "FunctionConfigurations",
            "FunctionCost" => "FunctionCosts",
            "FunctionCredential" => "FunctionCredentials",
            "FunctionExecution" => "FunctionExecutions",
            "FunctionExecutionAudit" => "FunctionExecutionAudits",
            "MediaRetentionPolicy" => "MediaRetentionPolicies",
            "Model" => "Models",
            "ModelCost" => "ModelCosts",
            "ModelProviderTypeAssociation" => "ModelIdentifiers",
            "ModelSeries" => "ModelSeries",
            "Notification" => "Notifications",
            "Provider" => "Providers",
            "ProviderKeyCredential" => "ProviderKeyCredentials",
            "ProviderMetadataDriftItem" => "ProviderMetadataDriftItems",
            "ProviderMetadataSyncRun" => "ProviderMetadataSyncRuns",
            "ProviderTool" => "ProviderTools",
            "RequestLog" => "RequestLogs",
            "VirtualKeyGroupTransaction" => "VirtualKeyGroupTransactions",
            _ => throw new InvalidOperationException("Unknown converted entity table.")
        };
    }

    internal static IReadOnlyList<Descriptor> Descriptors { get; } =
    [
        Int(typeof(BillingAuditEvent), "EventType", typeof(BillingAuditEventType), BillingAuditEventType.ErrorResponseSkipped, 7, (BillingAuditEventType)(-197), (BillingAuditEventType)int.MaxValue),
        Int(typeof(FunctionCallAudit), "EventType", typeof(FunctionCallAuditEventType), FunctionCallAuditEventType.FunctionCallExecutionCompleted, 3, (FunctionCallAuditEventType)(-197), (FunctionCallAuditEventType)int.MaxValue),
        Int(typeof(FunctionConfiguration), "DefaultExecutionMode", typeof(ExecutionMode), ExecutionMode.Asynchronous, 2, (ExecutionMode)(-197), (ExecutionMode)int.MaxValue),
        Int(typeof(FunctionConfiguration), "ProviderType", typeof(FunctionProviderType), FunctionProviderType.Mcp, 5, (FunctionProviderType)(-197), (FunctionProviderType)int.MaxValue),
        Int(typeof(FunctionConfiguration), "Purpose", typeof(FunctionPurpose), FunctionPurpose.RAG, 4, (FunctionPurpose)(-197), (FunctionPurpose)int.MaxValue),
        Int(typeof(FunctionCost), "PricingModel", typeof(FunctionPricingModel), FunctionPricingModel.Hybrid, 6, (FunctionPricingModel)(-197), (FunctionPricingModel)int.MaxValue),
        Int(typeof(FunctionCost), "ProviderType", typeof(FunctionProviderType), FunctionProviderType.Mcp, 5, (FunctionProviderType)(-197), (FunctionProviderType)int.MaxValue),
        Int(typeof(FunctionCost), "Purpose", typeof(FunctionPurpose), FunctionPurpose.RAG, 4, (FunctionPurpose)(-197), (FunctionPurpose)int.MaxValue, typeof(FunctionPurpose?), true),
        Int(typeof(FunctionCredential), "ProviderType", typeof(FunctionProviderType), FunctionProviderType.Mcp, 5, (FunctionProviderType)(-197), (FunctionProviderType)int.MaxValue),
        Int(typeof(FunctionExecution), "ExecutionMode", typeof(ExecutionMode), ExecutionMode.Asynchronous, 2, (ExecutionMode)(-197), (ExecutionMode)int.MaxValue),
        Int(typeof(FunctionExecution), "State", typeof(ExecutionState), ExecutionState.TimedOut, 6, (ExecutionState)(-197), (ExecutionState)int.MaxValue),
        Int(typeof(FunctionExecutionAudit), "EventType", typeof(FunctionAuditEventType), FunctionAuditEventType.SpendUpdated, 7, (FunctionAuditEventType)(-197), (FunctionAuditEventType)int.MaxValue),
        Int(typeof(MediaRetentionPolicy), "QuotaExceededBehavior", typeof(MediaQuotaExceededBehavior), MediaQuotaExceededBehavior.AllowAndEvict, 1, (MediaQuotaExceededBehavior)(-197), (MediaQuotaExceededBehavior)int.MaxValue),
        Int(typeof(Model), "CapabilitySource", typeof(ModelCapabilitySource), ModelCapabilitySource.Manual, 4, (ModelCapabilitySource)(-197), (ModelCapabilitySource)int.MaxValue),
        Int(typeof(Model), "TokenizerType", typeof(TokenizerType), TokenizerType.O200KBase, 5, (TokenizerType)(-197), (TokenizerType)int.MaxValue),
        Int(typeof(ModelCost), "PricingModel", typeof(PricingModel), PricingModel.RulesBased, 6, (PricingModel)(-197), (PricingModel)int.MaxValue),
        Int(typeof(ModelProviderTypeAssociation), "CapabilitySource", typeof(ModelCapabilitySource), ModelCapabilitySource.Manual, 4, (ModelCapabilitySource)(-197), (ModelCapabilitySource)int.MaxValue, typeof(ModelCapabilitySource?), true),
        Int(typeof(ModelProviderTypeAssociation), "Provider", typeof(ProviderType), ProviderType.Vertex, 17, (ProviderType)(-197), (ProviderType)int.MaxValue, typeof(ProviderType?), true),
        Int(typeof(ModelSeries), "TokenizerType", typeof(TokenizerType), TokenizerType.O200KBase, 5, (TokenizerType)(-197), (TokenizerType)int.MaxValue),
        Int(typeof(Notification), "Severity", typeof(NotificationSeverity), NotificationSeverity.Error, 2, (NotificationSeverity)(-197), (NotificationSeverity)int.MaxValue),
        Int(typeof(Notification), "Type", typeof(NotificationType), NotificationType.ProviderKeyReenabled, 4, (NotificationType)(-197), (NotificationType)int.MaxValue),
        Int(typeof(Provider), "ProviderType", typeof(ProviderType), ProviderType.Vertex, 17, (ProviderType)(-197), (ProviderType)int.MaxValue),
        Json(typeof(Provider), "Settings"),
        Json(typeof(ProviderKeyCredential), "SecretSettings"),
        Text(typeof(ProviderMetadataDriftItem), "DriftType", typeof(DriftType), DriftType.ModelDeprecated, "ModelDeprecated", (DriftType)(-197), (DriftType)int.MaxValue, "character varying(30)"),
        Text(typeof(ProviderMetadataDriftItem), "Status", typeof(DriftStatus), DriftStatus.AutoResolved, "AutoResolved", (DriftStatus)(-197), (DriftStatus)int.MaxValue, "character varying(20)"),
        Int(typeof(ProviderMetadataSyncRun), "ProviderType", typeof(ProviderType), ProviderType.Vertex, 17, (ProviderType)(-197), (ProviderType)int.MaxValue),
        // Nullable CLR enum, but [Required] makes the database column NOT NULL.
        Int(typeof(ProviderTool), "Provider", typeof(ProviderType), ProviderType.Vertex, 17, (ProviderType)(-197), (ProviderType)int.MaxValue, typeof(ProviderType?), false),
        Int(typeof(RequestLog), "BillingMethod", typeof(RequestBillingMethod), RequestBillingMethod.ProviderReportedCost, 1, (RequestBillingMethod)(-197), (RequestBillingMethod)int.MaxValue, typeof(RequestBillingMethod?), true),
        Int(typeof(VirtualKeyGroupTransaction), "ReferenceType", typeof(ReferenceType), ReferenceType.Initial, 4, (ReferenceType)(-197), (ReferenceType)int.MaxValue),
        Int(typeof(VirtualKeyGroupTransaction), "TransactionType", typeof(TransactionType), TransactionType.Adjustment, 4, (TransactionType)(-197), (TransactionType)int.MaxValue)
    ];

    private static Descriptor Int(Type entity, string property, Type modelType,
        object known, int literal, object negative, object maximum,
        Type? propertyType = null, bool nullable = false) =>
        new(entity, property, propertyType ?? modelType, modelType, typeof(int), nullable,
            "integer", NpgsqlDbType.Integer,
            [new("known", known, literal), new("unknown-negative", negative, -197),
             new("unknown-maximum", maximum, 2147483647), new("sql-null", null, null)]);

    private static Descriptor Text(Type entity, string property, Type modelType,
        object known, string literal, object negative, object maximum, string storeType) =>
        new(entity, property, modelType, modelType, typeof(string), false, storeType,
            NpgsqlDbType.Varchar,
            [new("known", known, literal), new("unknown-negative", negative, "-197"),
             new("unknown-maximum", maximum, "2147483647"), new("sql-null", null, null)]);

    private static Descriptor Json(Type entity, string property) =>
        new(entity, property, typeof(Dictionary<string, string>),
            typeof(Dictionary<string, string>), typeof(string), true, "jsonb", NpgsqlDbType.Jsonb,
            [new("sql-null", null, null), new("empty", new Dictionary<string, string>(), "{}"),
             new("unicode", new Dictionary<string, string>
             {
                 ["O'Hare 雪"] = "café 🔎\nquoted \"value\"",
                 ["Case"] = "UPPER", ["case"] = "lower"
             }, """{"O'Hare 雪":"café 🔎\nquoted \"value\"","Case":"UPPER","case":"lower"}"""),
             new("null-value", new Dictionary<string, string> { ["nullable"] = null! }, """{"nullable":null}""")]);

    internal static void AssertInventory(IModel model)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(property => property.GetTypeMapping().Converter is not null))
            {
                Ensure(actual.Add(entity.ClrType.Name + "." + property.Name), "duplicate converted property");
            }
        }
        Ensure(Descriptors.Count == DescriptorCount && actual.Count == DescriptorCount,
            "converted property count changed");
        Ensure(actual.SetEquals(Descriptors.Select(descriptor => descriptor.Key)),
            "converted property inventory changed");
        foreach (var descriptor in Descriptors) Resolve(model, descriptor);
    }

    internal static IProperty Resolve(IModel model, Descriptor descriptor)
    {
        var entity = model.GetEntityTypes().SingleOrDefault(item => item.ClrType == descriptor.EntityType)
            ?? throw new InvalidOperationException("Converter entity is missing: " + descriptor.Key);
        var property = entity.FindProperty(descriptor.Property)
            ?? throw new InvalidOperationException("Converter property is missing: " + descriptor.Key);
        var mapping = property.GetTypeMapping();
        var converter = mapping.Converter
            ?? throw new InvalidOperationException("Converter is missing: " + descriptor.Key);
        Ensure(property.ClrType == descriptor.PropertyType && property.IsNullable == descriptor.Nullable,
            descriptor.Key + " property type/nullability changed");
        Ensure(converter.ModelClrType == descriptor.ModelType && converter.ProviderClrType == descriptor.ProviderType,
            descriptor.Key + " converter types changed");
        Ensure(property.GetColumnType() == descriptor.StoreType,
            descriptor.Key + " PostgreSQL column type changed");
        Ensure(entity.GetTableName() == descriptor.Table && entity.GetSchema() is null &&
            property.GetColumnName(StoreObjectIdentifier.Table(descriptor.Table, null)) == descriptor.Property,
            descriptor.Key + " table/column/schema mapping changed");
        return property;
    }

    internal static void AssertDelegates(IModel model, Descriptor descriptor)
    {
        var property = Resolve(model, descriptor);
        var converter = property.GetTypeMapping().Converter!;
        foreach (var sample in descriptor.Cases)
        {
            AssertProvider(descriptor, converter.ConvertToProvider(sample.Model), sample.Provider);
            AssertModel(converter.ConvertFromProvider(sample.Provider), sample.Model);
        }
        if (descriptor.ParameterType == NpgsqlDbType.Jsonb)
        {
            // JSON null and whitespace return null, independently of SQL NULL.
            Ensure(converter.ConvertFromProvider("null") is null, descriptor.Key + " JSON null changed");
            Ensure(converter.ConvertFromProvider(" \t\r\n") is null, descriptor.Key + " legacy whitespace changed");
            foreach (var invalid in new[] { "{", "[]", "1", "\"value\"", "{\"nested\":{\"value\":true}}" })
                Expect<JsonException>(() => converter.ConvertFromProvider(invalid));
            AssertDictionaryComparer(property);
        }
        else if (descriptor.ParameterType == NpgsqlDbType.Varchar)
        {
            AssertModel(converter.ConvertFromProvider(((string)descriptor.Cases[0].Provider!).ToLowerInvariant()),
                descriptor.Cases[0].Model);
            Expect<InvalidOperationException>(() => converter.ConvertFromProvider("not-a-declared-member"));
        }
    }

    internal static void AssertDictionaryComparer(IProperty property)
    {
        // The configured property comparer (not TypeMapping.Comparer) owns tracking.
        var comparer = property.GetValueComparer()
            ?? throw new InvalidOperationException("Dictionary property comparer is missing.");
        var original = new Dictionary<string, string> { ["region"] = "west", ["unicode"] = "雪 🔎" };
        var snapshot = comparer.Snapshot(original) as Dictionary<string, string>
            ?? throw new InvalidOperationException("Dictionary snapshot type changed.");
        Ensure(!ReferenceEquals(original, snapshot), "dictionary snapshot aliases the caller");
        AssertEquivalent(original, snapshot, "snapshot");
        var reordered = new Dictionary<string, string> { ["unicode"] = "雪 🔎", ["region"] = "west" };
        AssertEquivalent(snapshot, reordered, "reordered contents");
        AssertEquivalent(snapshot, new Dictionary<string, string>(reordered, StringComparer.OrdinalIgnoreCase),
            "identical contents with a caller-provided comparer");

        var changedValue = new Dictionary<string, string>(snapshot) { ["region"] = "West" };
        AssertDifferent(snapshot, changedValue, "case-sensitive values");
        var changedKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REGION"] = "west", ["unicode"] = "雪 🔎"
        };
        AssertDifferent(snapshot, changedKey, "ordinal keys independent of the caller-provided comparer");
        AssertDifferent(snapshot, new Dictionary<string, string> { ["region"] = "west" }, "removed entry");
        var caseDistinct = new Dictionary<string, string> { ["Case"] = "UPPER", ["case"] = "lower" };
        AssertEquivalent(caseDistinct, new Dictionary<string, string> { ["case"] = "lower", ["Case"] = "UPPER" },
            "distinct ordinal keys");

        var empty = new Dictionary<string, string>();
        AssertEquivalent(empty, new Dictionary<string, string>(), "empty dictionaries");
        AssertDifferent(null, empty, "null and empty dictionaries");
        var emptySnapshot = comparer.Snapshot(empty) as Dictionary<string, string>;
        Ensure(emptySnapshot is { Count: 0 } && !ReferenceEquals(empty, emptySnapshot),
            "empty dictionary snapshot aliases the caller");
        var nullValue = new Dictionary<string, string> { ["nullable"] = null!, ["region"] = "west" };
        var nullValueSnapshot = comparer.Snapshot(nullValue) as Dictionary<string, string>
            ?? throw new InvalidOperationException("Null-valued dictionary snapshot type changed.");
        Ensure(!ReferenceEquals(nullValue, nullValueSnapshot), "null-valued dictionary snapshot aliases the caller");
        AssertEquivalent(nullValue, nullValueSnapshot, "null-valued snapshot");
        AssertEquivalent(nullValue, new Dictionary<string, string> { ["region"] = "west", ["nullable"] = null! },
            "reordered null-valued entries");
        nullValue["nullable"] = "";
        Ensure(nullValueSnapshot["nullable"] is null, "null-valued snapshot mutated with caller");
        AssertDifferent(nullValue, nullValueSnapshot, "null and empty-string values");
        AssertDifferent(new Dictionary<string, string> { ["nullable"] = null! }, empty, "null-valued and missing entries");

        original["region"] = "east";
        AssertDifferent(original, snapshot, "in-place value change");
        original["added"] = "café";
        Ensure(snapshot["region"] == "west" && !snapshot.ContainsKey("added"), "snapshot mutated with caller");
        AssertDifferent(original, snapshot, "in-place entry addition");
        Ensure(comparer.Snapshot(null) is null && comparer.Equals(null, null) && comparer.GetHashCode(null) == 0,
            "dictionary null comparer changed");

        void AssertEquivalent(Dictionary<string, string> left, Dictionary<string, string> right, string scenario)
        {
            Ensure(comparer.Equals(left, right) && comparer.Equals(right, left), "dictionary equality changed for " + scenario);
            Ensure(comparer.GetHashCode(left) == comparer.GetHashCode(right), "equal dictionary hashes differ for " + scenario);
        }

        void AssertDifferent(Dictionary<string, string>? left, Dictionary<string, string>? right, string scenario) =>
            Ensure(!comparer.Equals(left, right) && !comparer.Equals(right, left), "dictionary difference missed for " + scenario);
    }

    internal static async Task<int> ExercisePostgresAsync(
        NpgsqlConnection connection, string schema, IModel model,
        IEnumerable<Descriptor> descriptors, CancellationToken cancellationToken)
    {
        ValidateSchema(schema);
        AssertInventory(model);
        var checks = 0;
        foreach (var descriptor in descriptors)
        {
            AssertDelegates(model, descriptor);
            var index = Descriptors.ToList().FindIndex(item => item.Key == descriptor.Key);
            Ensure(index >= 0, "unknown converter descriptor");
            var table = $"\"{schema}\".\"value_{index:D2}\"";
            await using (var create = new NpgsqlCommand(
                $"CREATE TABLE {table} (\"Case\" text PRIMARY KEY, \"Value\" {descriptor.StoreType}{(descriptor.Nullable ? "" : " NOT NULL")})", connection))
                await create.ExecuteNonQueryAsync(cancellationToken);
            var converter = Resolve(model, descriptor).GetTypeMapping().Converter!;
            foreach (var sample in descriptor.Cases)
            {
                var value = converter.ConvertToProvider(sample.Model);
                await using var insert = new NpgsqlCommand($"INSERT INTO {table} VALUES (@case, @value)", connection);
                insert.Parameters.AddWithValue("case", NpgsqlDbType.Text, sample.Name);
                insert.Parameters.Add(new NpgsqlParameter("value", descriptor.ParameterType) { Value = value ?? DBNull.Value });
                if (sample.Model is null && !descriptor.Nullable)
                {
                    try
                    {
                        await insert.ExecuteNonQueryAsync(cancellationToken);
                        throw new InvalidOperationException("Required converter column accepted SQL NULL.");
                    }
                    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.NotNullViolation)
                    {
                        // This case proves the required column rejects SQL NULL.
                        // Every other SQL state, cancellation or unexpected success fails.
                    }
                    checks++;
                    continue;
                }
                await insert.ExecuteNonQueryAsync(cancellationToken);
                await using var read = new NpgsqlCommand($"SELECT \"Value\", \"Value\" IS NULL FROM {table} WHERE \"Case\" = @case", connection);
                read.Parameters.AddWithValue("case", NpgsqlDbType.Text, sample.Name);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                Ensure(await reader.ReadAsync(cancellationToken), "persisted converter row is missing");
                var stored = reader.IsDBNull(0) ? null : descriptor.ParameterType == NpgsqlDbType.Integer
                    ? (object)reader.GetInt32(0) : reader.GetString(0);
                Ensure(reader.GetBoolean(1) == (sample.Provider is null), "SQL NULL mapping changed");
                AssertProvider(descriptor, stored, sample.Provider);
                AssertModel(converter.ConvertFromProvider(stored), sample.Model);
                Ensure(!await reader.ReadAsync(cancellationToken), "persisted converter row is duplicated");
                checks++;
            }
            if (descriptor.ParameterType == NpgsqlDbType.Jsonb)
            {
                await using var jsonNull = new NpgsqlCommand(
                    $"INSERT INTO {table} VALUES ('json-null', 'null'::jsonb); SELECT \"Value\" IS NULL, \"Value\"::text FROM {table} WHERE \"Case\" = 'json-null'", connection);
                await using var reader = await jsonNull.ExecuteReaderAsync(cancellationToken);
                Ensure(await reader.ReadAsync(cancellationToken) && !reader.GetBoolean(0) && reader.GetString(1) == "null",
                    "JSON null collapsed into SQL NULL");
                Ensure(converter.ConvertFromProvider(reader.GetString(1)) is null, "persisted JSON null conversion changed");
                checks++;
            }
        }
        return checks;
    }

    private static void AssertProvider(Descriptor descriptor, object? actual, object? expected)
    {
        if (descriptor.ParameterType == NpgsqlDbType.Jsonb && actual is string actualJson && expected is string expectedJson)
        {
            using var left = JsonDocument.Parse(actualJson);
            using var right = JsonDocument.Parse(expectedJson);
            Ensure(JsonElement.DeepEquals(left.RootElement, right.RootElement), descriptor.Key + " JSON contents changed");
        }
        else Ensure(Equals(actual, expected), descriptor.Key + " provider scalar changed");
    }

    internal static void AssertModel(object? actual, object? expected)
    {
        if (actual is Dictionary<string, string> actualBag && expected is Dictionary<string, string> expectedBag)
        {
            Ensure(!ReferenceEquals(actualBag, expectedBag) && actualBag.Count == expectedBag.Count,
                "read dictionary alias/cardinality changed");
            foreach (var pair in expectedBag)
                Ensure(actualBag.TryGetValue(pair.Key, out var value) && value == pair.Value, "read dictionary value changed");
        }
        else Ensure(Equals(actual, expected), "read converter model scalar changed");
    }

    internal static void ValidateSchema(string schema)
    {
        Ensure(schema.StartsWith(SchemaPrefix, StringComparison.Ordinal) &&
            schema.Length == SchemaPrefix.Length + 32 &&
            Guid.TryParseExact(schema.AsSpan(SchemaPrefix.Length), "N", out var owner) && owner != Guid.Empty,
            "schema is not owned by the converter contract");
    }

    internal static async Task CleanupAsync(string connectionString, string schema)
    {
        ValidateSchema(schema);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bounded = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false, Timeout = 10, CommandTimeout = 10, CancellationTimeout = 1000
        };
        await using var connection = new NpgsqlConnection(bounded.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using (var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection))
            await drop.ExecuteNonQueryAsync(deadline.Token);
        await using var absent = new NpgsqlCommand("SELECT count(*) FROM pg_namespace WHERE nspname = @schema", connection);
        absent.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
        Ensure((long)(await absent.ExecuteScalarAsync(deadline.Token))! == 0, "owned schema cleanup was not confirmed");
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Invalid converter input did not produce the expected exception type.");
    }

    internal static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Persisted converter contract: " + message + ".");
    }
}
