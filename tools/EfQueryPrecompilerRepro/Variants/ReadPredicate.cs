using Microsoft.EntityFrameworkCore;

namespace EfQueryPrecompilerRepro;

public static class ReproQuery
{
    public static Task<List<VirtualKey>> ExecuteAsync(ReproContext suppliedContext)
    {
        var context = suppliedContext;
        var now = DateTime.UtcNow;
        return context.VirtualKeys
            .Where(vk => vk.IsEnabled && vk.ExpiresAt != null && vk.ExpiresAt < now)
            .ToListAsync();
    }
}
