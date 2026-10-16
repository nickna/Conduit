using Microsoft.EntityFrameworkCore;

namespace EfQueryPrecompilerRepro;

public static class ReproQuery
{
    public static Task<int> ExecuteAsync(ReproContext suppliedContext)
    {
        var context = suppliedContext;
        return context.VirtualKeys
            .ExecuteUpdateAsync(setters => setters.SetProperty(vk => vk.IsEnabled, vk => false));
    }
}
