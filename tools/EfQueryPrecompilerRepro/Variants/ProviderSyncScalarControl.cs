using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public static class ProviderSyncQuery
{
    public static async Task<SyncMapping?> ExecuteAsync(SyncContext suppliedContext, int suppliedId)
    {
        var db = suppliedContext;
        var id = suppliedId;
        var item = await db.DriftItems.FirstOrDefaultAsync(x => x.Id == id);
        if (item == null)
            return null;

        var mappingId = item.ModelProviderMappingId;
        return await db.Mappings.FirstOrDefaultAsync(m => m.Id == mappingId);
    }
}

public class SyncItem
{
    public int Id { get; set; }
    public int ModelProviderMappingId { get; set; }
}

public class SyncMapping
{
    public int Id { get; set; }
}

public sealed class SyncContext(DbContextOptions<SyncContext> options) : DbContext(options)
{
    public DbSet<SyncItem> DriftItems { get; set; } = null!;
    public DbSet<SyncMapping> Mappings { get; set; } = null!;
}

public sealed class SyncContextFactory : IDesignTimeDbContextFactory<SyncContext>
{
    public SyncContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SyncContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
