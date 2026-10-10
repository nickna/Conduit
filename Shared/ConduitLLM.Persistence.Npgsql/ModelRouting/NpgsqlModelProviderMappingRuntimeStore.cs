using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql store for Gateway model routing and route policy.
/// </summary>
public sealed class NpgsqlModelProviderMappingRuntimeStore : IModelProviderMappingRuntimeStore
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private const string SelectCost = """
        SELECT
            cost."Id" AS cost_id,
            cost."CostName" AS cost_name,
            cost."PricingModel" AS cost_pricing_model,
            cost."PricingConfiguration" AS cost_pricing_configuration,
            cost."InputCostPerMillionTokens" AS cost_input,
            cost."OutputCostPerMillionTokens" AS cost_output,
            cost."EmbeddingCostPerMillionTokens" AS cost_embedding,
            cost."CreatedAt" AS cost_created_at,
            cost."UpdatedAt" AS cost_updated_at,
            cost."ModelType" AS cost_model_type,
            cost."IsActive" AS cost_active,
            cost."EffectiveDate" AS cost_effective_at,
            cost."ExpiryDate" AS cost_expires_at,
            cost."Description" AS cost_description,
            cost."Priority" AS cost_priority,
            cost."BatchProcessingMultiplier" AS cost_batch_multiplier,
            cost."SupportsBatchProcessing" AS cost_supports_batch,
            cost."CachedInputCostPerMillionTokens" AS cost_cached_input,
            cost."CachedInputWriteCostPerMillionTokens" AS cost_cached_write,
            cost."CostPerSearchUnit" AS cost_search_unit,
            cost."AudioCostPerMinute" AS cost_audio_minute,
            cost."AudioCostPerThousandCharacters" AS cost_audio_characters,
            cost."ReasoningCostPerMillionTokens" AS cost_reasoning
        FROM "ModelCosts" AS cost
        """;

    private const string SelectRuntimeGraph = """
        SELECT
            mapping."Id" AS mapping_id,
            mapping."ModelAlias" AS mapping_alias,
            mapping."ProviderModelId" AS mapping_provider_model_id,
            mapping."ProviderId" AS mapping_provider_id,
            mapping."IsEnabled" AS mapping_enabled,
            mapping."RoutingPriority" AS mapping_priority,
            mapping."RoutingWeight" AS mapping_weight,
            mapping."ProviderOptions" AS mapping_options,
            mapping."CreatedAt" AS mapping_created_at,
            mapping."UpdatedAt" AS mapping_updated_at,
            provider."ProviderType" AS provider_type,
            provider."ProviderName" AS provider_name,
            provider."BaseUrl" AS provider_base_url,
            provider."Settings"::text AS provider_settings,
            provider."IsEnabled" AS provider_enabled,
            provider."TrustProviderReportedCosts" AS provider_trust_costs,
            provider."ProviderCostMarkupMultiplier" AS provider_markup,
            provider."CreatedAt" AS provider_created_at,
            provider."UpdatedAt" AS provider_updated_at,
            association."Id" AS association_id,
            association."ModelId" AS association_model_id,
            association."IsEnabled" AS association_enabled,
            association."MaxInputTokens" AS association_max_input,
            association."MaxOutputTokens" AS association_max_output,
            association."InputModalities"::text AS association_input_modalities,
            association."OutputModalities"::text AS association_output_modalities,
            association."OperationalCapabilities"::text AS association_operational_capabilities,
            association."CapabilitySource" AS association_capability_source,
            association."CapabilitiesLastVerifiedAt" AS association_capabilities_verified_at,
            association."ProviderVariation" AS association_variation,
            association."QualityScore" AS association_quality,
            association."SpeedScore" AS association_speed,
            association."Identifier" AS association_identifier,
            association."Provider" AS association_provider_type,
            association."ModelCostId" AS association_cost_id,
            association."IsPrimary" AS association_primary,
            association."Metadata" AS association_metadata,
            model."Id" AS model_id,
            model."Name" AS model_name,
            model."Version" AS model_version,
            model."Description" AS model_description,
            model."ModelCardUrl" AS model_card_url,
            model."ModelSeriesId" AS model_series_id,
            model."SupportsVision" AS model_supports_vision,
            model."SupportsImageGeneration" AS model_supports_image_generation,
            model."SupportsVideoGeneration" AS model_supports_video_generation,
            model."SupportsEmbeddings" AS model_supports_embeddings,
            model."SupportsSpeechToText" AS model_supports_speech_to_text,
            model."SupportsTextToSpeech" AS model_supports_text_to_speech,
            model."SupportsRerank" AS model_supports_rerank,
            model."SupportsChat" AS model_supports_chat,
            model."SupportsFunctionCalling" AS model_supports_function_calling,
            model."SupportsStreaming" AS model_supports_streaming,
            model."InputModalities"::text AS model_input_modalities,
            model."OutputModalities"::text AS model_output_modalities,
            model."CapabilitySource" AS model_capability_source,
            model."CapabilitiesLastVerifiedAt" AS model_capabilities_verified_at,
            model."TokenizerType" AS model_tokenizer_type,
            model."MaxInputTokens" AS model_max_input,
            model."MaxOutputTokens" AS model_max_output,
            model."IsActive" AS model_active,
            model."Parameters" AS model_parameters,
            model."CreatedAt" AS model_created_at,
            model."UpdatedAt" AS model_updated_at,
            series."Id" AS series_id,
            series."AuthorId" AS series_author_id,
            series."Name" AS series_name,
            series."Description" AS series_description,
            series."TokenizerType" AS series_tokenizer_type,
            series."Parameters" AS series_parameters,
            cost."Id" AS cost_id,
            cost."CostName" AS cost_name,
            cost."PricingModel" AS cost_pricing_model,
            cost."PricingConfiguration" AS cost_pricing_configuration,
            cost."InputCostPerMillionTokens" AS cost_input,
            cost."OutputCostPerMillionTokens" AS cost_output,
            cost."EmbeddingCostPerMillionTokens" AS cost_embedding,
            cost."CreatedAt" AS cost_created_at,
            cost."UpdatedAt" AS cost_updated_at,
            cost."ModelType" AS cost_model_type,
            cost."IsActive" AS cost_active,
            cost."EffectiveDate" AS cost_effective_at,
            cost."ExpiryDate" AS cost_expires_at,
            cost."Description" AS cost_description,
            cost."Priority" AS cost_priority,
            cost."BatchProcessingMultiplier" AS cost_batch_multiplier,
            cost."SupportsBatchProcessing" AS cost_supports_batch,
            cost."CachedInputCostPerMillionTokens" AS cost_cached_input,
            cost."CachedInputWriteCostPerMillionTokens" AS cost_cached_write,
            cost."CostPerSearchUnit" AS cost_search_unit,
            cost."AudioCostPerMinute" AS cost_audio_minute,
            cost."AudioCostPerThousandCharacters" AS cost_audio_characters,
            cost."ReasoningCostPerMillionTokens" AS cost_reasoning
        FROM "ModelProviderMappings" AS mapping
        INNER JOIN "Providers" AS provider ON provider."Id" = mapping."ProviderId"
        INNER JOIN "ModelIdentifiers" AS association
            ON association."Id" = mapping."ModelProviderTypeAssociationId"
        INNER JOIN "Models" AS model ON model."Id" = association."ModelId"
        INNER JOIN "ModelSeries" AS series ON series."Id" = model."ModelSeriesId"
        LEFT JOIN "ModelCosts" AS cost ON cost."Id" = association."ModelCostId"
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlModelProviderMappingRuntimeStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<ModelProviderMappingRuntimeRecord?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var mappings = await ReadMappingsAsync(
            $"{SelectRuntimeGraph} WHERE mapping.\"Id\" = @id",
            command => command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id),
            cancellationToken);
        return mappings.Count == 0 ? null : mappings[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelProviderMappingRuntimeRecord>> GetByAliasAsync(
        string modelAlias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        return await ReadMappingsAsync(
            $"{SelectRuntimeGraph} WHERE lower(mapping.\"ModelAlias\") = lower(@modelAlias) " +
            "ORDER BY mapping.\"RoutingPriority\", mapping.\"Id\"",
            command => command.Parameters.AddWithValue("modelAlias", NpgsqlDbType.Varchar, modelAlias),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModelProviderMappingRuntimePage> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ReadPageAsync(providerId: null, page, pageSize, cancellationToken);

    /// <inheritdoc />
    public Task<ModelProviderMappingRuntimePage> GetByProviderPaginatedAsync(
        int providerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ReadPageAsync(providerId, page, pageSize, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelProviderMappingRuntimeRecord>> GetByModelIdAsync(
        int modelId,
        CancellationToken cancellationToken = default) =>
        await ReadMappingsAsync(
            $"{SelectRuntimeGraph} WHERE association.\"ModelId\" = @modelId " +
            "ORDER BY mapping.\"ModelAlias\", mapping.\"Id\"",
            command => command.Parameters.AddWithValue("modelId", NpgsqlDbType.Integer, modelId),
            cancellationToken);

    /// <inheritdoc />
    public async Task<int?> GetCanonicalModelIdForAssociationAsync(
        int associationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"ModelId\" FROM \"ModelIdentifiers\" WHERE \"Id\" = @associationId";
        command.Parameters.AddWithValue("associationId", NpgsqlDbType.Integer, associationId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt32(value);
    }

    /// <inheritdoc />
    public async Task<ModelRoutePolicyRuntimeRecord?> GetRoutePolicyAsync(
        string modelAlias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ModelAlias", "Strategy", "CostWeight", "SpeedWeight",
                "QualityWeight", "CacheAffinityEnabled", "AffinityTtlSeconds",
                "MaxAffinityScorePenalty", "IsEnabled", "CreatedAt", "UpdatedAt"
            FROM "ModelRoutePolicies"
            WHERE "ModelAlias" = @modelAlias
            """;
        command.Parameters.AddWithValue("modelAlias", NpgsqlDbType.Varchar, modelAlias);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ModelRoutePolicyRuntimeRecord
        {
            Id = reader.GetInt32(0),
            ModelAlias = reader.GetString(1),
            Strategy = reader.GetString(2),
            CostWeight = reader.GetDecimal(3),
            SpeedWeight = reader.GetDecimal(4),
            QualityWeight = reader.GetDecimal(5),
            CacheAffinityEnabled = reader.GetBoolean(6),
            AffinityTtlSeconds = reader.GetInt32(7),
            MaxAffinityScorePenalty = reader.GetDecimal(8),
            IsEnabled = reader.GetBoolean(9),
            CreatedAt = reader.GetDateTime(10),
            UpdatedAt = reader.GetDateTime(11)
        };
    }

    /// <inheritdoc />
    public async Task<ModelCostRuntimeRecord?> GetModelCostByIdAsync(
        int modelCostId,
        CancellationToken cancellationToken = default) =>
        await ReadCostAsync(
            $"{SelectCost} WHERE cost.\"Id\" = @modelCostId",
            command => command.Parameters.AddWithValue(
                "modelCostId",
                NpgsqlDbType.Integer,
                modelCostId),
            cancellationToken);

    /// <inheritdoc />
    public async Task<ModelCostRuntimeRecord?> GetModelCostForIdentifierAsync(
        string modelIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentifier);
        return await ReadCostAsync(
            $"""
            {SelectCost}
            INNER JOIN "ModelIdentifiers" AS association
                ON association."ModelCostId" = cost."Id"
            WHERE association."Identifier" = @modelIdentifier
              AND association."IsEnabled" = true
              AND cost."IsActive" = true
              AND cost."EffectiveDate" <= @now
              AND (cost."ExpiryDate" IS NULL OR cost."ExpiryDate" > @now)
            ORDER BY cost."Priority" DESC, cost."EffectiveDate" DESC
            LIMIT 1
            """,
            command =>
            {
                command.Parameters.AddWithValue(
                    "modelIdentifier",
                    NpgsqlDbType.Varchar,
                    modelIdentifier);
                command.Parameters.AddWithValue(
                    "now",
                    NpgsqlDbType.TimestampTz,
                    DateTime.UtcNow);
            },
            cancellationToken);
    }

    private async Task<ModelProviderMappingRuntimePage> ReadPageAsync(
        int? providerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePagination(page, pageSize);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = providerId.HasValue
            ? "SELECT count(*) FROM \"ModelProviderMappings\" WHERE \"ProviderId\" = @providerId"
            : "SELECT count(*) FROM \"ModelProviderMappings\"";
        if (providerId.HasValue)
        {
            countCommand.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId.Value);
        }
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        var filter = providerId.HasValue ? "WHERE mapping.\"ProviderId\" = @providerId " : string.Empty;
        var mappings = await ReadMappingsAsync(
            $"{SelectRuntimeGraph} {filter}" +
            "ORDER BY mapping.\"ModelAlias\", mapping.\"Id\" OFFSET @offset LIMIT @limit",
            command =>
            {
                if (providerId.HasValue)
                {
                    command.Parameters.AddWithValue("providerId", NpgsqlDbType.Integer, providerId.Value);
                }
                command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, (page - 1) * pageSize);
                command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize);
            },
            cancellationToken);
        return new ModelProviderMappingRuntimePage(mappings, totalCount);
    }

    private async Task<List<ModelProviderMappingRuntimeRecord>> ReadMappingsAsync(
        string sql,
        Action<NpgsqlCommand>? addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        addParameters?.Invoke(command);

        var mappings = new List<ModelProviderMappingRuntimeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mappings.Add(ReadMapping(reader));
        }
        return mappings;
    }

    private async Task<ModelCostRuntimeRecord?> ReadCostAsync(
        string sql,
        Action<NpgsqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        addParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCost(reader) : null;
    }

    private static ModelProviderMappingRuntimeRecord ReadMapping(NpgsqlDataReader reader) => new()
    {
        Id = GetInt32(reader, "mapping_id"),
        ModelAlias = GetString(reader, "mapping_alias"),
        ProviderModelId = GetString(reader, "mapping_provider_model_id"),
        ProviderId = GetInt32(reader, "mapping_provider_id"),
        IsEnabled = GetBoolean(reader, "mapping_enabled"),
        RoutingPriority = GetInt32(reader, "mapping_priority"),
        RoutingWeight = GetDecimal(reader, "mapping_weight"),
        ProviderOptions = GetNullableString(reader, "mapping_options"),
        CreatedAt = GetDateTime(reader, "mapping_created_at"),
        UpdatedAt = GetDateTime(reader, "mapping_updated_at"),
        Provider = ReadProvider(reader),
        Association = ReadAssociation(reader)
    };

    private static Provider ReadProvider(NpgsqlDataReader reader) => new()
    {
        Id = GetInt32(reader, "mapping_provider_id"),
        ProviderType = (ProviderType)GetInt32(reader, "provider_type"),
        ProviderName = GetString(reader, "provider_name"),
        BaseUrl = GetNullableString(reader, "provider_base_url"),
        Settings = DeserializeSettings(GetNullableString(reader, "provider_settings")),
        IsEnabled = GetBoolean(reader, "provider_enabled"),
        TrustProviderReportedCosts = GetBoolean(reader, "provider_trust_costs"),
        ProviderCostMarkupMultiplier = GetDecimal(reader, "provider_markup"),
        CreatedAt = GetDateTime(reader, "provider_created_at"),
        UpdatedAt = GetDateTime(reader, "provider_updated_at")
    };

    private static ModelProviderTypeAssociationRuntimeRecord ReadAssociation(
        NpgsqlDataReader reader) => new()
    {
        Id = GetInt32(reader, "association_id"),
        ModelId = GetInt32(reader, "association_model_id"),
        IsEnabled = GetBoolean(reader, "association_enabled"),
        MaxInputTokens = GetNullableInt32(reader, "association_max_input"),
        MaxOutputTokens = GetNullableInt32(reader, "association_max_output"),
        InputModalitiesJson = GetNullableString(reader, "association_input_modalities"),
        OutputModalitiesJson = GetNullableString(reader, "association_output_modalities"),
        OperationalCapabilitiesJson = GetNullableString(reader, "association_operational_capabilities"),
        CapabilitySource = GetNullableInt32(reader, "association_capability_source"),
        CapabilitiesLastVerifiedAt = GetNullableDateTime(reader, "association_capabilities_verified_at"),
        ProviderVariation = GetNullableString(reader, "association_variation"),
        QualityScore = GetNullableDecimal(reader, "association_quality"),
        SpeedScore = GetNullableDecimal(reader, "association_speed"),
        Identifier = GetString(reader, "association_identifier"),
        ProviderType = GetNullableInt32(reader, "association_provider_type"),
        ModelCostId = GetNullableInt32(reader, "association_cost_id"),
        IsPrimary = GetBoolean(reader, "association_primary"),
        Metadata = GetNullableString(reader, "association_metadata"),
        Model = ReadModel(reader),
        ModelCost = IsNull(reader, "cost_id") ? null : ReadCost(reader)
    };

    private static ModelRuntimeRecord ReadModel(NpgsqlDataReader reader) => new()
    {
        Id = GetInt32(reader, "model_id"),
        Name = GetString(reader, "model_name"),
        Version = GetNullableString(reader, "model_version"),
        Description = GetNullableString(reader, "model_description"),
        ModelCardUrl = GetNullableString(reader, "model_card_url"),
        ModelSeriesId = GetInt32(reader, "model_series_id"),
        SupportsVision = GetBoolean(reader, "model_supports_vision"),
        SupportsImageGeneration = GetBoolean(reader, "model_supports_image_generation"),
        SupportsVideoGeneration = GetBoolean(reader, "model_supports_video_generation"),
        SupportsEmbeddings = GetBoolean(reader, "model_supports_embeddings"),
        SupportsSpeechToText = GetBoolean(reader, "model_supports_speech_to_text"),
        SupportsTextToSpeech = GetBoolean(reader, "model_supports_text_to_speech"),
        SupportsRerank = GetBoolean(reader, "model_supports_rerank"),
        SupportsChat = GetBoolean(reader, "model_supports_chat"),
        SupportsFunctionCalling = GetBoolean(reader, "model_supports_function_calling"),
        SupportsStreaming = GetBoolean(reader, "model_supports_streaming"),
        InputModalitiesJson = GetNullableString(reader, "model_input_modalities"),
        OutputModalitiesJson = GetNullableString(reader, "model_output_modalities"),
        CapabilitySource = GetInt32(reader, "model_capability_source"),
        CapabilitiesLastVerifiedAt = GetNullableDateTime(reader, "model_capabilities_verified_at"),
        TokenizerType = GetInt32(reader, "model_tokenizer_type"),
        MaxInputTokens = GetNullableInt32(reader, "model_max_input"),
        MaxOutputTokens = GetNullableInt32(reader, "model_max_output"),
        IsActive = GetBoolean(reader, "model_active"),
        ModelParameters = GetNullableString(reader, "model_parameters"),
        CreatedAt = GetDateTime(reader, "model_created_at"),
        UpdatedAt = GetDateTime(reader, "model_updated_at"),
        Series = new ModelSeriesRuntimeRecord
        {
            Id = GetInt32(reader, "series_id"),
            AuthorId = GetInt32(reader, "series_author_id"),
            Name = GetString(reader, "series_name"),
            Description = GetNullableString(reader, "series_description"),
            TokenizerType = GetInt32(reader, "series_tokenizer_type"),
            Parameters = GetString(reader, "series_parameters")
        }
    };

    private static ModelCostRuntimeRecord ReadCost(NpgsqlDataReader reader) => new()
    {
        Id = GetInt32(reader, "cost_id"),
        CostName = GetString(reader, "cost_name"),
        PricingModel = GetInt32(reader, "cost_pricing_model"),
        PricingConfiguration = GetNullableString(reader, "cost_pricing_configuration"),
        InputCostPerMillionTokens = GetDecimal(reader, "cost_input"),
        OutputCostPerMillionTokens = GetDecimal(reader, "cost_output"),
        EmbeddingCostPerMillionTokens = GetNullableDecimal(reader, "cost_embedding"),
        CreatedAt = GetDateTime(reader, "cost_created_at"),
        UpdatedAt = GetDateTime(reader, "cost_updated_at"),
        ModelType = GetString(reader, "cost_model_type"),
        IsActive = GetBoolean(reader, "cost_active"),
        EffectiveDate = GetDateTime(reader, "cost_effective_at"),
        ExpiryDate = GetNullableDateTime(reader, "cost_expires_at"),
        Description = GetNullableString(reader, "cost_description"),
        Priority = GetInt32(reader, "cost_priority"),
        BatchProcessingMultiplier = GetNullableDecimal(reader, "cost_batch_multiplier"),
        SupportsBatchProcessing = GetBoolean(reader, "cost_supports_batch"),
        CachedInputCostPerMillionTokens = GetNullableDecimal(reader, "cost_cached_input"),
        CachedInputWriteCostPerMillionTokens = GetNullableDecimal(reader, "cost_cached_write"),
        CostPerSearchUnit = GetNullableDecimal(reader, "cost_search_unit"),
        AudioCostPerMinute = GetNullableDecimal(reader, "cost_audio_minute"),
        AudioCostPerThousandCharacters = GetNullableDecimal(reader, "cost_audio_characters"),
        ReasoningCostPerMillionTokens = GetNullableDecimal(reader, "cost_reasoning")
    };

    private static Dictionary<string, string>? DeserializeSettings(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize(
                json,
                ProviderPersistenceJsonContext.Default.DictionaryStringString);

    private static bool IsNull(NpgsqlDataReader reader, string name) =>
        reader.IsDBNull(reader.GetOrdinal(name));

    private static string GetString(NpgsqlDataReader reader, string name) =>
        reader.GetString(reader.GetOrdinal(name));

    private static string? GetNullableString(NpgsqlDataReader reader, string name) =>
        IsNull(reader, name) ? null : GetString(reader, name);

    private static int GetInt32(NpgsqlDataReader reader, string name) =>
        reader.GetInt32(reader.GetOrdinal(name));

    private static int? GetNullableInt32(NpgsqlDataReader reader, string name) =>
        IsNull(reader, name) ? null : GetInt32(reader, name);

    private static bool GetBoolean(NpgsqlDataReader reader, string name) =>
        reader.GetBoolean(reader.GetOrdinal(name));

    private static decimal GetDecimal(NpgsqlDataReader reader, string name) =>
        reader.GetDecimal(reader.GetOrdinal(name));

    private static decimal? GetNullableDecimal(NpgsqlDataReader reader, string name) =>
        IsNull(reader, name) ? null : GetDecimal(reader, name);

    private static DateTime GetDateTime(NpgsqlDataReader reader, string name) =>
        reader.GetDateTime(reader.GetOrdinal(name));

    private static DateTime? GetNullableDateTime(NpgsqlDataReader reader, string name) =>
        IsNull(reader, name) ? null : GetDateTime(reader, name);

    private static (int Page, int PageSize) NormalizePagination(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize));
}
