using ConduitLLM.Admin.Extensions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Admin;

[Collection("AdminCompositionEnvironment")]
[Trait("Category", "Unit")]
[Trait("Component", "Admin")]
public sealed class AdminConfigurationTests : IDisposable
{
    private const string EnvironmentOrigin = "https://environment.example";
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"conduit-admin-config-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _originalEnvironment = new();

    [Fact]
    public async Task BasicSettings_PreservesAspNetCoreHostingEnvironment()
    {
        SetEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:0");
        SetEnvironment("DOTNET_URLS", null);
        SetEnvironment("URLS", null);
        Directory.CreateDirectory(_contentRoot);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ConduitLLM.Admin.Program).Assembly.GetName().Name,
            ContentRootPath = _contentRoot,
            EnvironmentName = "Testing",
            Args = []
        });
        ConduitLLM.Admin.Program.ConfigureBasicSettings(builder);
        await using var app = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(timeout.Token);
            var address = Assert.Single(app.Urls);
            Assert.StartsWith("http://127.0.0.1:", address);
            Assert.NotEqual(0, new Uri(address).Port);
        }
        finally
        {
            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdownTimeout.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BasicSettings_JsonAndCommandLineCannotSupplyOrOverrideOrigins(bool configureEnvironment)
    {
        Directory.CreateDirectory(_contentRoot);
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), """
            { "AdminApi": { "AllowedOrigins": ["https://json.example", "https://json-extra.example"] } }
            """);
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.Testing.json"), """
            { "AdminApi": { "AllowedOrigins": ["https://testing-json.example", "https://testing-json-extra.example", "https://testing-json-only.example"] } }
            """);

        for (var index = 0; index < 4; index++)
        {
            SetEnvironment($"AdminApi__AllowedOrigins__{index}",
                index == 0 && configureEnvironment ? EnvironmentOrigin : null);
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ConduitLLM.Admin.Program).Assembly.GetName().Name,
            ContentRootPath = _contentRoot,
            EnvironmentName = "Testing",
            Args = [
                "--AdminApi:AllowedOrigins:0=https://command-line.example",
                "--AdminApi:AllowedOrigins:3=https://command-line-only.example"
            ]
        });
        using var configuration = builder.Configuration;

        ConduitLLM.Admin.Program.ConfigureBasicSettings(builder);
        builder.Services.AddAdminServices(configuration);
        using var services = builder.Services.BuildServiceProvider();

        string[] expectedOrigins = configureEnvironment ? [EnvironmentOrigin] : [];
        Assert.Equal(expectedOrigins,
            configuration.GetSection("AdminApi:AllowedOrigins").Get<string[]>() ?? []);

        var policy = services.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy("AdminCorsPolicy");
        Assert.NotNull(policy);
        Assert.Equal(expectedOrigins, policy.Origins);
        Assert.True(policy.IsOriginAllowed(EnvironmentOrigin));
        if (configureEnvironment)
        {
            foreach (var origin in new[]
            {
                "https://json.example", "https://json-extra.example",
                "https://testing-json.example", "https://testing-json-extra.example", "https://testing-json-only.example",
                "https://command-line.example", "https://command-line-only.example"
            })
            {
                Assert.False(policy.IsOriginAllowed(origin));
            }
        }
    }

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }
}
