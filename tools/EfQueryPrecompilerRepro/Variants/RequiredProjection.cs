using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public static class RequiredProjectionQuery
{
    public static Task<RequiredKeySnapshot?> ExecuteAsync(RequiredProjectionContext suppliedContext, int suppliedId)
    {
        var context = suppliedContext;
        var id = suppliedId;
        return context.Keys.Where(key => key.Id == id)
            .Select(key => new RequiredKeySnapshot
            {
                Id = key.Id,
                GroupId = key.GroupId,
                Group = new RequiredGroupSnapshot { Id = key.Group.Id, Balance = key.Group.Balance }
            })
            .FirstOrDefaultAsync();
    }
}

public class RequiredProjectionKey
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public RequiredProjectionGroup Group { get; set; } = null!;
}

public class RequiredProjectionGroup
{
    public int Id { get; set; }
    public decimal Balance { get; set; }
}

public sealed class RequiredKeySnapshot
{
    public int Id { get; init; }
    public int GroupId { get; init; }
    public required RequiredGroupSnapshot Group { get; init; }
}

public sealed class RequiredGroupSnapshot
{
    public int Id { get; init; }
    public decimal Balance { get; init; }
}

public sealed class RequiredProjectionContext(DbContextOptions<RequiredProjectionContext> options) : DbContext(options)
{
    public DbSet<RequiredProjectionKey> Keys { get; set; } = null!;
    public DbSet<RequiredProjectionGroup> Groups { get; set; } = null!;
}

public sealed class RequiredProjectionContextFactory : IDesignTimeDbContextFactory<RequiredProjectionContext>
{
    public RequiredProjectionContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<RequiredProjectionContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
