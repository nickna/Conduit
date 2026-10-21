using System.Reflection;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Exceptions;
using ConduitLLM.Configuration.Interfaces;

namespace ConduitLLM.Tests.Architecture;

public sealed class ConfigurationContractBoundaryTests
{
    [Fact]
    public void ExtractedConfigurationContractsRetainTheirOriginalAssemblyIdentity()
    {
        var implementation = typeof(ConduitDbContext).Assembly;
        var forwarded = implementation.GetForwardedTypes();
        var extractedContracts = new[] { typeof(ModelProviderMapping).Assembly, Assembly.Load("ConduitLLM.Contracts") }
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.Namespace == "ConduitLLM.Configuration" ||
                           type.Namespace?.StartsWith("ConduitLLM.Configuration.", StringComparison.Ordinal) == true ||
                           type.Namespace?.StartsWith("ConduitLLM.Core.Events", StringComparison.Ordinal) == true ||
                           type.FullName is "ConduitLLM.Configuration.ConduitSettings" or
                               "ConduitLLM.Configuration.PerformanceTrackingSettings" or
                               "ConduitLLM.Core.Models.CacheStats" or "TokenizerType")
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(extractedContracts, forwarded.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray());
        var representativeContracts = new[]
        {
            typeof(ModelProviderMapping), typeof(ConduitSettings),
            typeof(IModelProviderMappingService), typeof(SpendUpdateNotification),
            typeof(BillingSystemException), typeof(IRepositoryBase<,>)
        };
        foreach (var contract in representativeContracts)
        {
            Assert.Contains(contract, forwarded);
        }

        var implementationTypes = implementation.GetTypes().Select(type => type.FullName).ToHashSet();
        foreach (var contract in forwarded)
        {
            Assert.Contains(contract.Assembly.GetName().Name, new[]
            {
                "ConduitLLM.Contracts", "ConduitLLM.Persistence.Abstractions"
            });
            Assert.Same(contract, Type.GetType($"{contract.FullName}, {implementation.FullName}", throwOnError: true));
            Assert.DoesNotContain(contract.FullName, implementationTypes);
        }
    }

    [Fact]
    public void EfServicesAndRegistrationHelpersBelongToPersistenceComposition()
    {
        var assembly = Assembly.Load("ConduitLLM.Persistence.Composition");
        foreach (var name in new[]
        {
            "ConduitLLM.Core.Services.DiscoveryModelProjector",
            "ConduitLLM.Core.Data.DatabaseConnectionFactory",
            "ConduitLLM.Core.Services.PostgresDistributedLockProvider",
            "ConduitLLM.Core.Extensions.DistributedLockServiceExtensions"
        })
        {
            Assert.NotNull(assembly.GetType(name, throwOnError: true));
            Assert.Null(Assembly.Load("ConduitLLM.Core").GetType(name));
        }
    }
}
