using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Typed PostgreSQL selection of eligible function pricing without EF queries.</summary>
public sealed class NpgsqlFunctionPricingRuntimeStore : IFunctionPricingRuntimeStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFunctionPricingRuntimeStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<FunctionCost?> FindEligibleCostAsync(
        int functionConfigurationId, DateTime asOfUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c."Id", c."CostName", c."ProviderType", c."Purpose", c."Description",
                c."BaseCost", c."PricingModel", c."CostPerExecution", c."CostPerResult",
                c."CostPerToken", c."CostPerMinute", c."TieredPricing", c."PricingConfiguration",
                c."IsActive", c."EffectiveDate", c."ExpiryDate", c."Priority", c."CreatedAt", c."UpdatedAt"
            FROM "FunctionCosts" c
            WHERE c."IsActive" AND c."EffectiveDate" <= @asOf
                AND (c."ExpiryDate" IS NULL OR c."ExpiryDate" > @asOf)
                AND EXISTS (SELECT 1 FROM "FunctionCostMappings" m
                    WHERE m."FunctionConfigurationId" = @configuration AND m."IsActive"
                        AND m."FunctionCostId" = c."Id")
            ORDER BY c."Priority" DESC, c."EffectiveDate" DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("configuration", NpgsqlDbType.Integer, functionConfigurationId);
        command.Parameters.AddWithValue("asOf", NpgsqlDbType.TimestampTz, asOfUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new FunctionCost
        {
            Id = reader.GetInt32(0),
            CostName = reader.GetString(1),
            ProviderType = (FunctionProviderType)reader.GetInt32(2),
            Purpose = reader.IsDBNull(3) ? null : (FunctionPurpose)reader.GetInt32(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            BaseCost = NullableDecimal(reader, 5),
            PricingModel = (FunctionPricingModel)reader.GetInt32(6),
            CostPerExecution = NullableDecimal(reader, 7),
            CostPerResult = NullableDecimal(reader, 8),
            CostPerToken = NullableDecimal(reader, 9),
            CostPerMinute = NullableDecimal(reader, 10),
            TieredPricing = reader.IsDBNull(11) ? null : reader.GetString(11),
            PricingConfiguration = reader.IsDBNull(12) ? null : reader.GetString(12),
            IsActive = reader.GetBoolean(13),
            EffectiveDate = reader.GetDateTime(14),
            ExpiryDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
            Priority = reader.GetInt32(16),
            CreatedAt = reader.GetDateTime(17),
            UpdatedAt = reader.GetDateTime(18)
        };
    }

    private static decimal? NullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
}
