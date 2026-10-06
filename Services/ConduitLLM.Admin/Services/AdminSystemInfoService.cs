using System.Diagnostics;
using System.Runtime.InteropServices;

using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs.Monitoring;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Core.Diagnostics;

using Microsoft.EntityFrameworkCore;

using ConduitLLM.Configuration.Interfaces;
namespace ConduitLLM.Admin.Services;

/// <summary>
/// Service for retrieving system information through the Admin API
/// </summary>
public class AdminSystemInfoService : IAdminSystemInfoService
{
    private readonly IConfigurationDbContext _dbContext;
    private readonly ILogger<AdminSystemInfoService> _logger;
    private readonly IProviderRepository _providerRepository;
    private readonly ISchemaVersionProbe? _schemaVersionProbe;
    private readonly ConduitLLM.Core.Configuration.CustomerErrorOptions _customerErrorOptions;
    private readonly DateTime _startTime;

    /// <summary>
    /// Initializes a new instance of the AdminSystemInfoService class
    /// </summary>
    /// <param name="dbContext">The configuration database context</param>
    /// <param name="logger">The logger</param>
    /// <param name="providerRepository">The provider repository</param>
    /// <param name="customerErrorOptions">Customer error mode (CONDUIT_CUSTOMER_MODE)</param>
    /// <param name="schemaVersionProbe">Read-only database schema-version probe</param>
    public AdminSystemInfoService(
        IConfigurationDbContext dbContext,
        ILogger<AdminSystemInfoService> logger,
        IProviderRepository providerRepository,
        ConduitLLM.Core.Configuration.CustomerErrorOptions? customerErrorOptions = null,
        ISchemaVersionProbe? schemaVersionProbe = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _providerRepository = providerRepository ?? throw new ArgumentNullException(nameof(providerRepository));
        _schemaVersionProbe = schemaVersionProbe;
        _customerErrorOptions = customerErrorOptions ?? new ConduitLLM.Core.Configuration.CustomerErrorOptions();
        _startTime = Process.GetCurrentProcess().StartTime;
    }

    /// <inheritdoc />
    public async Task<SystemInfoDto> GetSystemInfoAsync()
    {
        _logger.LogDebug("Getting system information");

        var systemInfo = new SystemInfoDto
        {
            Version = GetVersionInfo(),
            OperatingSystem = GetOsInfo(),
            Runtime = GetRuntimeInfo(),
            Database = await GetDatabaseInfoAsync(),
            RecordCounts = await GetRecordCountsAsync()
        };

        return systemInfo;
    }

    /// <inheritdoc />
    public async Task<HealthStatusDto> GetHealthStatusAsync()
    {
        _logger.LogDebug("Getting health status");

        var sw = Stopwatch.StartNew();
        var checks = new Dictionary<string, ComponentHealth>();

        // Database health
        var dbSw = Stopwatch.StartNew();
        var dbHealth = await CheckDatabaseHealthAsync();
        dbHealth.Duration = dbSw.ElapsedMilliseconds;
        checks.Add("database", dbHealth);

        // Provider health check removed - Epic #680

        sw.Stop();

        // Overall health is determined by component statuses
        string overallStatus = checks.All(c => c.Value.Status == "healthy")
            ? "healthy"
            : checks.Any(c => c.Value.Status == "unhealthy")
                ? "unhealthy"
                : "degraded";

        return new HealthStatusDto
        {
            Status = overallStatus,
            Timestamp = DateTime.UtcNow,
            Checks = checks,
            TotalDuration = sw.ElapsedMilliseconds
        };
    }

    private VersionInfo GetVersionInfo()
    {
        var build = BuildMetadata.FromAssembly(typeof(AdminSystemInfoService).Assembly);

        return new VersionInfo
        {
            AppVersion = build.Version,
            CommitSha = build.CommitSha,
            BuildTimestamp = build.BuildTimestamp
        };
    }

    private OsInfo GetOsInfo()
    {
        return new OsInfo
        {
            Description = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString()
        };
    }

