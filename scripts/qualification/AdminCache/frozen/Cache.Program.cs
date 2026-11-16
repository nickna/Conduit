using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZiggyCreatures.Caching.Fusion;

namespace Conduit.CacheMeasurements;

internal static class Program
{
    internal const int Models = 32;
    internal const int Associations = 8;
    private const string Source = "f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa";
    private static readonly DateTime Epoch = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
    private const string Parameters = "{\"temperature\":{\"type\":\"slider\",\"min\":0,\"max\":2,\"step\":0.1,\"default\":0.7},\"max_tokens\":{\"type\":\"number\",\"default\":8192},\"top_p\":{\"type\":\"slider\",\"default\":0.95}}";

    public static async Task<int> Main(string[] args)
    {
        if (JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("Reflection JSON must remain disabled.");
        var mode = args.SingleOrDefault() ?? throw new ArgumentException("Mode write or read required.");
        if (mode is not ("write" or "read")) throw new ArgumentException("Mode write or read required.");
        var redis = Required("CONDUIT_EXTENDED_CACHE_REDIS");
        var environment = Required("CONDUIT_EXTENDED_CACHE_ENVIRONMENT");
        var output = Path.GetFullPath(Required("CONDUIT_EXTENDED_CACHE_OUTPUT"));
        if (File.Exists(output)) throw new IOException("A new output path is required; previous evidence is immutable.");
        var warmups = int.Parse(Environment.GetEnvironmentVariable("CONDUIT_EXTENDED_CACHE_WARMUPS") ?? "100");
        var samples = int.Parse(Environment.GetEnvironmentVariable("CONDUIT_EXTENDED_CACHE_SAMPLES") ?? "1000");
        if (warmups < 1 || samples < 1 || samples > 10000) throw new ArgumentOutOfRangeException(nameof(samples));
        var report = new Report
        {
            Mode = mode, Environment = environment, SourceRevision = Source,
            Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(), ProcessId = Environment.ProcessId,
            Warmups = warmups, MeasuredPerState = samples, StartedAtUtc = DateTime.UtcNow,
            Fixture = new FixtureDescription
            {
                DiscoveryModelCount = Models, PricingAssociationCount = Associations,
                MappingSnapshotBytes = EncodeMapping(Mapping(42)).Length,
                PricingSnapshotBytes = EncodeCost(Cost(42)).Length,
                DiscoveryLogicalJsonBytes = JsonSerializer.SerializeToUtf8Bytes(Discovery(), ReportJson.Default.DiscoveryModelsResult).Length
            },
            ProductionAssemblies = new[] { typeof(FusionModelProviderMappingService).Assembly, typeof(ApplicationCacheOptions).Assembly, typeof(ApplicationCacheExtensions).Assembly, typeof(FusionCache).Assembly }
                .Distinct().Select(assembly => new AssemblyIdentity(assembly.GetName().Name!, assembly.GetName().Version?.ToString() ?? "", FileHash(assembly.Location))).ToArray()
        };
        try
        {
            await using var host = Host(redis, environment);
            var mappingInner = new MappingLoader { FailReads = mode == "read" };
            var costInner = new CostLoader { FailReads = mode == "read" };
            var mappings = MappingService(host, mappingInner);
            var costs = CostService(host, costInner);
            var discovery = host.GetRequiredService<IDiscoveryCacheService>();
            var payload = Discovery();
            var discoveryLoads = 0;
            Task<DiscoveryModelsResult> LoadDiscovery(CancellationToken _)
            {
                discoveryLoads++;
                if (mode == "read") throw new InvalidOperationException("Restarted L2 must not call the discovery loader.");
                return Task.FromResult(payload);
            }
            if (mode == "write")
            {
                report.Rows.AddRange(await WritePair("mapping", warmups, samples,
                    index => mappings.GetMappingByIdAsync(index + 100000), () => mappingInner.Loads,
                    (value, index) => ValidateMapping(value!, index + 100000), value => value!.Provider.Settings!["account"] = "caller-mutated"));
                report.Rows.AddRange(await WritePair("discovery", warmups, samples,
                    index => discovery.GetOrLoadAsync("complete-catalog-" + index, LoadDiscovery), () => discoveryLoads,
                    (value, _) => ValidateDiscovery(value), value => value.Data.Clear()));
                report.Rows.AddRange(await WritePair("pricing", warmups, samples,
                    index => costs.GetCostByIdAsync(index + 200000), () => costInner.Loads,
                    (value, index) => ValidateCost(value!, index + 200000), value =>
                    {
                        value!.InputCostPerMillionTokens = 99m;
                        value.ModelProviderTypeAssociations.First().Model.Name = "caller-mutated";
                    }));
                Check(mappingInner.Loads == warmups + samples && costInner.Loads == warmups + samples && discoveryLoads == warmups + samples, "Cold factory counts differ from exact key counts.");
            }
            else
            {
                report.Rows.Add(await ReadL2("mapping", warmups, samples,
                    index => mappings.GetMappingByIdAsync(index + 100000), () => mappingInner.Loads,
                    (value, index) => ValidateMapping(value!, index + 100000), value => value!.Provider.Settings!["account"] = "caller-mutated"));
                report.Rows.Add(await ReadL2("discovery", warmups, samples,
                    index => discovery.GetOrLoadAsync("complete-catalog-" + index, LoadDiscovery), () => discoveryLoads,
                    (value, _) => ValidateDiscovery(value), value => value.Data.Clear()));
                report.Rows.Add(await ReadL2("pricing", warmups, samples,
                    index => costs.GetCostByIdAsync(index + 200000), () => costInner.Loads,
                    (value, index) => ValidateCost(value!, index + 200000), value =>
                    {
                        value!.InputCostPerMillionTokens = 99m;
                        value.ModelProviderTypeAssociations.First().Model.Name = "caller-mutated";
                    }));
                Check(mappingInner.Loads == 0 && costInner.Loads == 0 && discoveryLoads == 0, "Restarted L2 attempted a repair load.");
            }
            report.FactoryCalls = new() { ["mapping"] = mappingInner.Loads, ["discovery"] = discoveryLoads, ["pricing"] = costInner.Loads };
            report.Succeeded = true;
        }
        catch (Exception exception)
        {
            report.Failure = exception.ToString();
        }
        report.CompletedAtUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(stream, report, ReportJson.Default.Report);
        Console.WriteLine($"{(report.Succeeded ? "PASS" : "FAIL")} {mode}: {report.Rows.Count} state rows; source={Source}; reflection=false");
        return report.Succeeded ? 0 : 1;
    }

    private static async Task<StateRow[]> WritePair<T>(string domain, int warmups, int count, Func<int, Task<T>> read, Func<int> loads, Action<T, int> validate, Action<T> mutate)
    {
        var cold = new StateRow(domain, "cold-distinct-key", count);
        var local = new StateRow(domain, "immediate-l1-owned-hit", count);
        for (var index = 0; index < warmups + count; index++)
        {
            var beforeLoads = loads();
            var start = Stopwatch.GetTimestamp();
            var coldSample = await Measure(() => read(index));
            var localSample = await Measure(() => read(index));
            var pairElapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Check(loads() - beforeLoads == 1, "Cold/L1 pair did not load exactly once.");
            validate(coldSample.Value, index);
            validate(localSample.Value, index);
            mutate(coldSample.Value);
            validate(localSample.Value, index);
            if (index < warmups) continue;
            // Mapping and pricing retain their actual production 100ms L1 cap.
            // Starting before cold lookup makes this conservative: a pair under 80ms
            // cannot expire the newly populated entry before the immediate second read.
            Check(pairElapsed < 80, "L1 sample state cannot be verified within the production 100ms TTL; retain failed invocation.");
            var sample = index - warmups;
            cold.Samples[sample] = new(sample, coldSample.Microseconds, coldSample.AllocatedBytes, 1);
            local.Samples[sample] = new(sample, localSample.Microseconds, localSample.AllocatedBytes, 0);
            local.MaximumColdToL1EndMilliseconds = Math.Max(local.MaximumColdToL1EndMilliseconds, pairElapsed);
        }
        cold.Finish(); local.Finish();
        return [cold, local];
    }

    private static async Task<StateRow> ReadL2<T>(string domain, int warmups, int count, Func<int, Task<T>> read, Func<int> loads, Action<T, int> validate, Action<T> mutate)
    {
        var row = new StateRow(domain, "independent-process-l2-distinct-key", count);
        for (var index = 0; index < warmups + count; index++)
        {
            var beforeLoads = loads();
            var measured = await Measure(() => read(index));
            Check(loads() == beforeLoads, "L2 attempted repair load.");
            validate(measured.Value, index);
            mutate(measured.Value);
            var owned = await read(index); // outside timing: prove mutation did not alter the cache
            validate(owned, index);
            Check(loads() == beforeLoads, "Ownership verification attempted repair load.");
            if (index >= warmups) row.Samples[index - warmups] = new(index - warmups, measured.Microseconds, measured.AllocatedBytes, 0);
        }
        row.Finish();
        return row;
    }

    private static async Task<Measurement<T>> Measure<T>(Func<Task<T>> action)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var value = await action();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        return new(value, elapsed, allocated);
    }

