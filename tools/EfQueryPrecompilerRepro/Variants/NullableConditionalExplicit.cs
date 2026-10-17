using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public static class NullableConditionalQuery
{
    public static Task<List<GenerationMetric>> ExecuteAsync(
        NullableConditionalContext suppliedContext, string suppliedTaskType, DateTime suppliedSince)
    {
        var context = suppliedContext;
        var taskType = suppliedTaskType;
        var since = suppliedSince;
        return context.Tasks
            .AsNoTracking()
            .Where(task => task.Type == taskType && task.CreatedAt >= since)
            .GroupBy(task => task.State)
            .Select(group => new GenerationMetric(
                group.Key,
                group.Count(),
                group.Where(task => task.CompletedAt.HasValue).Any()
                    ? (double?)group.Where(task => task.CompletedAt.HasValue)
                        .Average(task => (task.CompletedAt!.Value - task.CreatedAt).TotalSeconds)
                    : null))
            .ToListAsync();
    }
}

public class GenerationTask
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public int State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed record GenerationMetric(int State, int Count, double? AverageDurationSeconds);

public sealed class NullableConditionalContext(DbContextOptions<NullableConditionalContext> options) : DbContext(options)
{
    public DbSet<GenerationTask> Tasks { get; set; } = null!;
}

public sealed class NullableConditionalContextFactory : IDesignTimeDbContextFactory<NullableConditionalContext>
{
    public NullableConditionalContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<NullableConditionalContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}
