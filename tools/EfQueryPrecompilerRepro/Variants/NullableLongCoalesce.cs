using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public static class NullableLongCoalesceQuery
{
    public static async Task<(int TotalFiles, long TotalSizeBytes)> ExecuteAsync(
        MediaLongContext suppliedContext, int suppliedGroupId, CancellationToken suppliedToken)
    {
        var context = suppliedContext;
        var queryGroupId = suppliedGroupId;
        var queryToken = suppliedToken;
        var row = await context.MediaRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Join(
                context.VirtualKeys.AsNoTracking().Where(key => key.VirtualKeyGroupId == queryGroupId),
                media => media.VirtualKeyId,
                key => key.Id,
                (media, _) => new { SizeBytes = (media.SizeBytes ?? 0L) })
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalFiles = group.Count(),
                TotalSizeBytes = group.Sum(item => item.SizeBytes)
            })
            .SingleOrDefaultAsync(queryToken);
        return row is null ? (0, 0L) : (row.TotalFiles, row.TotalSizeBytes);
    }
}

public class MediaLongRecord
{
    public Guid Id { get; set; }
    public int VirtualKeyId { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class MediaLongVirtualKey
{
    public int Id { get; set; }
    public int VirtualKeyGroupId { get; set; }
}

public sealed class MediaLongContext(DbContextOptions<MediaLongContext> options) : DbContext(options)
{
    public DbSet<MediaLongRecord> MediaRecords { get; set; } = null!;
    public DbSet<MediaLongVirtualKey> VirtualKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaLongRecord>().HasQueryFilter(media => media.DeletedAt == null);
        modelBuilder.Entity<MediaLongRecord>().HasOne<MediaLongVirtualKey>().WithMany()
            .HasForeignKey(media => media.VirtualKeyId);
    }
}

public sealed class MediaLongContextFactory : IDesignTimeDbContextFactory<MediaLongContext>
{
    public MediaLongContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<MediaLongContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
