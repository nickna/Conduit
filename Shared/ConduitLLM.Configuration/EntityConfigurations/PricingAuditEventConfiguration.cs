using ConduitLLM.Configuration.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConduitLLM.Configuration.EntityConfigurations;

/// <summary>
/// Entity configuration for PricingAuditEvent.
/// </summary>
public class PricingAuditEventConfiguration : IEntityTypeConfiguration<PricingAuditEvent>
{
    /// <summary>
    /// Configures the PricingAuditEvent entity.
    /// </summary>
    public void Configure(EntityTypeBuilder<PricingAuditEvent> builder)
    {
        builder.ToTable("PricingAuditEvents");

        builder.HasKey(e => e.Id);

        // Auto-increment primary key
        builder.Property(e => e.Id)
            .ValueGeneratedOnAdd();

        // Indexes for query performance
        builder.HasIndex(e => e.Timestamp)
            .HasDatabaseName("IX_PricingAuditEvents_Timestamp");

        builder.HasIndex(e => e.VirtualKeyId)
            .HasDatabaseName("IX_PricingAuditEvents_VirtualKeyId");

        builder.HasIndex(e => e.ModelId)
            .HasDatabaseName("IX_PricingAuditEvents_ModelId");

        builder.HasIndex(e => e.PricingType)
            .HasDatabaseName("IX_PricingAuditEvents_PricingType");

        builder.HasIndex(e => e.RequestId)
            .HasDatabaseName("IX_PricingAuditEvents_RequestId");

        // Composite indexes for common queries
        builder.HasIndex(e => new { e.VirtualKeyId, e.Timestamp })
            .HasDatabaseName("IX_PricingAuditEvents_VirtualKeyId_Timestamp");

        builder.HasIndex(e => new { e.ModelId, e.Timestamp })
            .HasDatabaseName("IX_PricingAuditEvents_ModelId_Timestamp");

        builder.HasIndex(e => new { e.PricingType, e.Timestamp })
            .HasDatabaseName("IX_PricingAuditEvents_PricingType_Timestamp");

        // Note: Cleanup queries use the IX_PricingAuditEvents_Timestamp index defined above
        // with a WHERE clause at query time. PostgreSQL doesn't allow CURRENT_TIMESTAMP in partial index filters.

        // Property configurations
        builder.Property(e => e.Timestamp)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(e => e.ModelId)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.PricingType)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.RequestId)
            .HasMaxLength(100);

        // Configure JSONB columns for PostgreSQL
        builder.Property(e => e.InputParameters)
            .HasColumnType("jsonb")
            .HasDefaultValue("{}");

        builder.Property(e => e.MatchedRule)
            .HasColumnType("jsonb");

        // Configure decimal precision
        builder.Property(e => e.AppliedRate)
            .HasColumnType("decimal(10, 8)")
            .IsRequired();

        builder.Property(e => e.Quantity)
            .HasColumnType("decimal(10, 4)")
            .IsRequired();

        builder.Property(e => e.CalculatedCost)
            .HasColumnType("decimal(12, 8)")
            .IsRequired();

        // Foreign key relationship
        builder.HasOne(e => e.VirtualKey)
            .WithMany()
            .HasForeignKey(e => e.VirtualKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
