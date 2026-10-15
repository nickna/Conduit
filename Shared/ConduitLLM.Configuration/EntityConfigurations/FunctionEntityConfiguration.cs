using Microsoft.EntityFrameworkCore;
using ConduitLLM.Functions.Entities;

namespace ConduitLLM.Configuration.EntityConfigurations;

/// <summary>
/// Entity Framework Core configuration for Function-related entities
/// Mirrors the pattern used for LLM Provider entities
/// </summary>
public static class FunctionEntityConfiguration
{
    /// <summary>
    /// Applies all Function entity configurations to the model builder
    /// </summary>
    public static void ApplyFunctionEntityConfigurations(this ModelBuilder modelBuilder)
    {
        // Configure FunctionConfiguration entity
        modelBuilder.Entity<FunctionConfiguration>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index for querying by provider type
            entity.HasIndex(e => e.ProviderType)
                .HasDatabaseName("IX_FunctionConfiguration_ProviderType");

            // Index for querying by purpose
            entity.HasIndex(e => e.Purpose)
                .HasDatabaseName("IX_FunctionConfiguration_Purpose");

            // Index for querying enabled configurations
            entity.HasIndex(e => e.IsEnabled)
                .HasDatabaseName("IX_FunctionConfiguration_IsEnabled");
        });

        // Configure FunctionCredential entity
        modelBuilder.Entity<FunctionCredential>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index for performance when querying by provider type
            entity.HasIndex(e => e.ProviderType)
                .HasDatabaseName("IX_FunctionCredential_ProviderType");

            // Optional owning configuration (config-scoped credentials, e.g. per-MCP-server tokens).
            // Null = provider-global credential (Exa/Tavily). Deleting a configuration removes its
            // scoped credentials.
            entity.HasOne<FunctionConfiguration>()
                .WithMany()
                .HasForeignKey(e => e.FunctionConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.FunctionConfigurationId)
                .HasDatabaseName("IX_FunctionCredential_FunctionConfigurationId");

            // Unique constraint: only one primary GLOBAL credential per provider type
            // (config-scoped credentials are excluded — they get their own rule below).
            entity.HasIndex(e => new { e.ProviderType, e.IsPrimary })
                .IsUnique()
                .HasFilter("\"IsPrimary\" = true AND \"FunctionConfigurationId\" IS NULL")
                .HasDatabaseName("IX_FunctionCredential_OnePrimaryPerProviderType");

            // Unique constraint: only one primary credential per owning configuration
            // (each MCP server has its own primary token).
            entity.HasIndex(e => new { e.FunctionConfigurationId, e.IsPrimary })
                .IsUnique()
                .HasFilter("\"IsPrimary\" = true AND \"FunctionConfigurationId\" IS NOT NULL")
                .HasDatabaseName("IX_FunctionCredential_OnePrimaryPerConfiguration");

            // Unique constraint: prevent duplicate API keys among GLOBAL credentials of a provider
            // type. Config-scoped tokens are excluded (they are encrypted non-deterministically and
            // legitimately differ per server).
            entity.HasIndex(e => new { e.ProviderType, e.ApiKey })
                .IsUnique()
                .HasDatabaseName("IX_FunctionCredential_UniqueApiKeyPerProviderType")
                .HasFilter("\"ApiKey\" IS NOT NULL AND \"FunctionConfigurationId\" IS NULL");

            // Configure check constraints
            entity.ToTable(t => {
                // Check constraint: Primary credentials must be enabled
                // This ensures data integrity at the database level
                t.HasCheckConstraint(
                    "CK_FunctionCredential_PrimaryMustBeEnabled",
                    "\"IsPrimary\" = false OR \"IsEnabled\" = true"
                );

                // Check constraint: FunctionAccountGroup range (0-32)
                // Renamed from CredentialGroup to align with ProviderAccountGroup pattern
                t.HasCheckConstraint(
                    "CK_FunctionCredential_AccountGroupRange",
                    "\"FunctionAccountGroup\" >= 0 AND \"FunctionAccountGroup\" <= 32"
                );
            });
        });

        // Configure FunctionCost entity
        modelBuilder.Entity<FunctionCost>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index for querying active costs
            entity.HasIndex(e => e.IsActive)
                .HasDatabaseName("IX_FunctionCost_IsActive");

            // Index for date-based queries
            entity.HasIndex(e => new { e.EffectiveDate, e.ExpiryDate })
                .HasDatabaseName("IX_FunctionCost_EffectiveDates");
        });

        // Configure FunctionCostMapping entity
        modelBuilder.Entity<FunctionCostMapping>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationship with FunctionConfiguration
            entity.HasOne(e => e.FunctionConfiguration)
                .WithMany(e => e.CostMappings)
                .HasForeignKey(e => e.FunctionConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Relationship with FunctionCost
            entity.HasOne(e => e.FunctionCost)
                .WithMany(e => e.FunctionMappings)
                .HasForeignKey(e => e.FunctionCostId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint: One cost per configuration
            entity.HasIndex(e => new { e.FunctionConfigurationId, e.FunctionCostId })
                .IsUnique()
                .HasDatabaseName("IX_FunctionCostMapping_Unique");
        });

        // Configure FunctionExecution entity
        modelBuilder.Entity<FunctionExecution>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationship with FunctionConfiguration
            entity.HasOne(e => e.FunctionConfiguration)
                .WithMany(e => e.Executions)
                .HasForeignKey(e => e.FunctionConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes for common queries
            entity.HasIndex(e => e.VirtualKeyId)
                .HasDatabaseName("IX_FunctionExecution_VirtualKeyId");

            entity.HasIndex(e => e.State)
                .HasDatabaseName("IX_FunctionExecution_State");

            entity.HasIndex(e => e.RequestedAt)
                .HasDatabaseName("IX_FunctionExecution_RequestedAt");

            // Index for async task processing (leasing)
            entity.HasIndex(e => new { e.State, e.NextRetryAt, e.LeasedBy })
                .HasDatabaseName("IX_FunctionExecution_AsyncProcessing");
        });

        // Configure FunctionExecutionAudit entity
        modelBuilder.Entity<FunctionExecutionAudit>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationship with FunctionExecution
            entity.HasOne(e => e.FunctionExecution)
                .WithMany(e => e.AuditEvents)
                .HasForeignKey(e => e.FunctionExecutionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Index for querying audit logs
            entity.HasIndex(e => new { e.FunctionExecutionId, e.Timestamp })
                .HasDatabaseName("IX_FunctionExecutionAudit_ExecutionTimestamp");
        });

        // Configure FunctionCallAudit entity
        modelBuilder.Entity<FunctionCallAudit>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Relationship with FunctionExecution (optional)
            entity.HasOne(e => e.FunctionExecution)
                .WithMany()
                .HasForeignKey(e => e.FunctionExecutionId)
                .OnDelete(DeleteBehavior.SetNull);

            // Relationship with FunctionConfiguration
            entity.HasOne(e => e.FunctionConfiguration)
                .WithMany()
                .HasForeignKey(e => e.FunctionConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Index for querying by chat completion ID (parent request)
            entity.HasIndex(e => e.ChatCompletionId)
                .HasDatabaseName("IX_FunctionCallAudit_ChatCompletionId");

            // Index for querying by function execution
            entity.HasIndex(e => e.FunctionExecutionId)
                .HasDatabaseName("IX_FunctionCallAudit_FunctionExecutionId");

            // Index for querying by function configuration
            entity.HasIndex(e => e.FunctionConfigurationId)
                .HasDatabaseName("IX_FunctionCallAudit_FunctionConfigurationId");

            // Index for querying by virtual key
            entity.HasIndex(e => e.VirtualKeyId)
                .HasDatabaseName("IX_FunctionCallAudit_VirtualKeyId");

            // Index for timestamp and event type queries (most common query pattern)
            entity.HasIndex(e => new { e.Timestamp, e.EventType })
                .HasDatabaseName("IX_FunctionCallAudit_TimestampEventType");

            // Index for request correlation
            entity.HasIndex(e => e.RequestId)
                .HasDatabaseName("IX_FunctionCallAudit_RequestId");

            // Composite index for querying calls by key and date range
            entity.HasIndex(e => new { e.VirtualKeyId, e.Timestamp })
                .HasDatabaseName("IX_FunctionCallAudit_VirtualKeyTimestamp");
        });
    }
}
