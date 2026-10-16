using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EfQueryPrecompilerRepro;

public enum ToolProvider { OpenAI = 1, Anthropic = 2 }

public sealed class ProviderTool
{
    public int Id { get; set; }
    public ToolProvider Provider { get; set; }
    public string ToolName { get; set; } = "";
}

public sealed class ProviderToolDto
{
    public ToolProvider Provider { get; set; }
    public string ToolName { get; set; } = "";
}

public sealed class ToolsContext(DbContextOptions<ToolsContext> options) : DbContext(options)
{
    public DbSet<ProviderTool> ProviderTools { get; set; } = null!;
}

public sealed class ToolsContextFactory : IDesignTimeDbContextFactory<ToolsContext>
{
    public ToolsContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ToolsContext>()
            // Design-time analysis only: no server and no credentials.
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ef_query_repro;Pooling=false;Timeout=1")
            .Options);
}

public sealed class ProviderToolsEndpoint(ToolsContext context)
{
    private readonly ToolsContext _context = context;

    public async Task<bool> ExecuteAsync(List<ProviderToolDto> tools)
    {
        var context = _context;
        foreach (var dto in tools)
        {
            if (await context.ProviderTools
                .AnyAsync(pt => pt.Provider == dto.Provider && pt.ToolName == dto.ToolName))
                return true;
        }
        return false;
    }
}
