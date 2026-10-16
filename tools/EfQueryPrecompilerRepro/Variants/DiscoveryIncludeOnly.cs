using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public class DiscoveryMapping
{
    public int Id { get; set; }
    public string ModelAlias { get; set; } = "";
    public bool IsEnabled { get; set; }
    public int? AssociationId { get; set; }
    public DiscoveryAssociation? Association { get; set; }
}

public class DiscoveryAssociation
{
    public int Id { get; set; }
    public int? ModelId { get; set; }
    public DiscoveryModel? Model { get; set; }
}

public class DiscoveryModel
{
    public int Id { get; set; }
    public int? SeriesId { get; set; }
    public DiscoverySeries? Series { get; set; }
}

public class DiscoverySeries
{
    public int Id { get; set; }
}

public sealed class DiscoveryContext(DbContextOptions<DiscoveryContext> options) : DbContext(options)
{
    public DbSet<DiscoveryMapping> Mappings { get; set; } = null!;
}

public sealed class DiscoveryContextFactory : IDesignTimeDbContextFactory<DiscoveryContext>
{
    public DiscoveryContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<DiscoveryContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1").Options);
}

public static class DiscoveryQuery
{
    public static Task<DiscoveryMapping?> ExecuteAsync(DiscoveryContext suppliedContext, string model)
    {
        var context = suppliedContext;
        var alias = model;
        return context.Mappings
            .Include(mapping => mapping.Association)
            .AsNoTracking()
            .Where(mapping => mapping.ModelAlias == alias && mapping.IsEnabled)
            .FirstOrDefaultAsync();
    }
}
