using ConduitLLM.Configuration;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Tests.Core.Extensions;

public sealed class DistributedLockCompositionTests
{
    [Fact]
    public void SelectedAdapterIsSingleton_WithoutCapturingScopedDbContext()
    {
        var adapter = new PostgresDistributedLockProvider("Host=127.0.0.1;Port=1;Database=fixture;Username=fixture",
            NullLogger<PostgresDistributedLockProvider>.Instance);
        var services = new ServiceCollection().AddLogging();
        services.AddScoped<ConduitDbContext>();
        services.AddSingleton(adapter);
        services.AddConduitDistributedLocks();
        services.AddConduitDistributedLocks();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.Same(adapter, provider.GetRequiredService<PostgresDistributedLockProvider>());
        Assert.Same(adapter, scope.ServiceProvider.GetRequiredService<PostgresDistributedLockProvider>());
        Assert.Same(adapter, provider.GetRequiredService<IDistributedLockProvider>());
        Assert.Same(adapter, scope.ServiceProvider.GetRequiredService<IDistributedLockProvider>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDistributedLockProvider));
    }
}
