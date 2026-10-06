using System.ComponentModel.DataAnnotations;
using System.Reflection;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.OpenApi;
using ConduitLLM.Core.Services;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Tests.Architecture;

/// <summary>
/// Guards the optional dependency seams used by trimmed and Native AOT hosts.
/// These assertions intentionally inspect emitted assembly references: a package
/// may exist in the repository without becoming part of a service's runtime graph.
/// </summary>
public sealed class AotDependencyBoundaryTests
{
    [Fact]
    public void ContractsRemainTransportAndPersistenceNeutral()
    {
        AssertDoesNotReference(
            typeof(IEventBus).Assembly,
            "ConduitLLM.Functions",
            "Microsoft.EntityFrameworkCore",
            "Wolverine",
            "Microsoft.AspNetCore.SignalR",
            "AWSSDK",
            "Amazon.",
            "Microsoft.OpenApi");
    }

    [Fact]
    public void CoreDoesNotOwnOptionalRuntimeAdapters()
    {
        AssertDoesNotReference(
            typeof(MediaLifecycleService).Assembly,
            "Wolverine",
            "Microsoft.AspNetCore.SignalR",
            "Microsoft.AspNetCore.SignalR.Protocols.MessagePack",
            "MessagePack",
            "AWSSDK",
            "Amazon.",
            "Microsoft.OpenApi",
            "Microsoft.ML.Tokenizers");
    }

    [Fact]
    public void PersistenceDoesNotOwnMessagingOrBuildTooling()
    {
        AssertDoesNotReference(
            typeof(ConduitLLM.Configuration.ConduitDbContext).Assembly,
            "Wolverine",
            "RabbitMQ.Client",
            "Microsoft.Build");
    }

    [Fact]
    public void AdminDoesNotDirectlyOwnGatewayOnlyAdapters()
    {
        AssertDoesNotReference(
            typeof(ConduitLLM.Admin.Services.MediaCleanupStatusService).Assembly,
            "ConduitLLM.SignalR",
            "ConduitLLM.Tokenization",
            "Microsoft.AspNetCore.SignalR",
            "MessagePack",
            "Microsoft.ML.Tokenizers",
            "AWSSDK",
            "Amazon.");
    }

    [Fact]
    public void OptionalFeaturesAreOwnedByDedicatedAssemblies()
    {
        Assert.Equal("ConduitLLM.Messaging.Wolverine", typeof(WolverineEventBus).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.SignalR", typeof(SignalRConfigurationExtensions).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.OpenApi", typeof(OperationMetadataTransformer).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.Tokenization", typeof(TiktokenCounter).Assembly.GetName().Name);
        Assert.Equal("ConduitLLM.Media", typeof(S3MediaStorageService).Assembly.GetName().Name);
    }

    [Fact]
    public void WebServicesCannotReferenceSchemaMutationExecutable()
    {
        AssertDoesNotReference(typeof(ConduitLLM.Admin.Program).Assembly, "ConduitLLM.Migrator");
        AssertDoesNotReference(Assembly.Load("ConduitLLM.Gateway"), "ConduitLLM.Migrator");
        Assert.Equal("ConduitLLM.Migrator", typeof(ConduitLLM.Configuration.Data.SimpleMigrationService).Assembly.GetName().Name);
    }

    [Fact]
    public void RepositoryAbstractionsDoNotExposeQueryable()
    {
        var repositoryInterfaces = typeof(IProviderRepository).Assembly
            .GetExportedTypes()
            .Where(type => type.IsInterface && type.Name.EndsWith("Repository", StringComparison.Ordinal));

        foreach (var repositoryInterface in repositoryInterfaces)
        {
            foreach (var method in repositoryInterface.GetMethods())
            {
                Assert.False(ContainsQueryable(method.ReturnType), $"{repositoryInterface.Name}.{method.Name} returns IQueryable");
                Assert.DoesNotContain(method.GetParameters(), parameter => ContainsQueryable(parameter.ParameterType));
            }
        }
    }

    [Fact]
    public void CompiledSchemaVersionMatchesLatestMigration()
    {
        var options = new DbContextOptionsBuilder<ConduitLLM.Configuration.ConduitDbContext>()
            .UseNpgsql("Host=localhost;Database=schema_inventory;Username=unused;Password=unused")
            .Options;
        using var context = new ConduitLLM.Configuration.ConduitDbContext(options);

        Assert.Equal(ConduitLLM.Configuration.Data.ConduitSchemaVersion.Current, context.Database.GetMigrations().Last());
    }

    [Fact]
    public void ProductionAssembliesDoNotUseReflectionBasedMaxLengthValidation()
    {
        var assemblyNames = new[]
        {
            "ConduitLLM.Admin",
            "ConduitLLM.Configuration",
            "ConduitLLM.Core",
            "ConduitLLM.Functions",
            "ConduitLLM.Gateway",
            "ConduitLLM.Providers",
            "ConduitLLM.Security"
        };
        var offenders = assemblyNames
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMembers(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(member => member.CustomAttributes.Any(attribute =>
                attribute.AttributeType == typeof(MaxLengthAttribute)))
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .OrderBy(name => name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void StringLengthValidationPreservesEfMaximumLengthMetadata()
    {
        var options = new DbContextOptionsBuilder<ConduitLLM.Configuration.ConduitDbContext>()
            .UseNpgsql("Host=localhost;Database=schema_inventory;Username=unused;Password=unused")
            .Options;
        using var context = new ConduitLLM.Configuration.ConduitDbContext(options);

        var keyName = context.Model.FindEntityType(typeof(VirtualKey))!
            .FindProperty(nameof(VirtualKey.KeyName));

        Assert.NotNull(keyName);
        Assert.Equal(100, keyName.GetMaxLength());
    }

    private static bool ContainsQueryable(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
        {
            return true;
        }

        return type.HasElementType && ContainsQueryable(type.GetElementType()!)
            || type.IsGenericType && type.GetGenericArguments().Any(ContainsQueryable);
    }

    private static void AssertDoesNotReference(Assembly assembly, params string[] forbiddenPrefixes)
    {
        var references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        foreach (var forbiddenPrefix in forbiddenPrefixes)
        {
            Assert.DoesNotContain(
                references,
                reference => reference.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
