using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public static class DecimalLiteralQuery
{
    public static Task<decimal> ExecuteAsync(DecimalLiteralContext suppliedContext, int suppliedId, decimal suppliedAmount)
    {
        var context = suppliedContext;
        var id = suppliedId;
        var amount = suppliedAmount;
        return context.Groups.Where(group => group.Id == id)
            .Select(group => group.Balance + (amount > 0 ? amount : 0))
            .SingleAsync();
    }
}

public class DecimalLiteralGroup
{
    public int Id { get; set; }
    public decimal Balance { get; set; }
}

public sealed class DecimalLiteralContext(DbContextOptions<DecimalLiteralContext> options) : DbContext(options)
{
    public DbSet<DecimalLiteralGroup> Groups { get; set; } = null!;
}

public sealed class DecimalLiteralContextFactory : IDesignTimeDbContextFactory<DecimalLiteralContext>
{
    public DecimalLiteralContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<DecimalLiteralContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