    internal static ServiceProvider Host(string redis, string environment)
    {
        // Same production registration/composition as CacheProbe.DiscoveryDomainProbe.Host.
        // Increase only the documented healthy Redis timeout to its supported 1s ceiling;
        // leave production cache L1/L2 TTLs and generated serializer behavior intact.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationCache:Environment"] = environment,
            ["ApplicationCache:DistributedReadTimeout"] = "00:00:01"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddConduitApplicationCache(configuration, "probe", redis);
        services.AddDiscoveryCache(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static FusionModelProviderMappingService MappingService(ServiceProvider host, IModelProviderMappingService inner) => new(inner,
        host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey), host.GetRequiredService<ApplicationCacheOptions>(),
        host.GetRequiredService<ApplicationCacheGeneration>(), new ModelMappingCacheInvalidator(host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey), host.GetRequiredService<ApplicationCacheGeneration>()),
        NullLogger<FusionModelProviderMappingService>.Instance);
    private static FusionModelCostService CostService(ServiceProvider host, IModelCostService inner) => new(inner,
        host.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey), host.GetRequiredService<ApplicationCacheOptions>(),
        host.GetRequiredService<ApplicationCacheGeneration>(), TimeProvider.System, NullLogger<FusionModelCostService>.Instance);

    internal static ModelProviderMapping Mapping(int id) => new()
    {
        Id = id, ModelAlias = "complete-model-" + id, ProviderModelId = "provider/model-" + id, ProviderId = 7,
        ModelProviderTypeAssociationId = id, IsEnabled = true, RoutingPriority = 12, RoutingWeight = 1.25m,
        ProviderOptions = "{\"provider\":{\"order\":[\"fixture\"],\"allow_fallbacks\":false}}", CreatedAt = Epoch, UpdatedAt = Epoch,
        Provider = new() { Id = 7, ProviderType = ProviderType.OpenAI, ProviderName = "Fixture provider", BaseUrl = "https://fixture.invalid/v1",
            Settings = new() { ["account"] = "fixture", ["region"] = "us-west", ["api-version"] = "2026-11-14", ["organization"] = "fixture-org" },
            IsEnabled = true, TrustProviderReportedCosts = true, ProviderCostMarkupMultiplier = 1.05m, CreatedAt = Epoch, UpdatedAt = Epoch },
        ModelProviderTypeAssociation = Association(id, id, ScalarCost(id))
    };
    private static Model Model(int id) => new()
    {
        Id = id, Name = "Complete model " + id, Version = "2026-11", Description = "Fixture model with rich routing/pricing/capability metadata café 雪 🔎 " + new string('x', 128),
        ModelSeriesId = 9, Series = new() { Id = 9, AuthorId = 3, Name = "Fixture series", Description = "Full inherited UI parameter contract", Parameters = Parameters },
        SupportsChat = true, SupportsStreaming = true, SupportsFunctionCalling = true, SupportsVision = true, SupportsEmbeddings = true, SupportsImageGeneration = true,
        MaxInputTokens = 131072, MaxOutputTokens = 8192, InputModalitiesJson = "[\"text\",\"image\"]", OutputModalitiesJson = "[\"text\"]",
        IsActive = true, CreatedAt = Epoch, UpdatedAt = Epoch
    };
    private static ModelProviderTypeAssociation Association(int id, int model, ModelCost cost) => new()
    {
        Id = id, ModelId = model, IsEnabled = true, Model = Model(model), ModelCostId = cost.Id, ModelCost = cost,
        Identifier = "provider/model-" + model, Provider = ProviderType.OpenAI, IsPrimary = true,
        MaxInputTokens = 131072, MaxOutputTokens = 8192, QualityScore = 0.9m, SpeedScore = 0.8m, ProviderVariation = "complete",
        InputModalitiesJson = "[\"text\",\"image\"]", OutputModalitiesJson = "[\"text\"]",
        OperationalCapabilitiesJson = "{\"tools\":true,\"streaming\":true}", Metadata = "{\"region\":\"us-west\",\"context\":131072}", CapabilitiesLastVerifiedAt = Epoch
    };
    private static ModelCost ScalarCost(int id) => new()
    {
        Id = id, CostName = "Fixture standard pricing", InputCostPerMillionTokens = 0.25m, OutputCostPerMillionTokens = 0.75m,
        EmbeddingCostPerMillionTokens = 0.1m, CachedInputCostPerMillionTokens = 0.125m, CachedInputWriteCostPerMillionTokens = 0.2m,
        CostPerSearchUnit = 0.005m, AudioCostPerMinute = 0.006m, AudioCostPerThousandCharacters = 0.01m, ReasoningCostPerMillionTokens = 0.8m,
        SupportsBatchProcessing = true, BatchProcessingMultiplier = 0.5m, Priority = 10, ModelType = "chat", IsActive = true,
        EffectiveDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), ExpiryDate = new DateTime(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        PricingConfiguration = "{\"defaultRate\":0.25,\"rules\":[{\"rate\":0.5,\"conditions\":{\"resolution\":\"1024x1024\"}}]}",
        Description = "Complete pricing scalar fixture", CreatedAt = Epoch, UpdatedAt = Epoch
    };
    internal static ModelCost Cost(int id)
    {
        var cost = ScalarCost(id);
        cost.ModelProviderTypeAssociations = Enumerable.Range(0, Associations).Select(index => Association(id * 10 + index, id * 10 + index, cost)).ToList();
        return cost;
    }
    internal static DiscoveryModelsResult Discovery()
    {
        var values = new List<JsonElement>();
        for (var index = 0; index < Models; index++)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteString("id", "complete-model-" + index); writer.WriteString("object", "model"); writer.WriteString("owned_by", "fixture");
                writer.WriteString("description", "Full catalog fixture café 雪 🔎 " + new string('x', 128));
                writer.WriteStartObject("capabilities"); writer.WriteBoolean("supports_chat", true); writer.WriteBoolean("supports_streaming", true); writer.WriteBoolean("supports_function_calling", true);
                writer.WriteBoolean("supports_vision", true); writer.WriteNumber("max_input_tokens", 131072); writer.WriteNumber("max_output_tokens", 8192); writer.WriteEndObject();
                writer.WriteStartObject("pricing"); writer.WriteNumber("input_cost", 0.25m); writer.WriteNumber("output_cost", 0.75m); writer.WriteNumber("cached_input_cost", 0.125m); writer.WriteString("currency", "USD"); writer.WriteEndObject();
                writer.WritePropertyName("parameters"); using var parameters = JsonDocument.Parse(Parameters); parameters.RootElement.WriteTo(writer);
                writer.WriteStartObject("provider"); writer.WriteString("name", "fixture"); writer.WriteString("identifier", "provider/model-" + index); writer.WriteString("region", "us-west"); writer.WriteEndObject();
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(stream.ToArray()); values.Add(document.RootElement.Clone());
        }
        return new() { Count = Models, Data = values, CapabilityFilter = "chat:with_pricing", CachedAt = Epoch, PricingRefreshAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    }

    private static byte[] EncodeMapping(ModelProviderMapping value) => JsonSerializer.SerializeToUtf8Bytes(MappingCacheSnapshot.From(value), ReportJson.Default.MappingCacheSnapshot);
    private static byte[] EncodeCost(ModelCost value) => JsonSerializer.SerializeToUtf8Bytes(CostCacheSnapshot.From(value), ReportJson.Default.CostCacheSnapshot);
    private static void ValidateMapping(ModelProviderMapping value, int id) => Check(EncodeMapping(value).AsSpan().SequenceEqual(EncodeMapping(Mapping(id))), "Mapping graph changed or lost fields.");
    private static void ValidateCost(ModelCost value, int id) => Check(EncodeCost(value).AsSpan().SequenceEqual(EncodeCost(Cost(id))), "Pricing graph changed or lost fields.");
    private static readonly DiscoveryModelsResult ExpectedDiscovery = Discovery();
    private static void ValidateDiscovery(DiscoveryModelsResult value)
    {
        Check(value.Count == Models && value.Data.Count == Models && value.CapabilityFilter == ExpectedDiscovery.CapabilityFilter && value.PricingRefreshAt == ExpectedDiscovery.PricingRefreshAt, "Discovery count/list/filter/pricing deadline changed.");
        for (var index = 0; index < Models; index++) Check(JsonElement.DeepEquals(value.Data[index], ExpectedDiscovery.Data[index]), "Discovery model metadata changed.");
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new ArgumentException(name + " is required.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class MappingLoader : IModelProviderMappingService
    {
        public int Loads; public bool FailReads;
        public Task<ModelProviderMapping?> GetMappingByIdAsync(int id) { Loads++; Check(!FailReads, "Mapping L2 loader invoked."); return Task.FromResult<ModelProviderMapping?>(Mapping(id)); }
        public Task<ModelProviderMapping?> GetMappingByModelAliasAsync(string alias) => throw new NotSupportedException();
        public Task<List<ModelProviderMapping>> GetAllMappingsAsync() => throw new NotSupportedException();
        public Task<List<ModelProviderMapping>> GetMappingsByModelAliasAsync(string alias) => throw new NotSupportedException();
        public Task AddMappingAsync(ModelProviderMapping value) => throw new NotSupportedException();
        public Task UpdateMappingAsync(ModelProviderMapping value) => throw new NotSupportedException();
        public Task DeleteMappingAsync(int id) => throw new NotSupportedException();
        public Task<(bool success, string? errorMessage, ModelProviderMapping? createdMapping)> ValidateAndCreateMappingAsync(ModelProviderMapping mapping) => throw new NotSupportedException();
        public Task<(bool success, string? errorMessage)> ValidateAndUpdateMappingAsync(int id, ModelProviderMapping mapping) => throw new NotSupportedException();
        public Task<bool> ProviderExistsByIdAsync(int id) => throw new NotSupportedException();
        public Task<List<(int Id, string ProviderName)>> GetAvailableProvidersAsync() => throw new NotSupportedException();
    }
    private sealed class CostLoader : IModelCostService
    {
        public int Loads; public bool FailReads;
        public Task<ModelCost?> GetCostByIdAsync(int id, CancellationToken token = default) { Loads++; Check(!FailReads, "Pricing L2 loader invoked."); return Task.FromResult<ModelCost?>(Cost(id)); }
        public Task<ModelCost?> GetCostForModelAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
        public Task<List<ModelCost>> ListModelCostsAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task AddModelCostAsync(ModelCost value, CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> UpdateModelCostAsync(ModelCost value, CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> DeleteModelCostAsync(int id, CancellationToken token = default) => throw new NotSupportedException();
        public Task ClearCacheAsync(CancellationToken token = default) => throw new NotSupportedException();
    }
}

internal sealed record Measurement<T>(T Value, double Microseconds, long AllocatedBytes);
internal sealed record Sample(int Index, double Microseconds, long ProcessWideAllocatedBytes, int BusinessLoaderCalls);
internal sealed record AssemblyIdentity(string Name, string Version, string Sha256);
internal sealed record Distribution(double Minimum, double P50, double P95, double P99, double Maximum, double Mean)
{
    internal static Distribution From(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        double Quantile(double p) => values[Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1)];
        return new(values[0], Quantile(0.5), Quantile(0.95), Quantile(0.99), values[^1], values.Average());
    }
}
internal sealed class StateRow(string domain, string state, int count)
{
    public string Domain { get; set; } = domain;
    public string State { get; set; } = state;
    public Sample[] Samples { get; set; } = new Sample[count];
    public Distribution? LatencyMicroseconds { get; set; }
    public Distribution? ProcessWideAllocatedBytes { get; set; }
    public double MaximumColdToL1EndMilliseconds { get; set; }
    public int BusinessLoaderCalls { get; set; }
    internal void Finish()
    {
        if (Samples.Any(sample => sample is null)) throw new InvalidOperationException("Incomplete samples.");
        LatencyMicroseconds = Distribution.From(Samples.Select(sample => sample.Microseconds));
        ProcessWideAllocatedBytes = Distribution.From(Samples.Select(sample => (double)sample.ProcessWideAllocatedBytes));
        BusinessLoaderCalls = Samples.Sum(sample => sample.BusinessLoaderCalls);
    }
}
internal sealed class FixtureDescription
{
    public int DiscoveryModelCount { get; set; }
    public int PricingAssociationCount { get; set; }
    public int MappingSnapshotBytes { get; set; }
    public int PricingSnapshotBytes { get; set; }
    public int DiscoveryLogicalJsonBytes { get; set; }
}
internal sealed class Report
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceRevision { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Framework { get; set; } = "";
    public string OS { get; set; } = "";
    public string OSArchitecture { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public int ProcessId { get; set; }
    public int Warmups { get; set; }
    public int MeasuredPerState { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public FixtureDescription Fixture { get; set; } = new();
    public AssemblyIdentity[] ProductionAssemblies { get; set; } = [];
    public List<StateRow> Rows { get; set; } = [];
    public Dictionary<string, int> FactoryCalls { get; set; } = [];
    public bool Succeeded { get; set; }
    public string? Failure { get; set; }
    public string AllocationBoundary { get; set; } = "GC.GetTotalAllocatedBytes(precise:true) process-wide before/after each awaited cache operation; includes background work and runtime noise. Validation, report arrays and expected-fixture comparison occur outside that interval. This is not thread-local allocation or HTTP-server allocation.";
    public string TimingBoundary { get; set; } = "Awaited actual production cache service lookup only. Cold includes synthetic populated business-loader construction/cache serialization/Redis write; no PostgreSQL I/O is measured. L1 immediately follows the same unique cold key; L2 reads each unique previously seeded key in a fresh reader process with forbidden loaders.";
    public string WarmupBoundary { get; set; } = "100 distinct-key operation warmups per domain/state, followed by 1000 measured distinct-key pairs/reads. Payload L1 TTLs unchanged (mapping/pricing100ms, discovery default). Reader ownership re-read is outside timing.";
    public string Limitations { get; set; } = "Deterministic populated fixtures, not production catalog distribution; sequential single-request cache latency not HTTP concurrency/throughput. p99 from1000 samples is exploratory. Synchronous validation may affect later GC state although its allocations are outside each measurement. Windows-x64 measurements do not establish Linux or ARM64 performance.";
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Report))]
[JsonSerializable(typeof(MappingCacheSnapshot))]
[JsonSerializable(typeof(CostCacheSnapshot))]
[JsonSerializable(typeof(DiscoveryModelsResult))]
internal partial class ReportJson : JsonSerializerContext;
