using ConduitLLM.Configuration.Extensions;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Extensions;

public sealed class DataProtectionCompositionTests
{
    [Fact]
    public void RedisPersistenceUsesTheHostConnectionWithoutBuildingAnIntermediateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var database = new Mock<IDatabase>();
        database.Setup(db => db.ListRange("composition-DataProtection-Keys", 0, -1, CommandFlags.None))
            .Returns(Array.Empty<RedisValue>());
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(connection => connection.GetDatabase(-1, null)).Returns(database.Object);
        var creations = 0;
        services.AddSingleton<IConnectionMultiplexer>(_ => { creations++; return redis.Object; });

        services.AddRedisDataProtection("unused-host:6379", "composition");

        Assert.Equal(0, creations);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
        Assert.IsType<RedisXmlRepository>(repository);
        Assert.Empty(repository!.GetAllElements());
        Assert.Equal(1, creations);
        redis.Verify(connection => connection.GetDatabase(-1, null), Times.Once);
        database.Verify(db => db.ListRange("composition-DataProtection-Keys", 0, -1, CommandFlags.None), Times.Once);
    }

    [Fact]
    public void LocalPersistenceDoesNotRegisterARedisConnection()
    {
        var services = new ServiceCollection();
        services.AddRedisDataProtection(null);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        Assert.Null(provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
    }
}
