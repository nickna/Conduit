using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public static class ProviderSyncQuery
{
    public static Task<SyncMapping?> ExecuteAsync(SyncContext suppliedContext)
    {
        var db = suppliedContext;
        var item = new SyncItem { ModelProviderMappingId = 7 };
        return db.Mappings.FirstOrDefaultAsync(m => m.Id == item.ModelProviderMappingId);
    }
}

public class SyncItem
{
    public int ModelProviderMappingId { get; set; }
}

public class SyncMapping
{
    public int Id { get; set; }
}

public sealed class SyncContext(DbContextOptions<SyncContext> options) : DbContext(options)
{
    public DbSet<SyncMapping> Mappings { get; set; } = null!;
}

public sealed class SyncContextFactory : IDesignTimeDbContextFactory<SyncContext>
{
    public SyncContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SyncContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
