using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public class BillingCheckpoint
{
    public int Id { get; set; }
}

public sealed class BillingContext(DbContextOptions<BillingContext> options) : DbContext(options)
{
    public DbSet<BillingCheckpoint> Checkpoints { get; set; } = null!;
}

public sealed class BillingContextFactory : IDesignTimeDbContextFactory<BillingContext>
{
    public BillingContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<BillingContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1").Options);
}

public static class BillingQuery
{
    private const int CheckpointId = 1;

    public static Task<BillingCheckpoint?> ExecuteAsync(BillingContext suppliedContext, CancellationToken cancellationToken)
    {
        var context = suppliedContext;
        return context.Checkpoints.SingleOrDefaultAsync(x => x.Id == 1);
    }
}
