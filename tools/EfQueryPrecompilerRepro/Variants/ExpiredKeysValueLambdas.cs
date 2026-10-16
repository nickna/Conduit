using Microsoft.EntityFrameworkCore;

namespace EfQueryPrecompilerRepro;

public static class ReproQuery
{
    public static async Task<int> ExecuteAsync(ReproContext suppliedContext)
    {
        var context = suppliedContext;
        var now = DateTime.UtcNow;
        return await context.VirtualKeys
            .Where(vk => vk.IsEnabled && vk.ExpiresAt != null && vk.ExpiresAt < now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(vk => vk.IsEnabled, vk => false)
                .SetProperty(vk => vk.UpdatedAt, vk => now));
    }
}
