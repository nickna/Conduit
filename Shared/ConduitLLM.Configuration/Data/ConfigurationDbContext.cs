using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.EntityConfigurations;
using ModelProviderMappingEntity = ConduitLLM.Configuration.Entities.ModelProviderMapping;

using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

using ConduitLLM.Configuration.Interfaces;
namespace ConduitLLM.Configuration
{
    /// <summary>
    /// Database context for ConduitLLM configuration
    /// </summary>
    public class ConduitDbContext : DbContext, IConfigurationDbContext
    {
        /// <summary>
        /// Initializes a new instance of the ConfigurationDbContext
        /// </summary>
        /// <param name="options">The options to be used by the context</param>
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Production registration supplies the checked-in ConfigurationDbContextModel compiled EF model.")]
        [UnconditionalSuppressMessage(
            "AOT",
            "IL3050",
            Justification = "Production registration supplies the checked-in ConfigurationDbContextModel compiled EF model.")]
        public ConduitDbContext(DbContextOptions<ConduitDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// Database set for virtual keys
        /// </summary>
        public virtual DbSet<VirtualKey> VirtualKeys { get; set; } = null!;

        /// <summary>
        /// Database set for virtual key groups
        /// </summary>
        public virtual DbSet<VirtualKeyGroup> VirtualKeyGroups { get; set; } = null!;

        /// <summary>
        /// Database set for virtual key group transactions
        /// </summary>
        public virtual DbSet<VirtualKeyGroupTransaction> VirtualKeyGroupTransactions { get; set; } = null!;

        public virtual DbSet<RefundIdempotencyRecord> RefundIdempotencyRecords { get; set; } = null!;

        /// <summary>
        /// Database set for request logs
        /// </summary>
        public virtual DbSet<RequestLog> RequestLogs { get; set; } = null!;

        /// <summary>
        /// Database set for billing audit events
        /// </summary>
        public virtual DbSet<BillingAuditEvent> BillingAuditEvents { get; set; } = null!;

        /// <summary>Durable cursor for the billing reconciliation job.</summary>
        public virtual DbSet<BillingReconciliationCheckpoint> BillingReconciliationCheckpoints { get; set; } = null!;

        /// <summary>
        /// Database set for pricing audit events (rules-based pricing)
        /// </summary>
        public virtual DbSet<PricingAuditEvent> PricingAuditEvents { get; set; } = null!;

        /// <summary>
        /// Database set for virtual key spend history
        /// </summary>
        public virtual DbSet<VirtualKeySpendHistory> VirtualKeySpendHistory { get; set; } = null!;

        /// <summary>
        /// Database set for virtual key spend history (alias for backward compatibility)
        /// </summary>
        public virtual DbSet<VirtualKeySpendHistory> VirtualKeySpendHistories => VirtualKeySpendHistory;


        /// <summary>
        /// Database set for notifications
        /// </summary>
        public virtual DbSet<Notification> Notifications { get; set; } = null!;

        /// <summary>
        /// Database set for global settings
        /// </summary>
        public virtual DbSet<GlobalSetting> GlobalSettings { get; set; } = null!;

        /// <summary>
        /// Database set for model costs
        /// </summary>
        public virtual DbSet<ModelCost> ModelCosts { get; set; } = null!;

        /// <summary>
        /// Database set for model provider mappings
        /// </summary>
        public virtual DbSet<ModelProviderMappingEntity> ModelProviderMappings { get; set; } = null!;
        public virtual DbSet<ModelRoutePolicy> ModelRoutePolicies { get; set; } = null!;
        
        /// <summary>
        /// Database set for models
        /// </summary>
        public virtual DbSet<Model> Models { get; set; } = null!;
        
        /// <summary>
        /// Database set for model series
        /// </summary>
        public virtual DbSet<ModelSeries> ModelSeries { get; set; } = null!;
        
        /// <summary>
        /// Database set for model authors
        /// </summary>
        public virtual DbSet<ModelAuthor> ModelAuthors { get; set; } = null!;
        
        /// <summary>
        /// Database set for model provider type associations
        /// </summary>
        public virtual DbSet<ModelProviderTypeAssociation> ModelProviderTypeAssociations { get; set; } = null!;

        /// <summary>
        /// Database set for provider metadata sync runs (OpenRouter drift detection).
        /// </summary>
        public virtual DbSet<ProviderMetadataSyncRun> ProviderMetadataSyncRuns { get; set; } = null!;

        /// <summary>
        /// Database set for provider metadata drift items awaiting admin review.
        /// </summary>
        public virtual DbSet<ProviderMetadataDriftItem> ProviderMetadataDriftItems { get; set; } = null!;

        /// <summary>
        /// Database set for media records
        /// </summary>
        public virtual DbSet<MediaRecord> MediaRecords { get; set; } = null!;

        /// <summary>
        /// Durable approvals for large scheduled media cleanup scopes.
        /// </summary>
        public virtual DbSet<MediaCleanupApproval> MediaCleanupApprovals { get; set; } = null!;

        /// <summary>
        /// Database set for media retention policies
        /// </summary>
        public virtual DbSet<MediaRetentionPolicy> MediaRetentionPolicies { get; set; } = null!;

        /// <summary>
        /// Database set for providers
        /// </summary>
        public virtual DbSet<Provider> Providers { get; set; } = null!;

        /// <summary>
        /// Database set for provider key credentials
        /// </summary>
        public virtual DbSet<ProviderKeyCredential> ProviderKeyCredentials { get; set; } = null!;

        /// <summary>
        /// Database set for provider tools
        /// </summary>
        public virtual DbSet<ProviderTool> ProviderTools { get; set; } = null!;

        /// <summary>
        /// Database set for IP filters
        /// </summary>
        public virtual DbSet<IpFilterEntity> IpFilters { get; set; } = null!;


        // ModelCostMapping removed - costs are now directly associated with ModelProviderTypeAssociation

        /// <summary>
        /// Database set for async tasks
        /// </summary>
        public virtual DbSet<AsyncTask> AsyncTasks { get; set; } = null!;

        // MediaLifecycleRecords removed - consolidated into MediaRecords table
        // Migration: 20250827194408_ConsolidateMediaTables.cs

        /// <summary>
        /// Database set for batch operation history
        /// </summary>
        public virtual DbSet<BatchOperationHistory> BatchOperationHistory { get; set; } = null!;

        // Function-related DbSets

        /// <summary>
        /// Database set for function configurations
        /// </summary>
        public virtual DbSet<ConduitLLM.Functions.Entities.FunctionConfiguration> FunctionConfigurations { get; set; } = null!;

        /// <summary>
        /// Database set for function credentials
        /// </summary>
        public virtual DbSet<ConduitLLM.Functions.Entities.FunctionCredential> FunctionCredentials { get; set; } = null!;

        /// <summary>
        /// Database set for function costs
        /// </summary>
        public virtual DbSet<ConduitLLM.Functions.Entities.FunctionCost> FunctionCosts { get; set; } = null!;

        /// <summary>
        /// Database set for function cost mappings
        /// </summary>
        public virtual DbSet<ConduitLLM.Functions.Entities.FunctionCostMapping> FunctionCostMappings { get; set; } = null!;

        /// <summary>
        /// Database set for function executions
        /// </summary>
        public virtual DbSet<ConduitLLM.Functions.Entities.FunctionExecution> FunctionExecutions { get; set; } = null!;

        /// <summary>
        /// Database set for function execution audits
        /// </summary>
        public virtual DbSet<ConduitLLM.Functions.Entities.FunctionExecutionAudit> FunctionExecutionAudits { get; set; } = null!;

        /// <summary>
        /// Database set for function call audits (tracking function calls in chat completions)
        /// </summary>
        public virtual DbSet<ConduitLLM.Functions.Entities.FunctionCallAudit> FunctionCallAudits { get; set; } = null!;

        public bool IsTestEnvironment { get; set; } = false;

        /// <summary>
        /// Configures the model for the context
        /// </summary>
        /// <param name="modelBuilder">The model builder</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // EF's experimental NativeAOT compiled-model generator cannot emit query
            // filters. The generated native model therefore omits them; its query data
            // plane is explicitly excluded. Normal/JIT model construction retains the
            // filters unchanged.
            var generateNativeCompiledModel = string.Equals(
                Environment.GetEnvironmentVariable("CONDUIT_EF_COMPILED_MODEL"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            // Configure VirtualKeyGroup entity
            modelBuilder.Entity<VirtualKeyGroup>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ExternalGroupId);
                
                // Configure relationships
                entity.HasMany(e => e.VirtualKeys)
                      .WithOne(e => e.VirtualKeyGroup)
                      .HasForeignKey(e => e.VirtualKeyGroupId)
                      .OnDelete(DeleteBehavior.Restrict);
                
                entity.HasMany(e => e.Transactions)
                      .WithOne(e => e.VirtualKeyGroup)
                      .HasForeignKey(e => e.VirtualKeyGroupId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Configure VirtualKey entity
            modelBuilder.Entity<VirtualKey>(entity =>
            {
                entity.HasIndex(e => e.KeyHash).IsUnique();

                // Configure navigation property for RequestLogs
                entity.HasMany(e => e.RequestLogs)
                      .WithOne(e => e.VirtualKey)
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Configure navigation property for SpendHistory
                entity.HasMany(e => e.SpendHistory)
                      .WithOne(e => e.VirtualKey)
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Configure navigation property for Notifications
                entity.HasMany(e => e.Notifications)
                      .WithOne(e => e.VirtualKey)
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Configure GlobalSetting entity
            modelBuilder.Entity<GlobalSetting>(entity =>
            {
                entity.HasIndex(e => e.Key).IsUnique();
            });

            // Configure RequestLog entity
            modelBuilder.Entity<RequestLog>(entity =>
            {
                entity.HasOne(e => e.VirtualKey)
                      .WithMany(e => e.RequestLogs)
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(e => new { e.BilledAtUtc, e.VirtualKeyId });
                entity.HasIndex(e => new { e.ModelProviderMappingId, e.Timestamp });
            });

            // Configure ModelCost entity
            modelBuilder.Entity<ModelCost>(entity =>
            {
                entity.HasIndex(e => e.CostName);
                
                // Configure one-to-many relationship with ModelProviderTypeAssociation
                entity.HasMany(e => e.ModelProviderTypeAssociations)
                      .WithOne(e => e.ModelCost)
                      .HasForeignKey(e => e.ModelCostId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // ModelProviderTypeAssociation configuration is handled by ModelProviderTypeAssociationEntityConfiguration
            // in EntityConfigurations/ModelEntityConfiguration.cs via ApplyModelConfigurations()

            // Configure VirtualKeySpendHistory entity
            modelBuilder.Entity<VirtualKeySpendHistory>(entity =>
            {
                entity.HasKey(e => e.Id);
                // Remove redundant relationship configuration as it's already defined by annotations and the VirtualKey configuration
            });

            // Configure Notification entity
            modelBuilder.Entity<Notification>(entity =>
            {
                entity.HasKey(e => e.Id);
                // Remove redundant relationship configuration as it's already defined by annotations and the VirtualKey configuration
            });

            // Configure IP Filter entity
            modelBuilder.Entity<IpFilterEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                // Create a non-unique index on the filter type and IP address/CIDR fields
                entity.HasIndex(e => new { e.FilterType, e.IpAddressOrCidr });
                // Create an index for IsEnabled to quickly filter active rules
                entity.HasIndex(e => e.IsEnabled);
                // Per-key scoping: null VirtualKeyId = global filter; a set value scopes the filter to
                // that key. Index it for the per-key enforcement query; cascade-delete with the key.
                entity.HasIndex(e => e.VirtualKeyId);
                entity.HasOne<VirtualKey>()
                      .WithMany(vk => vk.IpFilters)
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Cascade);
            });


            // Configure AsyncTask entity
            modelBuilder.Entity<AsyncTask>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.VirtualKeyId);
                entity.HasIndex(e => e.Type);
                entity.HasIndex(e => e.State);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => e.IsArchived);
                entity.HasIndex(e => new { e.VirtualKeyId, e.CreatedAt });
                
                // Composite index for archival queries
                entity.HasIndex(e => new { e.IsArchived, e.CompletedAt, e.State })
                      .HasDatabaseName("IX_AsyncTasks_Archival");
                
                // Index for cleanup queries
                entity.HasIndex(e => new { e.IsArchived, e.ArchivedAt })
                      .HasDatabaseName("IX_AsyncTasks_Cleanup");
                
                entity.HasOne(e => e.VirtualKey)
                      .WithMany()
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Cascade);
                
                // Configure large text fields without specifying provider-specific types
                // EF Core will map these to appropriate text types for each provider
                // By not specifying MaxLength, EF Core treats these as unlimited length text
                entity.Property(e => e.Payload);
                entity.Property(e => e.Result);
                entity.Property(e => e.Error);
                entity.Property(e => e.Metadata);
            });

