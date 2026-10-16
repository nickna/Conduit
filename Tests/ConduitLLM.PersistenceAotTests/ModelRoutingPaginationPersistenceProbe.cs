using System.Runtime.ExceptionServices;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

internal static class ModelRoutingPaginationPersistenceProbe
{
    private const int ProviderId = 1901;
    private static readonly DateTime Instant = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    // Called after the original model-routing assertions in the caller-owned
    // disposable schema. These fixture IDs are disjoint from that original graph.
    internal static async Task RunAsync(string connectionString)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 1, MinPoolSize = 0, ApplicationName = $"native_model_page_{Guid.NewGuid():N}",
            Timeout = 5, CommandTimeout = 10, CancellationTimeout = 1000
        };
        var ownedSchema = builder.SearchPath;
        Require(ownedSchema is not null && ownedSchema.StartsWith(PersistenceProbeLifecycle.SchemaPrefix, StringComparison.Ordinal) &&
            ownedSchema.Length == PersistenceProbeLifecycle.SchemaPrefix.Length + 32 &&
            Guid.TryParseExact(ownedSchema.AsSpan(PersistenceProbeLifecycle.SchemaPrefix.Length), "N", out _),
            "seed only the caller-owned GUID schema");
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        var store = new NpgsqlModelProviderMappingRuntimeStore(source);
        var baseline = await store.GetByIdAsync(906, deadline.Token);
        Require(baseline is { ModelAlias: "native-route", ProviderId: 901 }, "original routing fixture");
        await SeedAsync(source, deadline.Token);

        foreach (var providerOnly in new[] { false, true })
        {
            var expected = ExpectedIds(providerOnly);
            foreach (var (page, size) in new[] { (1, 1), (2, 2), (0, 0), (-3, -1), (1, 500), (1000, 10) })
            {
                var actual = await ReadBoundedAsync(store, providerOnly, page, size, deadline.Token);
                var normalizedPage = Math.Max(1, page);
                var normalizedSize = Math.Clamp(size <= 0 ? 20 : size, 1, 100);
                Require(actual.TotalCount == expected.Length && actual.Items.Select(item => item.Id).SequenceEqual(
                    expected.Skip((normalizedPage - 1) * normalizedSize).Take(normalizedSize)),
                    "single-slot total/filter/order/tie-break/normalization");
                foreach (var item in actual.Items) AssertGraph(item, baseline!);
            }

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectCallerCancellationAsync(Read(store, providerOnly, 1, 10, cancelled.Token), cancelled);
            }
            await AssertOccupiedPoolCancellationAsync(source, store, providerOnly, deadline.Token);
            await AssertSqlCancellationAsync(source, store, builder.ConnectionString, providerOnly, countPhase: true, deadline.Token);
            await AssertSqlCancellationAsync(source, store, builder.ConnectionString, providerOnly, countPhase: false, deadline.Token);
            AssertPage(await ReadBoundedAsync(store, providerOnly, 1, 10, deadline.Token), expected.Take(10), expected.Length, baseline!);
        }

        var missing = await ReadBoundedAsync(store, true, 0, 500, deadline.Token, providerId: -1);
        Require(missing.TotalCount == 0 && missing.Items.Count == 0, "missing provider remains an empty page");
        var populated = (await ReadBoundedAsync(store, true, 1, 1, deadline.Token)).Items.Single();
        populated.Provider.Settings!["region"] = "detached mutation";
        Require((await ReadBoundedAsync(store, true, 1, 1, deadline.Token)).Items.Single().Provider.Settings!["region"] == "west",
            "page graph remains detached across reads");
        await AssertSlotReusableAsync(source, deadline.Token);
        Console.WriteLine("Model routing pagination persistence passed: both typed APIs use one pool slot; complete graphs, totals, provider filters, ordering, normalized bounds, occupied-pool and observed count/graph SQL cancellation, caller tokens and slot reuse.");
    }

    private static int[] ExpectedIds(bool providerOnly) => providerOnly
        ? Enumerable.Range(2001, 105).ToArray()
        : new[] { 906, 2001, 2002, 2200 }.Concat(Enumerable.Range(2003, 103)).ToArray();

    private static Task<ModelProviderMappingRuntimePage> Read(NpgsqlModelProviderMappingRuntimeStore store,
        bool providerOnly, int page, int size, CancellationToken token, int providerId = ProviderId) => providerOnly
        ? store.GetByProviderPaginatedAsync(providerId, page, size, token)
        : store.GetPaginatedAsync(page, size, token);

    private static async Task<ModelProviderMappingRuntimePage> ReadBoundedAsync(NpgsqlModelProviderMappingRuntimeStore store,
        bool providerOnly, int page, int size, CancellationToken token, int providerId = ProviderId)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        var pending = Read(store, providerOnly, page, size, bounded.Token, providerId);
        Exception? failure = null;
        ModelProviderMappingRuntimePage? result = null;
        try { result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) { failure = exception; }
        bounded.Cancel();
        await ReapAsync(pending, failure);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    private static void AssertPage(ModelProviderMappingRuntimePage page, IEnumerable<int> expected, int count,
        ModelProviderMappingRuntimeRecord baseline)
    {
        Require(page.TotalCount == count && page.Items.Select(item => item.Id).SequenceEqual(expected), "page recovery");
        foreach (var item in page.Items) AssertGraph(item, baseline);
    }

    private static async Task SeedAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Providers" ("Id", "ProviderType", "ProviderName", "BaseUrl", "Settings", "IsEnabled",
                "TrustProviderReportedCosts", "ProviderCostMarkupMultiplier", "CreatedAt", "UpdatedAt") VALUES
                (1901, @provider, 'page provider 雪', 'https://example.test/page', '{"region":"west","label":"O''Hare 雪"}', false, true, 1.12500001, @instant, @updated),
                (1911, @provider, 'null page provider', NULL, NULL, false, false, 0.75000001, @instant, @updated);
            INSERT INTO "ModelSeries" ("Id", "AuthorId", "Name", "Description", "TokenizerType", "Parameters") VALUES
                (1902, 17, 'page series 雪', 'series description', 23, '{"seed":"系列"}'),
                (1912, 0, 'null page series', NULL, 0, '{}');
            INSERT INTO "Models" ("Id", "Name", "Version", "Description", "ModelCardUrl", "ModelSeriesId",
                "SupportsVision", "SupportsImageGeneration", "SupportsVideoGeneration", "SupportsEmbeddings",
                "SupportsSpeechToText", "SupportsTextToSpeech", "SupportsRerank", "SupportsChat", "SupportsFunctionCalling", "SupportsStreaming",
                "InputModalities", "OutputModalities", "CapabilitySource", "CapabilitiesLastVerifiedAt", "TokenizerType", "MaxInputTokens", "MaxOutputTokens",
                "IsActive", "Parameters", "CreatedAt", "UpdatedAt") VALUES
                (1903, 'page model', 'v2', 'model description', 'https://example.test/card', 1902,
                    true, false, true, false, true, false, true, false, true, false,
                    '["text","image"]', '["audio"]', 77, @modelVerified, 42, 65536, 4096, false, '{"temperature":0.25}', @instant, @updated),
                (1913, 'null page model', NULL, NULL, NULL, 1912,
                    false, false, false, true, false, false, false, false, false, false,
                    NULL, NULL, 0, NULL, 0, NULL, NULL, true, NULL, @instant, @updated);
            INSERT INTO "ModelCosts" ("Id", "CostName", "PricingModel", "PricingConfiguration", "InputCostPerMillionTokens", "OutputCostPerMillionTokens",
                "EmbeddingCostPerMillionTokens", "CreatedAt", "UpdatedAt", "ModelType", "IsActive", "EffectiveDate", "ExpiryDate", "Description", "Priority",
                "BatchProcessingMultiplier", "SupportsBatchProcessing", "CachedInputCostPerMillionTokens", "CachedInputWriteCostPerMillionTokens",
                "CostPerSearchUnit", "AudioCostPerMinute", "AudioCostPerThousandCharacters", "ReasoningCostPerMillionTokens") VALUES
                (1904, 'page cost', 123, '{"mode":"fixture"}', 2.50000001, 10.00000002, 0.75000003, @instant, @updated, 'chat', false,
                    @effective, @expiry, 'cost description', -7, 0.5, false, 0.25000004, 0.37500005, 0.12500006, 1.75000007, 2.25000008, 3.12500009);
            INSERT INTO "ModelIdentifiers" ("Id", "ModelId", "IsEnabled", "MaxInputTokens", "MaxOutputTokens", "InputModalities", "OutputModalities",
                "OperationalCapabilities", "CapabilitySource", "CapabilitiesLastVerifiedAt", "ProviderVariation", "QualityScore", "SpeedScore", "Identifier",
                "Provider", "ModelCostId", "IsPrimary", "Metadata") VALUES
                (1905, 1903, false, 32768, 2048, '["text"]', '["audio"]', '{"supports_json_schema":true}', 99, @associationVerified, '',
                    0.98765432, 1.23456789, 'page/full', @provider, 1904, false, '{"owner":"native page"}'),
                (1915, 1913, true, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'page/null', NULL, NULL, true, NULL);
            INSERT INTO "ModelProviderMappings" ("Id", "ModelAlias", "ProviderModelId", "ProviderId", "IsEnabled", "RoutingPriority", "RoutingWeight",
                "ProviderOptions", "CreatedAt", "UpdatedAt", "ModelProviderTypeAssociationId")
                SELECT 2000+n, CASE WHEN n <= 2 THEN 'page-000' ELSE 'page-'||lpad(n::text,3,'0') END,
                    'page/provider-'||n, 1901, n % 2 = 0, 1000-n, n / 100000000.0,
                    CASE WHEN n % 3 = 0 THEN NULL ELSE '{"ordinal":'||n||'}' END,
                    @instant + n * interval '1 second', @updated + n * interval '1 second', 1905
                FROM generate_series(1,105) AS n;
            INSERT INTO "ModelProviderMappings" ("Id", "ModelAlias", "ProviderModelId", "ProviderId", "IsEnabled", "RoutingPriority", "RoutingWeight",
                "ProviderOptions", "CreatedAt", "UpdatedAt", "ModelProviderTypeAssociationId") VALUES
                (2200, 'page-000', 'page/null', 1911, false, -99, 0, NULL, @instant, @updated, 1915);
            """;
        command.Parameters.AddWithValue("provider", (int)ProviderType.OpenRouter);
        command.Parameters.AddWithValue("instant", Instant);
        command.Parameters.AddWithValue("updated", Instant.AddMinutes(1));
        command.Parameters.AddWithValue("modelVerified", Instant.AddMinutes(2));
        command.Parameters.AddWithValue("associationVerified", Instant.AddMinutes(3));
        command.Parameters.AddWithValue("effective", Instant.AddDays(-1));
        command.Parameters.AddWithValue("expiry", Instant.AddDays(1));
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AssertGraph(ModelProviderMappingRuntimeRecord item, ModelProviderMappingRuntimeRecord baseline)
    {
        if (item.Id == 906)
        {
            Require(item.ModelAlias == baseline.ModelAlias && item.ProviderModelId == baseline.ProviderModelId &&
                item.ProviderId == baseline.ProviderId && item.Association.Id == baseline.Association.Id,
                "original mapping remains in the unfiltered page");
            return;
        }
        var nulls = item.Id == 2200;
        var ordinal = item.Id - 2000;
        Require(nulls || ordinal is >= 1 and <= 105, "only seeded mappings are returned");
        Require(item.ModelAlias == (nulls || ordinal <= 2 ? "page-000" : $"page-{ordinal:D3}") &&
            item.ProviderModelId == (nulls ? "page/null" : $"page/provider-{ordinal}") && item.ProviderId == (nulls ? 1911 : 1901) &&
            item.IsEnabled == (!nulls && ordinal % 2 == 0) && item.RoutingPriority == (nulls ? -99 : 1000 - ordinal) &&
            item.RoutingWeight == (nulls ? 0m : ordinal / 100000000m) &&
            item.ProviderOptions == (nulls || ordinal % 3 == 0 ? null : $"{{\"ordinal\":{ordinal}}}") &&
            item.CreatedAt == Instant.AddSeconds(nulls ? 0 : ordinal) && item.UpdatedAt == Instant.AddMinutes(1).AddSeconds(nulls ? 0 : ordinal),
            "all mapping scalars including disabled/null routes");
        var provider = item.Provider;
        Require(provider.Id == item.ProviderId && provider.ProviderType == ProviderType.OpenRouter &&
            provider.ProviderName == (nulls ? "null page provider" : "page provider 雪") &&
            provider.BaseUrl == (nulls ? null : "https://example.test/page") && !provider.IsEnabled &&
            provider.TrustProviderReportedCosts == !nulls && provider.ProviderCostMarkupMultiplier == (nulls ? 0.75000001m : 1.12500001m) &&
            provider.CreatedAt == Instant && provider.UpdatedAt == Instant.AddMinutes(1) &&
            (nulls ? provider.Settings is null : provider.Settings is { Count: 2 } && provider.Settings["region"] == "west" && provider.Settings["label"] == "O'Hare 雪"),
            "complete provider graph");
        var association = item.Association;
        Require(association.Id == (nulls ? 1915 : 1905) && association.ModelId == (nulls ? 1913 : 1903) && association.IsEnabled == nulls &&
            association.MaxInputTokens == (nulls ? null : 32768) && association.MaxOutputTokens == (nulls ? null : 2048) &&
            association.CapabilitySource == (nulls ? null : 99) && association.CapabilitiesLastVerifiedAt == (nulls ? null : Instant.AddMinutes(3)) &&
            association.ProviderVariation == (nulls ? null : "") && association.QualityScore == (nulls ? null : 0.98765432m) &&
            association.SpeedScore == (nulls ? null : 1.23456789m) && association.Identifier == (nulls ? "page/null" : "page/full") &&
            association.ProviderType == (nulls ? null : (int)ProviderType.OpenRouter) && association.ModelCostId == (nulls ? null : 1904) &&
            association.IsPrimary == nulls && association.Metadata == (nulls ? null : "{\"owner\":\"native page\"}"), "all association scalars");
        AssertJson(association.InputModalitiesJson, nulls ? null : "[\"text\"]");
        AssertJson(association.OutputModalitiesJson, nulls ? null : "[\"audio\"]");
        AssertJson(association.OperationalCapabilitiesJson, nulls ? null : "{\"supports_json_schema\":true}");
        var model = association.Model;
        Require(model.Id == association.ModelId && model.Name == (nulls ? "null page model" : "page model") &&
            model.Version == (nulls ? null : "v2") && model.Description == (nulls ? null : "model description") &&
            model.ModelCardUrl == (nulls ? null : "https://example.test/card") && model.ModelSeriesId == (nulls ? 1912 : 1902) &&
            model.SupportsVision == !nulls && !model.SupportsImageGeneration && model.SupportsVideoGeneration == !nulls && model.SupportsEmbeddings == nulls &&
            model.SupportsSpeechToText == !nulls && !model.SupportsTextToSpeech && model.SupportsRerank == !nulls && !model.SupportsChat &&
            model.SupportsFunctionCalling == !nulls && !model.SupportsStreaming && model.CapabilitySource == (nulls ? 0 : 77) &&
            model.CapabilitiesLastVerifiedAt == (nulls ? null : Instant.AddMinutes(2)) && model.TokenizerType == (nulls ? 0 : 42) &&
            model.MaxInputTokens == (nulls ? null : 65536) && model.MaxOutputTokens == (nulls ? null : 4096) && model.IsActive == nulls &&
            model.ModelParameters == (nulls ? null : "{\"temperature\":0.25}") && model.CreatedAt == Instant && model.UpdatedAt == Instant.AddMinutes(1),
            "complete canonical model graph");
        AssertJson(model.InputModalitiesJson, nulls ? null : "[\"text\",\"image\"]");
        AssertJson(model.OutputModalitiesJson, nulls ? null : "[\"audio\"]");
        var series = model.Series;
        Require(series.Id == model.ModelSeriesId && series.AuthorId == (nulls ? 0 : 17) && series.Name == (nulls ? "null page series" : "page series 雪") &&
            series.Description == (nulls ? null : "series description") && series.TokenizerType == (nulls ? 0 : 23) &&
            series.Parameters == (nulls ? "{}" : "{\"seed\":\"系列\"}"), "complete series graph");
        var cost = association.ModelCost;
        if (nulls) { Require(cost is null, "missing optional cost stays null"); return; }
        Require(cost is not null && cost.Id == 1904 && cost.CostName == "page cost" && cost.PricingModel == 123 &&
            cost.PricingConfiguration == "{\"mode\":\"fixture\"}" && cost.InputCostPerMillionTokens == 2.50000001m &&
            cost.OutputCostPerMillionTokens == 10.00000002m && cost.EmbeddingCostPerMillionTokens == 0.75000003m &&
            cost.CreatedAt == Instant && cost.UpdatedAt == Instant.AddMinutes(1) && cost.ModelType == "chat" && !cost.IsActive &&
            cost.EffectiveDate == Instant.AddDays(-1) && cost.ExpiryDate == Instant.AddDays(1) && cost.Description == "cost description" && cost.Priority == -7 &&
            cost.BatchProcessingMultiplier == 0.5m && !cost.SupportsBatchProcessing && cost.CachedInputCostPerMillionTokens == 0.25000004m &&
            cost.CachedInputWriteCostPerMillionTokens == 0.37500005m && cost.CostPerSearchUnit == 0.12500006m && cost.AudioCostPerMinute == 1.75000007m &&
            cost.AudioCostPerThousandCharacters == 2.25000008m && cost.ReasoningCostPerMillionTokens == 3.12500009m, "complete optional cost graph");
    }

    private static void AssertJson(string? actual, string? expected)
    {
        if (expected is null) { Require(actual is null, "null graph JSON"); return; }
        Require(actual is not null, "present graph JSON");
        using var actualDocument = JsonDocument.Parse(actual!);
        using var expectedDocument = JsonDocument.Parse(expected);
        Require(JsonElement.DeepEquals(actualDocument.RootElement, expectedDocument.RootElement), "complete graph JSON values");
    }

    private static async Task ExpectCallerCancellationAsync(Task pending, CancellationTokenSource caller)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException exception) when (caller.IsCancellationRequested)
        {
            Require(exception.CancellationToken == caller.Token, "cancellation preserves the exact caller token");
            return;
        }
        throw new InvalidOperationException("Native model page ignored caller cancellation.");
    }

    private static async Task ReapAsync(Task pending, Exception? primary)
    {
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (pending.IsCanceled) { }
        catch (Exception exception)
        {
            if (primary is not null && !ReferenceEquals(exception, primary))
                throw new AggregateException("Native model page work and drain failed.", primary, exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static async Task AssertOccupiedPoolCancellationAsync(NpgsqlDataSource source, NpgsqlModelProviderMappingRuntimeStore store,
        bool providerOnly, CancellationToken token)
    {
        var held = await source.OpenConnectionAsync(token);
        using var caller = new CancellationTokenSource();
        Task? pending = null;
        Exception? primary = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            pending = Read(store, providerOnly, 1, 10, caller.Token);
            Require(!pending.IsCompleted, "pagination waits while its only slot is leased");
            caller.Cancel();
            await ExpectCallerCancellationAsync(pending, caller);
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            caller.Cancel();
            await DrainAsync();
            // A failed first drain must still release the held slot, and a
            // newly unblocked operation must finish before source disposal.
            try { await held.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            await DrainAsync();
        }
        if (primary is not null)
        {
            if (cleanupFailures.Count != 0) throw new AggregateException("Native model page pool work and cleanup failed.", new[] { primary }.Concat(cleanupFailures));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanupFailures.Count != 0) throw new AggregateException("Native model page pool cleanup failed.", cleanupFailures);
        await AssertSlotReusableAsync(source, token);

        async Task DrainAsync()
        {
            if (pending is null) return;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (pending.IsCanceled) { }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
    }

    private static async Task AssertSlotReusableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection = await source.OpenConnectionAsync(bound.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Require(await command.ExecuteScalarAsync(bound.Token) is 1, "single pool slot is reusable");
    }

    private static async Task AssertSqlCancellationAsync(NpgsqlDataSource source, NpgsqlModelProviderMappingRuntimeStore store,
        string connectionString, bool providerOnly, bool countPhase, CancellationToken token)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        work.CancelAfter(TimeSpan.FromSeconds(20));
        int readerPid;
        string schema, application;
        await using (var warm = await source.OpenConnectionAsync(work.Token))
        await using (var command = warm.CreateCommand())
        {
            readerPid = warm.ProcessID;
            command.CommandText = "SELECT current_schema(), current_setting('application_name')";
            await using var reader = await command.ExecuteReaderAsync(work.Token);
            Require(await reader.ReadAsync(work.Token), "pooled reader identity");
            schema = reader.GetString(0); application = reader.GetString(1);
        }
        Require(schema.StartsWith(PersistenceProbeLifecycle.SchemaPrefix, StringComparison.Ordinal) &&
            schema.Length == PersistenceProbeLifecycle.SchemaPrefix.Length + 32 &&
            Guid.TryParseExact(schema.AsSpan(PersistenceProbeLifecycle.SchemaPrefix.Length), "N", out _), "exact caller-owned GUID schema");
        var relation = $"\"{schema}\".\"{(countPhase ? "ModelProviderMappings" : "Providers")}\"";
        var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = false, ApplicationName = "native_model_page_blocker", Timeout = 5, CommandTimeout = 5, CancellationTimeout = 1000 }.ConnectionString);
        NpgsqlTransaction? transaction = null;
        using var caller = new CancellationTokenSource();
        Task? pending = null;
        Exception? failure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            await blocker.OpenAsync(work.Token);
            transaction = await blocker.BeginTransactionAsync(work.Token);
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"LOCK TABLE {relation} IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(work.Token);
            }
            pending = Read(store, providerOnly, 1, 10, caller.Token);
            await WaitForSqlLockAsync(connectionString, relation, readerPid, application, blocker.ProcessID, countPhase, pending, work.Token);
            caller.Cancel();
            await ExpectCallerCancellationAsync(pending, caller);
            // The table remains locked: SELECT 1 proves the sole slot was released.
            await AssertSlotReusableAsync(source, work.Token);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            caller.Cancel();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (pending.IsCanceled) { }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(cleanup.Token); } catch (Exception exception) { cleanupFailures.Add(exception); }
                try { await transaction.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            try { await blocker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception exception) { cleanupFailures.Add(exception); }
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (pending.IsCanceled) { }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
        }
        if (failure is not null)
        {
            if (cleanupFailures.Count != 0) throw new AggregateException("Native model page SQL work and cleanup failed.", new[] { failure }.Concat(cleanupFailures));
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (cleanupFailures.Count != 0) throw new AggregateException("Native model page SQL cleanup failed.", cleanupFailures);
        await AssertSlotReusableAsync(source, token);
        var recovered = await ReadBoundedAsync(store, providerOnly, 1, 10, token);
        Require(recovered.TotalCount == (providerOnly ? 105 : 107) && recovered.Items.Select(item => item.Id).SequenceEqual(ExpectedIds(providerOnly).Take(10)),
            "page recovery after each observed SQL cancellation");
    }

    private static async Task WaitForSqlLockAsync(string connectionString, string relation, int readerPid,
        string application, int blockerPid, bool countPhase, Task pending, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = false, ApplicationName = "native_model_page_observer", Timeout = 5, CommandTimeout = 5, CancellationTimeout = 1000 }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity a JOIN pg_locks waiting ON waiting.pid = a.pid
            WHERE a.pid = @reader AND a.application_name = @application
                AND a.datid = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND a.state = 'active' AND a.wait_event_type = 'Lock' AND waiting.locktype = 'relation'
                AND waiting.relation = @relation::regclass AND waiting.mode = 'AccessShareLock' AND NOT waiting.granted
                AND @blocker = ANY(pg_blocking_pids(a.pid))
                AND ((@count AND a.query LIKE 'SELECT count(*)%') OR (NOT @count AND a.query LIKE '%mapping_alias%'))
                AND EXISTS (SELECT 1 FROM pg_locks held WHERE held.pid = @blocker AND held.relation = waiting.relation
                    AND held.mode = 'AccessExclusiveLock' AND held.granted))
            """;
        command.Parameters.AddWithValue("reader", readerPid); command.Parameters.AddWithValue("application", application);
        command.Parameters.AddWithValue("relation", relation); command.Parameters.AddWithValue("blocker", blockerPid);
        command.Parameters.AddWithValue("count", countPhase);
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("Native model page completed before its exact SQL lock wait was observed.");
            }
            if (await command.ExecuteScalarAsync(token) is true) return;
            await Task.Delay(25, token);
        }
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Native model pagination contract failed: {contract}.");
    }
}
