using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public static class GuardedNullableIdQuery
{
    public static async Task<List<GuardedGroupSnapshot>> ExecuteAsync(
        GuardedNullableIdContext suppliedContext, int? suppliedGroupId)
    {
        var context = suppliedContext;
        var groupId = suppliedGroupId;
        var queryGroupId = groupId;
        var groups = groupId.HasValue
            ? await context.Groups
                .AsNoTracking()
                .Where(group => group.Id == queryGroupId.Value)
                .Select(group => new { group.Id, group.GroupName, group.MediaRetentionPolicyId })
                .ToListAsync()
            : await context.Groups
                .AsNoTracking()
                .Select(group => new { group.Id, group.GroupName, group.MediaRetentionPolicyId })
                .ToListAsync();
        return groups.Select(group => new GuardedGroupSnapshot(
            group.Id, group.GroupName, group.MediaRetentionPolicyId)).ToList();
    }
}

public class GuardedGroup
{
    public int Id { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public int? MediaRetentionPolicyId { get; set; }
}

public sealed record GuardedGroupSnapshot(int Id, string GroupName, int? MediaRetentionPolicyId);

public class GuardedNullableIdContext(DbContextOptions<GuardedNullableIdContext> options) : DbContext(options)
{
    public DbSet<GuardedGroup> Groups { get; set; } = null!;
}

public class GuardedNullableIdContextFactory : IDesignTimeDbContextFactory<GuardedNullableIdContext>
{
    public GuardedNullableIdContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<GuardedNullableIdContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