            // Configure MediaRecord entity
            modelBuilder.Entity<MediaRecord>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.StorageKey).IsUnique();
                entity.HasIndex(e => e.VirtualKeyId);
                entity.HasIndex(e => e.ExpiresAt);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => e.DeletedAt);
                entity.HasIndex(e => new { e.VirtualKeyId, e.CreatedAt });
                if (!generateNativeCompiledModel)
                {
                    entity.HasQueryFilter(e => e.DeletedAt == null);
                }

                // Keep cascade semantics for key deletion: the storage reconciliation sweep
                // independently discovers and removes objects left behind in external storage.
                entity.HasOne(e => e.VirtualKey)
                      .WithMany()
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<MediaCleanupApproval>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CleanupType).HasMaxLength(64);
                entity.Property(e => e.Status).HasMaxLength(32);
                entity.Property(e => e.DecidedBy).HasMaxLength(256);
                entity.Property(e => e.ExecutionStatus).HasMaxLength(512);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => new { e.CleanupType, e.VirtualKeyGroupId, e.Status });
                entity.HasIndex(e => e.ExpiresAtUtc);
            });

            // MediaLifecycleRecord configuration removed - consolidated into MediaRecords
            // Migration: 20250827194408_ConsolidateMediaTables.cs

            // Configure MediaRetentionPolicy entity
            modelBuilder.Entity<MediaRetentionPolicy>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.HasIndex(e => e.IsDefault);
                entity.HasIndex(e => e.IsActive);
                
                // Ensure only one default policy
                entity.HasIndex(e => e.IsDefault)
                      .HasFilter("\"IsDefault\" = true")
                      .IsUnique();
            });

            // Configure VirtualKeyGroup relationship with MediaRetentionPolicy
            modelBuilder.Entity<VirtualKeyGroup>(entity =>
            {
                entity.HasOne(e => e.MediaRetentionPolicy)
                      .WithMany(p => p.VirtualKeyGroups)
                      .HasForeignKey(e => e.MediaRetentionPolicyId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Configure BatchOperationHistory entity
            modelBuilder.Entity<BatchOperationHistory>(entity =>
            {
                entity.HasKey(e => e.OperationId);
                entity.Ignore(e => e.Id);
                entity.HasIndex(e => e.VirtualKeyId);
                entity.HasIndex(e => e.OperationType);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.StartedAt);
                entity.HasIndex(e => new { e.VirtualKeyId, e.StartedAt });
                entity.HasIndex(e => new { e.OperationType, e.Status, e.StartedAt });
                
                entity.HasOne(e => e.VirtualKey)
                      .WithMany()
                      .HasForeignKey(e => e.VirtualKeyId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Configure VirtualKeyGroupTransaction entity
            modelBuilder.Entity<VirtualKeyGroupTransaction>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.VirtualKeyGroupId);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => new { e.VirtualKeyGroupId, e.CreatedAt });
                entity.HasIndex(e => new { e.IsDeleted, e.CreatedAt });
                entity.HasIndex(e => e.ReferenceType);
                entity.HasIndex(e => e.TransactionType);
                entity.HasIndex(e => new { e.BillingWindowStartUtc, e.VirtualKeyGroupId });

                // Idempotency for at-least-once spend processing (#927): one ledger row
                // per idempotency key. Filtered so the many rows without a key are exempt.
                entity.HasIndex(e => e.IdempotencyKey)
                      .IsUnique()
                      .HasFilter("\"IdempotencyKey\" IS NOT NULL");

                // Store enums as integers
                entity.Property(e => e.TransactionType)
                      .HasConversion<int>();

                entity.Property(e => e.ReferenceType)
                      .HasConversion<int>();

                // Global query filter for soft deletes (EF Core 10 named query filter)
                if (!generateNativeCompiledModel)
                {
                    entity.HasQueryFilter("SoftDelete", t => !t.IsDeleted);
                }
            });

            // Configure ProviderTool entity
            modelBuilder.Entity<ProviderTool>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.Provider, e.ToolName })
                      .HasDatabaseName("IX_ProviderTool_Provider_ToolName")
                      .IsUnique();
                entity.HasIndex(e => e.IsActive)
                      .HasDatabaseName("IX_ProviderTool_IsActive");
                
                // Store enum as integer
                entity.Property(e => e.Provider)
                      .HasConversion<int>();
            });

            modelBuilder.ApplyConfigurationEntityConfigurations(IsTestEnvironment);

            // Apply Model entity configurations with all indexes and relationships
            modelBuilder.ApplyModelConfigurations();

            // Apply Function entity configurations (mirrors Provider pattern)
            modelBuilder.ApplyFunctionEntityConfigurations();

            // Apply BillingAuditEvent configuration
            modelBuilder.ApplyConfiguration(new EntityConfigurations.BillingAuditEventConfiguration());

            modelBuilder.Entity<BillingReconciliationCheckpoint>(entity =>
            {
                entity.ToTable("BillingReconciliationCheckpoints");
                entity.HasKey(e => e.Id);
            });

            modelBuilder.Entity<RefundIdempotencyRecord>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.VirtualKeyGroupId, e.OperationId }).IsUnique();
                entity.Property(e => e.ResponseJson).IsRequired();
            });

            // Apply PricingAuditEvent configuration (rules-based pricing)
            modelBuilder.ApplyConfiguration(new EntityConfigurations.PricingAuditEventConfiguration());

            // Apply ProviderMetadataDriftItem configuration (string enums + partial unique index)
            modelBuilder.ApplyConfiguration(new EntityConfigurations.ProviderMetadataDriftItemConfiguration());

            // Apply Provider configuration (structured Settings as jsonb)
            modelBuilder.ApplyConfiguration(new EntityConfigurations.ProviderEntityConfiguration());

            // Apply ProviderKeyCredential configuration (encrypted SecretSettings as jsonb)
            modelBuilder.ApplyConfiguration(new EntityConfigurations.ProviderKeyCredentialEntityConfiguration());

            // Note: ModelProviderMapping and Provider are now included in test environments
            // as they are required by the application code during tests
        }
    }
}
