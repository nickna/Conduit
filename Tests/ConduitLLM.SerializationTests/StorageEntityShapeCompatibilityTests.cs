using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Migrations;
using ConduitLLM.Functions.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ConduitLLM.SerializationTests;

public sealed class StorageEntityShapeCompatibilityTests
{
    [Fact]
    public void Fluent_storage_shapes_preserve_the_existing_migration_contract()
    {
        using var context = new ConduitDbContext(new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql("Host=localhost;Database=shape_contract;Username=unused;Password=unused")
            .Options);
        var current = context.GetService<IDesignTimeModel>().Model;
        var snapshot = new ConduitDbContextModelSnapshot().Model;
        Type[] entities = [typeof(AsyncTask), typeof(RequestLog), typeof(BatchOperationHistory),
            typeof(BillingAuditEvent), typeof(RefundIdempotencyRecord),
            typeof(WebhookDeliveryRecord), typeof(WebhookReplayAudit), typeof(MediaRecord),
            typeof(MediaRetentionPolicy), typeof(ModelAuthor), typeof(ModelCost),
            typeof(ModelProviderMapping), typeof(ModelProviderTypeAssociation), typeof(ModelRoutePolicy),
            typeof(Notification), typeof(PricingAuditEvent), typeof(ProviderMetadataDriftItem),
            typeof(ProviderMetadataSyncRun), typeof(ProviderTool), typeof(VirtualKey),
            typeof(VirtualKeyGroup), typeof(VirtualKeyGroupTransaction), typeof(GlobalSetting),
            typeof(IpFilterEntity), typeof(Provider), typeof(FunctionCallAudit), typeof(FunctionConfiguration),
            typeof(FunctionCredential), typeof(FunctionCost), typeof(FunctionExecution), typeof(FunctionExecutionAudit)];

        foreach (var type in entities)
        {
            var expected = Assert.IsAssignableFrom<IEntityType>(snapshot.FindEntityType(type.FullName!));
            var actual = Assert.IsAssignableFrom<IEntityType>(current.FindEntityType(type));
            foreach (var property in expected.GetProperties())
            {
                var actualProperty = Assert.IsAssignableFrom<IProperty>(actual.FindProperty(property.Name));
                Assert.Equal(property.GetMaxLength(), actualProperty.GetMaxLength());
                Assert.Equal(property.IsNullable, actualProperty.IsNullable);
            }
        }
    }
}
