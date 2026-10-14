using ConduitLLM.Configuration;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Security;

namespace ConduitLLM.Tests.Architecture;

public sealed class FunctionPersistenceBoundaryTests
{
    [Fact]
    public void PersistenceDoesNotReferenceFunctionImplementations()
    {
        var references = typeof(ConduitDbContext).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name == "ConduitLLM.Functions" ||
            reference.Name!.StartsWith("ModelContextProtocol", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryExtractedFunctionContractRetainsItsOriginalAssemblyIdentity()
    {
        var owner = typeof(FunctionConfiguration).Assembly;
        Assert.Equal("ConduitLLM.Persistence.Abstractions", owner.GetName().Name);
        var contracts = owner.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("ConduitLLM.Functions.", StringComparison.Ordinal) == true)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(24, contracts.Length);
        var oldOwner = typeof(FunctionCredentialProtector).Assembly;
        Assert.Equal(contracts, oldOwner.GetForwardedTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray());

        foreach (var contract in contracts)
        {
            var originalIdentity = $"{contract.FullName}, {oldOwner.FullName}";
            Assert.Same(contract, Type.GetType(originalIdentity, throwOnError: true));
            Assert.DoesNotContain(oldOwner.GetTypes(), type => type.FullName == contract.FullName);
        }
    }

    [Fact]
    public void FunctionPersistenceContractsRemainBackendAndRuntimeNeutral()
    {
        var references = typeof(FunctionConfiguration).Assembly.GetReferencedAssemblies();
        Assert.All(references, reference => Assert.False(
            reference.Name!.StartsWith("ConduitLLM.", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Npgsql", StringComparison.Ordinal) ||
            reference.Name.StartsWith("ModelContextProtocol", StringComparison.Ordinal),
            $"Neutral function contracts reference runtime implementation {reference.Name}."));
    }
}
