using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public sealed class VirtualKey
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ReproContext(DbContextOptions<ReproContext> options) : DbContext(options)
{
    public DbSet<VirtualKey> VirtualKeys { get; set; } = null!;
}

public sealed class ReproContextFactory : IDesignTimeDbContextFactory<ReproContext>
{
    public ReproContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ReproContext>()
            // No credentials and no server. Building the model requires no connection.
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
