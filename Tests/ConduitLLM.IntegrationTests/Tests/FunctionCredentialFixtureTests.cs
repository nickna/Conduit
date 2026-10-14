using System.Security.Cryptography;
using System.Xml.Linq;

using ConduitLLM.Functions.Security;
using ConduitLLM.SchemaStartupFixtures;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;
using Testcontainers.Redis;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "FunctionCredentials")]
public sealed class FunctionCredentialFixtureTests(FunctionCredentialRedisFixture redisFixture)
    : IClassFixture<FunctionCredentialRedisFixture>
{
    [Fact]
    public void NullServicesFailBeforeRegistrationOrProtection()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() =>
            FunctionCredentialFixture.AddServices(null!, "unused:6379")).ParamName);
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() =>
            FunctionCredentialFixture.ProtectApiKey(null!, "fixture-key")).ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MissingRedisIsRejectedWithoutAddingServices(string? connectionString)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<ArgumentException>(() =>
            FunctionCredentialFixture.AddServices(services, connectionString!));
        Assert.Equal("redisConnectionString", error.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void RegistrationUsesProductionProtectorAndApplicationWithLazyOwnedRedisAndNoLogProviders()
    {
        var services = new ServiceCollection();
        Assert.Same(services, FunctionCredentialFixture.AddServices(services,
            "unreachable.invalid:1,connectTimeout=1,syncTimeout=1,asyncTimeout=1,connectRetry=9"));
        var redis = Assert.Single(services, value => value.ServiceType == typeof(IConnectionMultiplexer));
        Assert.Equal(ServiceLifetime.Singleton, redis.Lifetime);
        Assert.NotNull(redis.ImplementationFactory);
        Assert.Null(redis.ImplementationType);
        var protector = Assert.Single(services, value => value.ServiceType == typeof(IFunctionCredentialProtector));
        Assert.Equal(ServiceLifetime.Singleton, protector.Lifetime);
        Assert.Equal(typeof(FunctionCredentialProtector), protector.ImplementationType);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.Equal("Conduit", provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        Assert.Empty(provider.GetServices<ILoggerProvider>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("enc:v1:already-protected-fixture")]
    public void InvalidPlaintextIsRejectedBeforeResolvingProtection(string? plaintext)
    {
        var provider = new UnavailableServices();
        var error = Assert.Throws<ArgumentException>(() =>
            FunctionCredentialFixture.ProtectApiKey(provider, plaintext!));
        Assert.Equal("plaintext", error.ParamName);
        Assert.Equal(0, provider.Resolutions);
    }

    [Fact]
    public async Task FreshProvidersRevealUnicodeCredentialsFromTheSamePersistedOwnedRing()
    {
        var connectionString = await redisFixture.GetConnectionStringAsync();
        var ring = NewOwnedRing();
        const string plaintext = "fixture-only-雪-🔑-é-данные";
        try
        {
            string ciphertext;
            using (var first = CreateProvider(connectionString, ring))
            {
                ciphertext = FunctionCredentialFixture.ProtectApiKey(first, plaintext);
                Assert.StartsWith(FunctionCredentialProtector.Prefix, ciphertext);
                Assert.DoesNotContain(plaintext, ciphertext, StringComparison.Ordinal);
                Assert.InRange(ciphertext.Length, FunctionCredentialProtector.Prefix.Length + 1, 2000);
                Assert.DoesNotContain('\r', ciphertext);
                Assert.DoesNotContain('\n', ciphertext);
                var elements = await first.GetRequiredService<IConnectionMultiplexer>()
                    .GetDatabase().ListRangeAsync(ring).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.NotEmpty(elements);
                Assert.Contains(elements, element => XElement.Parse(element.ToString()).Name.LocalName == "key");
            }
            using (var second = CreateProvider(connectionString, ring))
            {
                var protector = Assert.IsType<FunctionCredentialProtector>(
                    second.GetRequiredService<IFunctionCredentialProtector>());
                Assert.Equal(plaintext, protector.Reveal(ciphertext));
                Assert.Empty(second.GetServices<ILoggerProvider>());
            }
        }
        finally
        {
            await DeleteOwnedRingAsync(connectionString, ring);
        }
    }

    [Fact]
    public async Task WrongApplicationAndWrongPurposeFailClosedAgainstTheSameOwnedRing()
    {
        var connectionString = await redisFixture.GetConnectionStringAsync();
        var ring = NewOwnedRing();
        const string plaintext = "fixture-only-purpose-boundary";
        try
        {
            string ciphertext;
            using (var original = CreateProvider(connectionString, ring))
                ciphertext = FunctionCredentialFixture.ProtectApiKey(original, plaintext);
            using (var wrongApplication = CreateProvider(connectionString, ring, "FixtureWrongApplication"))
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    wrongApplication.GetRequiredService<IFunctionCredentialProtector>().Reveal(ciphertext));
                Assert.IsAssignableFrom<CryptographicException>(error.InnerException);
            }
            using (var sameApplication = CreateProvider(connectionString, ring))
            {
                var wrongPurpose = sameApplication.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("ConduitLLM.Functions.Credentials.wrong-purpose");
                Assert.Throws<CryptographicException>(() =>
                    wrongPurpose.Unprotect(ciphertext[FunctionCredentialProtector.Prefix.Length..]));
                Assert.Equal(plaintext, sameApplication.GetRequiredService<IFunctionCredentialProtector>().Reveal(ciphertext));
            }
        }
        finally
        {
            await DeleteOwnedRingAsync(connectionString, ring);
        }
    }

    [Theory]
    [InlineData("invalid-base64")]
    [InlineData("truncated")]
    [InlineData("tampered")]
    public async Task CorruptedCredentialsAreRejectedByTheProductionProtector(string corruption)
    {
        var connectionString = await redisFixture.GetConnectionStringAsync();
        var ring = NewOwnedRing();
        try
        {
            using var provider = CreateProvider(connectionString, ring);
            var ciphertext = FunctionCredentialFixture.ProtectApiKey(provider, "fixture-only-corruption");
            var corrupted = corruption switch
            {
                "invalid-base64" => FunctionCredentialProtector.Prefix + "$invalid$",
                "truncated" => ciphertext[..(FunctionCredentialProtector.Prefix.Length + 8)],
                "tampered" => Tamper(ciphertext),
                _ => throw new ArgumentOutOfRangeException(nameof(corruption))
            };
            var error = Assert.Throws<InvalidOperationException>(() =>
                provider.GetRequiredService<IFunctionCredentialProtector>().Reveal(corrupted));
            Assert.NotNull(error.InnerException);
            Assert.Empty(provider.GetServices<ILoggerProvider>());
        }
        finally
        {
            await DeleteOwnedRingAsync(connectionString, ring);
        }
    }

    [Fact]
    public async Task CiphertextThatExceedsTheCredentialColumnLimitIsRejected()
    {
        var connectionString = await redisFixture.GetConnectionStringAsync();
        var ring = NewOwnedRing();
        try
        {
            using var provider = CreateProvider(connectionString, ring);
            var plaintext = new string('x', 2000);
            var protector = provider.GetRequiredService<IFunctionCredentialProtector>();
            Assert.True(protector.Protect(plaintext)!.Length > 2000);
            Assert.Throws<InvalidOperationException>(() =>
                FunctionCredentialFixture.ProtectApiKey(provider, plaintext));
        }
        finally
        {
            await DeleteOwnedRingAsync(connectionString, ring);
        }
    }

    private static string NewOwnedRing() => $"function-credential-fixture:{Guid.NewGuid():N}:keys";

    private static ServiceProvider CreateProvider(string connectionString, string ownedRing, string application = "Conduit")
    {
        var services = new ServiceCollection();
        FunctionCredentialFixture.AddServices(services, connectionString);
        // Override only this fixture's storage key after the production registration.
        services.AddOptions<KeyManagementOptions>().PostConfigure<IConnectionMultiplexer>((options, redis) =>
            options.XmlRepository = new RedisXmlRepository(() => redis.GetDatabase(), ownedRing));
        if (application != "Conduit")
            services.AddDataProtection().SetApplicationName(application);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static string Tamper(string ciphertext)
    {
        var changed = ciphertext.ToCharArray();
        var index = FunctionCredentialProtector.Prefix.Length +
            (ciphertext.Length - FunctionCredentialProtector.Prefix.Length) / 2;
        changed[index] = changed[index] == 'A' ? 'B' : 'A';
        return new string(changed);
    }

    private static async Task DeleteOwnedRingAsync(string connectionString, string ownedRing)
    {
        var segments = ownedRing.Split(':');
        if (segments.Length != 3 || segments[0] != "function-credential-fixture" ||
            !Guid.TryParseExact(segments[1], "N", out _) || segments[2] != "keys")
            throw new InvalidOperationException("Cleanup requires a GUID-owned fixture key.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = ConfigurationOptions.Parse(connectionString);
        options.ConnectTimeout = options.SyncTimeout = options.AsyncTimeout = 5000;
        options.ConnectRetry = 0;
        options.AbortOnConnectFail = true;
        ConnectionMultiplexer? redis = null;
        try
        {
            redis = ConnectionMultiplexer.Connect(options);
            await redis.GetDatabase().KeyDeleteAsync(ownedRing).WaitAsync(deadline.Token);
            Assert.False(await redis.GetDatabase().KeyExistsAsync(ownedRing).WaitAsync(deadline.Token));
        }
        finally
        {
            try
            {
                if (redis is not null)
                    await redis.CloseAsync(allowCommandsToComplete: false).WaitAsync(deadline.Token);
            }
            finally
            {
                redis?.Dispose();
            }
        }
    }

    private sealed class UnavailableServices : IServiceProvider
    {
        public int Resolutions { get; private set; }
        public object? GetService(Type serviceType)
        {
            Resolutions++;
            throw new InvalidOperationException("Protection must not resolve services for rejected input.");
        }
    }
}

/// <summary>Starts Redis only when a credential test requests it; never starts PostgreSQL.</summary>
public sealed class FunctionCredentialRedisFixture : IAsyncLifetime
{
    private readonly Lazy<Task<string>> _connectionString;
    private RedisContainer? _container;

    public FunctionCredentialRedisFixture() =>
        _connectionString = new Lazy<Task<string>>(CreateConnectionStringAsync);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<string> GetConnectionStringAsync() => _connectionString.Value;

    private async Task<string> CreateConnectionStringAsync()
    {
        if (Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_REDIS") is { } external &&
            !string.IsNullOrWhiteSpace(external))
            return external;
        _container = new RedisBuilder("redis:7.4-alpine").Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _container.StartAsync(deadline.Token);
        return _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    }
}
