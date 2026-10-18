using ConduitLLM.Configuration.Entities;
using ConduitLLM.Functions.Entities;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.EntityConfigurations;

/// <summary>Database constraints for persisted entities, separate from API request validation.</summary>
internal static class StorageEntityShapeConfiguration
{
    public static void ApplyStorageEntityShapes(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AsyncTask>(entity =>
        {
            entity.Property(e => e.Id).HasMaxLength(50);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ProgressMessage).HasMaxLength(500);
            entity.Property(e => e.LeasedBy).HasMaxLength(100);
            entity.Property(e => e.ProviderOperationId).HasMaxLength(200);
            entity.Property(e => e.RetryDispatchId).HasMaxLength(64);
        });

        modelBuilder.Entity<RequestLog>(entity =>
        {
            entity.Property(e => e.ModelName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ProviderType).HasMaxLength(50);
            entity.Property(e => e.RoutingDecisionReason).HasMaxLength(50);
            entity.Property(e => e.RequestType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.UserId).HasMaxLength(100);
            entity.Property(e => e.ClientIp).HasMaxLength(50);
            entity.Property(e => e.RequestPath).HasMaxLength(256);
        });

        modelBuilder.Entity<BatchOperationHistory>(entity =>
        {
            entity.Property(e => e.OperationId).HasMaxLength(50);
            entity.Property(e => e.OperationType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
        });

        modelBuilder.Entity<BillingAuditEvent>(entity =>
        {
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.RequestId).HasMaxLength(100);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.ProviderType).HasMaxLength(50);
            entity.Property(e => e.RequestPath).HasMaxLength(256);
        });

        modelBuilder.Entity<RefundIdempotencyRecord>(entity =>
        {
            entity.Property(e => e.OperationId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.RequestHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ResponseJson).IsRequired();
        });

        modelBuilder.Entity<WebhookDeliveryRecord>(entity =>
        {
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.EventId).HasMaxLength(128);
            entity.Property(e => e.TaskId).HasMaxLength(50);
            entity.Property(e => e.State).HasMaxLength(16);
            entity.Property(e => e.LastError).HasMaxLength(512);
        });

        modelBuilder.Entity<WebhookReplayAudit>(entity =>
        {
            entity.Property(e => e.DeliveryId).HasMaxLength(64);
            entity.Property(e => e.Actor).HasMaxLength(128);
        });

        modelBuilder.Entity<MediaRecord>(entity =>
        {
            entity.Property(e => e.StorageKey).HasMaxLength(500);
            entity.Property(e => e.MediaType).HasMaxLength(50);
            entity.Property(e => e.ContentType).HasMaxLength(100);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.Model).HasMaxLength(100);
        });

        modelBuilder.Entity<MediaRetentionPolicy>(entity =>
        {
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<ModelAuthor>(entity =>
        {
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.WebsiteUrl).HasMaxLength(500);
        });

        modelBuilder.Entity<ModelCost>(entity =>
        {
            entity.Property(e => e.CostName).HasMaxLength(255);
            entity.Property(e => e.ModelType).HasMaxLength(50);
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<ModelProviderMapping>(entity =>
        {
            entity.Property(e => e.ModelAlias).HasMaxLength(100);
            entity.Property(e => e.ProviderModelId).HasMaxLength(100);
        });

        modelBuilder.Entity<ModelProviderTypeAssociation>()
            .Property(e => e.Identifier).HasMaxLength(200);

        modelBuilder.Entity<ModelRoutePolicy>(entity =>
        {
            entity.Property(e => e.ModelAlias).HasMaxLength(100);
            entity.Property(e => e.Strategy).HasMaxLength(30);
        });

        modelBuilder.Entity<Notification>()
            .Property(e => e.Message).HasMaxLength(500);

        // PricingAuditEvent's three lengths already live in PricingAuditEventConfiguration.
        modelBuilder.Entity<ProviderMetadataDriftItem>(entity =>
        {
            entity.Property(e => e.OpenRouterModelId).HasMaxLength(200);
            entity.Property(e => e.ResolvedBy).HasMaxLength(100);
        });

        modelBuilder.Entity<ProviderMetadataSyncRun>(entity =>
        {
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.TriggeredBy).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
        });

        modelBuilder.Entity<ProviderTool>(entity =>
        {
            entity.Property(e => e.BillingUnit).HasMaxLength(50);
            entity.Property(e => e.CostDescription).HasMaxLength(200);
        });

        modelBuilder.Entity<VirtualKey>(entity =>
        {
            entity.Property(e => e.KeyName).HasMaxLength(100);
            entity.Property(e => e.KeyHash).HasMaxLength(128);
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<VirtualKeyGroup>(entity =>
        {
            entity.Property(e => e.ExternalGroupId).HasMaxLength(100);
            entity.Property(e => e.GroupName).HasMaxLength(100);
        });

        modelBuilder.Entity<VirtualKeyGroupTransaction>(entity =>
        {
            entity.Property(e => e.OriginalAmountVerifiedBy).HasMaxLength(100);
            entity.Property(e => e.OriginalAmountEvidence).HasMaxLength(500);
            entity.Property(e => e.ReferenceId).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.InitiatedBy).HasMaxLength(50);
            entity.Property(e => e.InitiatedByUserId).HasMaxLength(100);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(100);
        });

        modelBuilder.Entity<GlobalSetting>(entity =>
        {
            entity.Property(e => e.Key).HasMaxLength(100);
            entity.Property(e => e.Value).HasMaxLength(2000);
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<IpFilterEntity>(entity =>
        {
            entity.Property(e => e.FilterType).HasMaxLength(10);
            entity.Property(e => e.IpAddressOrCidr).HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.CreatedBy).HasMaxLength(100);
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);
        });

        modelBuilder.Entity<Provider>()
            .Property(e => e.ProviderName).HasMaxLength(100);

        modelBuilder.Entity<FunctionCallAudit>(entity =>
        {
            entity.Property(e => e.FailureReason).HasMaxLength(2000);
            entity.Property(e => e.RequestId).HasMaxLength(100);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);
        });

        modelBuilder.Entity<FunctionConfiguration>(entity =>
        {
            entity.Property(e => e.ConfigurationName).HasMaxLength(200);
            entity.Property(e => e.BaseUrl).HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<FunctionCredential>(entity =>
        {
            entity.Property(e => e.ApiKey).HasMaxLength(2000);
            entity.Property(e => e.BaseUrl).HasMaxLength(500);
            entity.Property(e => e.Organization).HasMaxLength(200);
            entity.Property(e => e.KeyName).HasMaxLength(200);
        });

        modelBuilder.Entity<FunctionCost>(entity =>
        {
            entity.Property(e => e.CostName).HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<FunctionExecution>(entity =>
        {
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.LeasedBy).HasMaxLength(100);
            entity.Property(e => e.WebhookUrl).HasMaxLength(1000);
            entity.Property(e => e.StatusMessage).HasMaxLength(500);
        });

        modelBuilder.Entity<FunctionExecutionAudit>()
            .Property(e => e.FailureReason).HasMaxLength(2000);
    }
}
