using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql implementation of configured-provider
/// persistence.
/// </summary>
public sealed class NpgsqlProviderRepository : IProviderRepository
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlProviderRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Provider>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var providers = await ReadProvidersAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.ProviderColumns} FROM \"Providers\" " +
            "ORDER BY \"ProviderType\"",
            addParameters: null,
            cancellationToken);
        await LoadCredentialsAsync(connection, providers, cancellationToken);
        return providers;
    }

    /// <inheritdoc />
    public async Task<(List<Provider> Items, int TotalCount)> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        (page, pageSize) = NormalizePagination(page, pageSize);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT count(*) FROM \"Providers\"";
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        var providers = await ReadProvidersAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.ProviderColumns} FROM \"Providers\" " +
            "ORDER BY \"ProviderType\" OFFSET @offset LIMIT @limit",
            command =>
            {
                command.Parameters.AddWithValue(
                    "offset",
                    NpgsqlDbType.Integer,
                    (page - 1) * pageSize);
                command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize);
            },
            cancellationToken);
        await LoadCredentialsAsync(connection, providers, cancellationToken);
        return (providers, totalCount);
    }

    /// <inheritdoc />
    public async Task<Provider?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var providers = await ReadProvidersAsync(
            connection,
            $"SELECT {ProviderPersistenceMappings.ProviderColumns} FROM \"Providers\" WHERE \"Id\" = @id",
            command => command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id),
            cancellationToken);
        await LoadCredentialsAsync(connection, providers, cancellationToken);
        return providers.Count == 0 ? null : providers[0];
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        Provider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateProvider(provider);
        RejectCredentialGraph(provider);

        var now = DateTime.UtcNow;
        if (provider.CreatedAt == default)
        {
            provider.CreatedAt = now;
        }
        provider.UpdatedAt = now;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Providers" (
                "ProviderType", "ProviderName", "BaseUrl", "Settings", "IsEnabled",
                "TrustProviderReportedCosts", "ProviderCostMarkupMultiplier", "CreatedAt", "UpdatedAt")
            VALUES (
                @providerType, @providerName, @baseUrl, @settings, @isEnabled,
                @trustProviderReportedCosts, @providerCostMarkupMultiplier, @createdAt, @updatedAt)
            RETURNING "Id"
            """;
        ProviderPersistenceMappings.AddProviderParameters(command, provider, includeId: false);
        provider.Id = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return provider.Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Provider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateProvider(provider);
        provider.UpdatedAt = DateTime.UtcNow;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "Providers"
            SET "ProviderType" = @providerType,
                "ProviderName" = @providerName,
                "BaseUrl" = @baseUrl,
                "Settings" = @settings,
                "IsEnabled" = @isEnabled,
                "TrustProviderReportedCosts" = @trustProviderReportedCosts,
                "ProviderCostMarkupMultiplier" = @providerCostMarkupMultiplier,
                "CreatedAt" = @createdAt,
                "UpdatedAt" = @updatedAt
            WHERE "Id" = @id
            """;
        ProviderPersistenceMappings.AddProviderParameters(command, provider, includeId: true);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"Providers\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, string>> GetProviderNameMapAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\", \"ProviderName\" FROM \"Providers\"";

        var names = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetInt32(0), reader.GetString(1));
        }
        return names;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(
        bool? enabledOnly,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (enabledOnly.HasValue)
        {
            command.CommandText = "SELECT count(*) FROM \"Providers\" WHERE \"IsEnabled\" = @isEnabled";
            command.Parameters.AddWithValue("isEnabled", NpgsqlDbType.Boolean, enabledOnly.Value);
        }
        else
        {
            command.CommandText = "SELECT count(*) FROM \"Providers\"";
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<List<Provider>> ReadProvidersAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? addParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        addParameters?.Invoke(command);

        var providers = new List<Provider>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            providers.Add(ProviderPersistenceMappings.ReadProvider(reader));
        }
        return providers;
    }

    private static async Task LoadCredentialsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Provider> providers,
        CancellationToken cancellationToken)
    {
        if (providers.Count == 0)
        {
            return;
        }

        var providersById = providers.ToDictionary(provider => provider.Id);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ProviderPersistenceMappings.CredentialColumns} FROM \"ProviderKeyCredentials\" " +
            "WHERE \"ProviderId\" = ANY(@providerIds) " +
            "ORDER BY \"ProviderId\", \"IsPrimary\" DESC, \"ProviderAccountGroup\"";
        command.Parameters.AddWithValue(
            "providerIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer,
            providersById.Keys.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var credential = ProviderPersistenceMappings.ReadCredential(reader);
            var provider = providersById[credential.ProviderId];
            credential.Provider = provider;
            provider.ProviderKeyCredentials.Add(credential);
        }
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

    private static void ValidateProvider(Provider provider) =>
        ArgumentNullException.ThrowIfNull(provider.ProviderName);

    private static void RejectCredentialGraph(Provider provider)
    {
        if (provider.ProviderKeyCredentials.Count > 0)
        {
            throw new InvalidOperationException(
                "Create provider credentials through IProviderKeyCredentialRepository.");
        }
    }
}