    private RuntimeInfo GetRuntimeInfo()
    {
        return new RuntimeInfo
        {
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            StartTime = _startTime,
            Uptime = DateTime.UtcNow - _startTime.ToUniversalTime(),
            CustomerMode = _customerErrorOptions.Mode.ToString()
        };
    }

    private async Task<DatabaseInfo> GetDatabaseInfoAsync()
    {
        var info = new DatabaseInfo
        {
            Provider = _dbContext.GetDatabase().ProviderName ?? "Unknown",
            Connected = false,
            Version = "Unknown"
        };

        try
        {
            // Check connection
            info.Connected = await _dbContext.GetDatabase().CanConnectAsync();
            _logger.LogDebug("Database connection check: {Connected}, provider: {Provider}", info.Connected, info.Provider);

            // Get database version if possible
            if (info.Connected)
            {
                // Get connection string (masked)
                var connectionString = _dbContext.GetDatabase().GetConnectionString();
                info.ConnectionString = MaskConnectionString(connectionString);

                if (info.Provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    info.Version = "-1"; // We'll get this with raw SQL below
                    info.Location = ExtractHostFromConnectionString(connectionString);

                    // Get PostgreSQL version and database size using raw SQL
                    try
                    {
                        var dbConnection = _dbContext.GetDatabase().GetDbConnection();
                        await dbConnection.OpenAsync();

                        using (var command = dbConnection.CreateCommand())
                        {
                            // Get PostgreSQL version
                            command.CommandText = "SELECT version()";
                            var versionResult = await command.ExecuteScalarAsync();
                            if (versionResult != null)
                            {
                                var versionString = versionResult.ToString();
                                if (!string.IsNullOrEmpty(versionString))
                                {
                                    // Extract just the version number from the full version string
                                    var match = System.Text.RegularExpressions.Regex.Match(versionString, @"PostgreSQL (\d+\.\d+)");
                                    info.Version = match.Success ? match.Groups[1].Value : versionString;
                                }
                            }
                        }

                        using (var command = dbConnection.CreateCommand())
                        {
                            // Get database size
                            var dbName = ExtractDatabaseNameFromConnectionString(connectionString);
                            // Validate database name to prevent SQL injection
                            if (!IsValidDatabaseName(dbName))
                            {
                                info.Size = "Invalid database name";
                            }
                            else
                            {
                                // Use quote_ident to safely escape the database name
                                command.CommandText = "SELECT pg_database_size(quote_ident(@dbName))";
                                var parameter = command.CreateParameter();
                                parameter.ParameterName = "@dbName";
                                parameter.Value = dbName;
                                command.Parameters.Add(parameter);
                                var sizeResult = await command.ExecuteScalarAsync();
                                if (sizeResult != null && long.TryParse(sizeResult.ToString(), out long sizeInBytes))
                                {
                                    info.Size = FormatFileSize(sizeInBytes);
                                }
                                else
                                {
                                    info.Size = "Unknown";
                                }
                            }
                        }

                        await dbConnection.CloseAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not get PostgreSQL version/size for host {Host}", info.Location);
                        info.Size = "N/A";
                    }
                }

                // Get table count
                var tables = await GetTableCountAsync();
                info.TableCount = tables;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database information");
            info.Connected = false;
        }

        return info;
    }

    private async Task<ComponentHealth> CheckDatabaseHealthAsync()
    {
        var health = new ComponentHealth
        {
            Description = "Database connection and migrations"
        };

        try
        {
            // Check connection
            bool canConnect = await _dbContext.GetDatabase().CanConnectAsync();

            if (canConnect)
            {
                var schema = _schemaVersionProbe is null
                    ? null
                    : await _schemaVersionProbe.GetStatusAsync();
                health.Status = schema is null || schema.IsCurrent ? "healthy" : "degraded";
                if (schema is { IsCurrent: false })
                {
                    _logger.LogWarning(
                        "Database schema version {AppliedVersion} does not match required version {ExpectedVersion}",
                        schema.AppliedVersion ?? "<uninitialized>", schema.ExpectedVersion);
                    health.Description = "Database connected but schema version is not current";
                }
                else
                {
                    _logger.LogDebug("Database schema is current");
                }
            }
            else
            {
                _logger.LogWarning("Database health check failed: unable to connect");
                health.Status = "unhealthy";
                health.Description = "Database connection failed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking database health");
            health.Status = "unhealthy";
            health.Error = ex.Message;
        }

        return health;
    }


    private async Task<RecordCountsDto> GetRecordCountsAsync()
    {
        var counts = new RecordCountsDto();

        try
        {
            counts.VirtualKeys = await _dbContext.VirtualKeys.CountAsync();
            counts.Settings = await _dbContext.GlobalSettings.CountAsync();
            counts.Providers = await _dbContext.Providers.CountAsync();
            counts.ModelMappings = await _dbContext.ModelProviderMappings.CountAsync();

            _logger.LogDebug("Configuration inventory: VirtualKeys={VirtualKeys}, Providers={Providers}, Mappings={Mappings}",
                counts.VirtualKeys, counts.Providers, counts.ModelMappings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting record counts");
        }

        return counts;
    }

    private async Task<int?> GetTableCountAsync()
    {
        try
        {
            var provider = _dbContext.GetDatabase().ProviderName ?? "";
            var dbConnection = _dbContext.GetDatabase().GetDbConnection();

            await dbConnection.OpenAsync();

            using (var command = dbConnection.CreateCommand())
            {
                if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    // PostgreSQL query to count tables
                    command.CommandText = @"
                        SELECT COUNT(*) 
                        FROM information_schema.tables 
                        WHERE table_schema = 'public' 
                        AND table_type = 'BASE TABLE'";
                }
                else if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    // SQLite query to count tables
                    command.CommandText = @"
                        SELECT COUNT(*) 
                        FROM sqlite_master 
                        WHERE type = 'table' 
                        AND name NOT LIKE 'sqlite_%'";
                }
                else
                {
                    // No table-count query for this provider — report unknown, not a guess
                    await dbConnection.CloseAsync();
                    return null;
                }

                var result = await command.ExecuteScalarAsync();
                await dbConnection.CloseAsync();

                if (result != null && int.TryParse(result.ToString(), out int count))
                {
                    return count;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting table count");
            return null;
        }
    }

    /// <summary>
    /// Parses a connection string into a case-insensitive dictionary of key-value pairs.
    /// Handles values containing '=' correctly by limiting the split.
    /// </summary>
    private static Dictionary<string, string> ParseConnectionStringParts(string? connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(connectionString))
            return result;

        foreach (var part in connectionString.Split(';'))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var kvp = trimmed.Split('=', 2);
            if (kvp.Length == 2)
            {
                result[kvp[0].Trim()] = kvp[1].Trim();
            }
        }

        return result;
    }

