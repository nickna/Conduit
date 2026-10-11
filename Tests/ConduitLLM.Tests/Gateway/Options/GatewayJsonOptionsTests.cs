using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Configuration.Options;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Tests.Gateway.Serialization;

public sealed class GatewayJsonOptionsTests
{
    [Fact]
    public void SerializerUsesSnakeCaseForPropertiesAndEnums()
    {
        var json = JsonSerializer.Serialize(
            new AsyncTaskStatus { TaskId = "request-1", State = TaskState.Processing },
            GatewayJsonOptions.Create());

        using var document = JsonDocument.Parse(json);
        Assert.Equal("request-1", document.RootElement.GetProperty("task_id").GetString());
        Assert.Equal("processing", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void SerializerRejectsIntegerEnumValues()
    {
        var deserialize = () => JsonSerializer.Deserialize<AsyncTaskStatus>(
            """{"task_id":"request-1","state":1}""",
            GatewayJsonOptions.Create());

        Assert.Throws<JsonException>(deserialize);
    }

    [Theory]
    [InlineData(typeof(ChatCompletionRequest), """{"model":"test","messages":[],"max_tokens":256}""", "max_tokens", 256)]
    [InlineData(typeof(ChatCompletionRequest), """{"model":"test","messages":[],"max_completion_tokens":512}""", "max_completion_tokens", 512)]
    [InlineData(typeof(ChatCompletionRequest), """{"model":"test","messages":[],"temperature":0.75}""", "temperature", 0.75)]
    [InlineData(typeof(EmbeddingRequest), """{"model":"test","input":"hello","dimensions":256}""", "dimensions", 256)]
    [InlineData(typeof(ImageGenerationRequest), """{"prompt":"A tree","n":2}""", "n", 2)]
    public void ConfiguringWebDefaultsAcceptsNumbersAndRejectsQuotedNumericRequestProperties(
        Type requestType,
        string json,
        string propertyName,
        double expectedValue)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        GatewayJsonOptions.Configure(options);

        var request = JsonSerializer.Deserialize(json, requestType, options);
        Assert.NotNull(request);
        var serialized = JsonSerializer.SerializeToElement(request, requestType, options);
        var number = serialized.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.Number, number.ValueKind);
        Assert.Equal(expectedValue, number.GetDouble());

        var quotedPayload = JsonNode.Parse(json)!;
        quotedPayload[propertyName] = number.GetRawText();
        var exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(quotedPayload.ToJsonString(), requestType, options));
        Assert.Equal($"$.{propertyName}", exception.Path);
    }

    [Fact]
    public void BasicSettingsExposeTheHttpWireSerializerAsTheSharedInstance()
    {
        var builder = WebApplication.CreateBuilder();
        global::Program.ConfigureBasicSettings(builder);
        using var services = builder.Services.BuildServiceProvider();

        var shared = services.GetRequiredService<JsonSerializerOptions>();
        var http = services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value
            .SerializerOptions;

        Assert.Same(http, shared);
        Assert.Same(JsonNamingPolicy.SnakeCaseLower, shared.PropertyNamingPolicy);
    }

    [Fact]
    public void BasicSettingsRejectOutOfRangeUsageAndBillingValues()
    {
        var builder = WebApplication.CreateBuilder();
        global::Program.ConfigureBasicSettings(builder);
        builder.Configuration["UsageTracking:MaximumStreamingToolCalls"] = "0";
        builder.Configuration["BillingAdmission:DefaultMaximumOutputTokens"] = "0";
        using var services = builder.Services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<UsageTrackingOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<BillingAdmissionOptions>>().Value);
    }

    [Fact]
    public void BillingServicesRejectOutOfRangeReconciliationValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BillingReconciliation:WindowHours"] = "0"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBillingAndPricingServices();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<BillingReconciliationOptions>>().Value);
    }

    [Fact]
    public void AsyncTaskStatusSerializesTheSharedCompletionTimestamp()
    {
        var completedAt = new DateTime(2026, 7, 26, 12, 30, 0, DateTimeKind.Utc);
        var json = JsonSerializer.Serialize(
            new AsyncTaskStatusResponse
            {
                TaskId = "task-1",
                Status = "completed",
                CompletedAt = completedAt
            },
            GatewayJsonOptions.Create());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            "2026-07-26T12:30:00Z",
            document.RootElement.GetProperty("completed_at").GetString());
    }
}
