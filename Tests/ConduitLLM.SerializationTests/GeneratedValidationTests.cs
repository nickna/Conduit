using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Gateway.Extensions;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using AdminProgram = ConduitLLM.Admin.Program;

namespace ConduitLLM.SerializationTests;

[Collection("ValidationEnvironment")]
public sealed class GeneratedValidationTests
{
    [Theory]
    [InlineData("", 0, 3, "KeyName", "Key name is required.")]
    [InlineData("valid", 0, 1, "VirtualKeyGroupId", "VirtualKeyGroupId must be a valid positive number.")]
    [InlineData("valid", 1, 3, "RateLimitPriority", "RateLimitPriority must be 0 (low), 1 (normal) or 2 (high).")]
    public async Task Admin_generated_validation_preserves_problem_envelope_and_prevents_handler_execution(
        string keyName, int groupId, int priority, string property, string expectedMessage)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        AdminProgram.ConfigureHttpServices(builder);
        await using var app = builder.Build();
        var handlerCalled = false;
        app.MapPost("/validation", (CreateVirtualKeyRequestDto request) =>
        {
            handlerCalled = true;
            return Microsoft.AspNetCore.Http.Results.NoContent();
        });
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        // Build the input with bounded generated metadata as production HTTP clients do.
        var input = JsonSerializer.Serialize(new CreateVirtualKeyRequestDto
        {
            KeyName = keyName,
            VirtualKeyGroupId = groupId,
            RateLimitPriority = priority
        }, ConduitLLM.Admin.Serialization.AdminHttpJsonContext.Default.CreateVirtualKeyRequestDto);
        using var response = await client.PostAsync("/validation",
            new StringContent(input, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(handlerCalled);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        var errors = body.GetProperty("errors").EnumerateObject().ToArray();
        Assert.Contains(errors, error => error.Name.EndsWith(property, StringComparison.OrdinalIgnoreCase)
            && error.Value.EnumerateArray().Any(message => message.GetString()!.Contains(expectedMessage, StringComparison.Ordinal)));
        Assert.Contains(expectedMessage, body.GetProperty("detail").GetString());

        // A valid body must continue to the handler through the same generated resolver.
        using var validResponse = await client.PostAsync("/validation", new StringContent(
            "{\"keyName\":\"valid\",\"virtualKeyGroupId\":1,\"rateLimitPriority\":1}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, validResponse.StatusCode);
        Assert.True(handlerCalled);
    }

    [Fact]
    public void Generated_options_validators_enforce_annotated_ranges_without_runtime_property_walks()
    {
        Check(new BillingCostCanaryOptionsValidator(), new BillingCostCanaryOptions(),
            new BillingCostCanaryOptions { IntervalMinutes = 0 }, nameof(BillingCostCanaryOptions.IntervalMinutes));
        Check(new BillingReconciliationOptionsValidator(), new BillingReconciliationOptions(),
            new BillingReconciliationOptions { RelativeThreshold = 1.01m }, nameof(BillingReconciliationOptions.RelativeThreshold));
        Check(new BatchSpendingOptionsValidator(), new BatchSpendingOptions(),
            new BatchSpendingOptions { RedisTtlHours = 0 }, nameof(BatchSpendingOptions.RedisTtlHours));
        Check(new RedisCircuitBreakerOptionsValidator(), new RedisCircuitBreakerOptions(),
            new RedisCircuitBreakerOptions { FailureThreshold = 0 }, nameof(RedisCircuitBreakerOptions.FailureThreshold));
        Check(new RateLimitOptionsValidator(), new RateLimitOptions(),
            new RateLimitOptions { PrioritySaturationThreshold = 0 }, nameof(RateLimitOptions.PrioritySaturationThreshold));
        Check(new SignalRConnectionOptionsValidator(), new SignalRConnectionOptions(),
            new SignalRConnectionOptions { MaxTotalConnections = 0 }, nameof(SignalRConnectionOptions.MaxTotalConnections));
    }

    [Fact]
    public void Generated_circuit_breaker_validator_retains_cross_property_rules()
    {
        var result = new RedisCircuitBreakerOptionsValidator().Validate(null,
            new RedisCircuitBreakerOptions { HalfOpenSuccessesRequired = 6, HalfOpenMaxAttempts = 5 });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("cannot be greater than", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.000000000000000000000000001", "0.01")]
    [InlineData("0.01", "1000000.0000000000000000001")]
    public void Billing_options_retain_exact_decimal_limits_after_generated_validation(
        string relativeThreshold, string absoluteThreshold)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BillingReconciliation:RelativeThreshold"] = relativeThreshold,
            ["BillingReconciliation:AbsoluteThresholdUsd"] = absoluteThreshold
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddBillingAndPricingServices();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BillingReconciliationOptions>>().Value);
    }

    [Fact]
    public void Generated_rate_limit_validator_accepts_existing_small_positive_environment_thresholds()
    {
        const string name = "CONDUIT_RATE_LIMIT_SATURATION_THRESHOLD";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "0.005");
            var services = new ServiceCollection();
            services.AddConduitRateLimiting(new ConfigurationBuilder().Build());
            using var provider = services.BuildServiceProvider();
            Assert.Equal(0.005, provider.GetRequiredService<IOptions<RateLimitOptions>>().Value.PrioritySaturationThreshold);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Theory]
    [InlineData(100, 0, false)]
    [InlineData(0, 10000, false)]
    [InlineData(100, 10000, true)]
    public void Gateway_SignalR_registration_validates_connection_limits_at_startup(
        int maxConnectionsPerVirtualKey, int maxTotalConnections, bool valid)
    {
        const string name = "REDIS_URL";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            // Configure the actual runtime registration without starting its Redis workers.
            Environment.SetEnvironmentVariable(name, "redis://127.0.0.1:6379");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{SignalRConnectionOptions.SectionName}:MaxConnectionsPerVirtualKey"] = maxConnectionsPerVirtualKey.ToString(),
                [$"{SignalRConnectionOptions.SectionName}:MaxTotalConnections"] = maxTotalConnections.ToString()
            });
            global::Program.ConfigureSignalRServices(builder);
            using var provider = builder.Services.BuildServiceProvider();
            var startupValidator = provider.GetRequiredService<IStartupValidator>();

            if (valid)
            {
                startupValidator.Validate();
                var options = provider.GetRequiredService<IOptions<SignalRConnectionOptions>>().Value;
                Assert.Equal(maxConnectionsPerVirtualKey, options.MaxConnectionsPerVirtualKey);
                Assert.Equal(maxTotalConnections, options.MaxTotalConnections);
            }
            else
            {
                var error = Assert.Throws<OptionsValidationException>(startupValidator.Validate);
                Assert.Equal(typeof(SignalRConnectionOptions), error.OptionsType);
                var property = maxTotalConnections == 0
                    ? nameof(SignalRConnectionOptions.MaxTotalConnections)
                    : nameof(SignalRConnectionOptions.MaxConnectionsPerVirtualKey);
                Assert.Contains(error.Failures, failure => failure.Contains(property, StringComparison.Ordinal));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    private static void Check<T>(IValidateOptions<T> validator, T valid, T invalid, string property) where T : class
    {
        Assert.True(validator.Validate(null, valid).Succeeded);
        var result = validator.Validate(null, invalid);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains(property, StringComparison.Ordinal));
    }
}

[CollectionDefinition("ValidationEnvironment", DisableParallelization = true)]
public sealed class ValidationEnvironmentCollection { }