    private static string MaskConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return "Not configured";

        var parts = ParseConnectionStringParts(connectionString);
        var maskedParts = new List<string>(parts.Count);

        foreach (var kvp in parts)
        {
            if (kvp.Key.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Equals("Pwd", StringComparison.OrdinalIgnoreCase))
            {
                maskedParts.Add($"{kvp.Key}=****");
            }
            else
            {
                maskedParts.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        return string.Join("; ", maskedParts);
    }

    private static string ExtractHostFromConnectionString(string? connectionString)
    {
        var parts = ParseConnectionStringParts(connectionString);

        if (parts.TryGetValue("Host", out var host))
            return host;
        if (parts.TryGetValue("Server", out var server))
            return server;

        return "Unknown";
    }

    private static string ExtractDatabaseNameFromConnectionString(string? connectionString)
    {
        var parts = ParseConnectionStringParts(connectionString);

        if (parts.TryGetValue("Database", out var database))
            return database;

        return "";
    }

    private bool IsValidDatabaseName(string dbName)
    {
        if (string.IsNullOrWhiteSpace(dbName))
            return false;

        // Database names in PostgreSQL can contain letters, numbers, underscores, and hyphens
        // They cannot contain quotes, semicolons, or other special characters that could be used for SQL injection
        return System.Text.RegularExpressions.Regex.IsMatch(dbName, @"^[a-zA-Z0-9_\-]+$");
    }

    private string FormatFileSize(long bytes)
    {
        if (bytes < 0)
            return "N/A";

        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
