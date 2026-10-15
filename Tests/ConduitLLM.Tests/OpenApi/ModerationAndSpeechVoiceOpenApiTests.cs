using System.Text.Json;

using ConduitLLM.Gateway.OpenApi;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Tests.Gateway.Endpoints;

using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Integration")]
[Trait("Component", "OpenApi")]
public sealed class ModerationAndSpeechVoiceOpenApiTests
{
    [Fact]
    public async Task FreshDocument_DescribesModerationGraphAndStockOrCustomSpeechVoice()
    {
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
        {
            services.ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions));
            services.AddEndpointsApiExplorer();
            services.AddOpenApi("v1", options => options.AddSchemaTransformer<StructuredJsonSchemaTransformer>());
        }, exposeOpenApi: true);
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var chat = schemas.GetProperty("ChatCompletionRequest");
        var moderationProperty = chat.GetProperty("properties").GetProperty("moderation");
        Assert.DoesNotContain("moderation", Required(chat));
        Assert.True(AllowsNull(moderationProperty, schemas));
        var moderation = Resolve(moderationProperty, schemas);
        Assert.Contains("model", Required(moderation));
        var model = moderation.GetProperty("properties").GetProperty("model");
        Assert.Equal("string", model.GetProperty("type").GetString());
        Assert.Equal(1, model.GetProperty("minLength").GetInt32());
        Assert.DoesNotContain("policy", Required(moderation));
        var policyProperty = moderation.GetProperty("properties").GetProperty("policy");
        Assert.True(AllowsNull(policyProperty, schemas));
        var policy = Resolve(policyProperty, schemas);
        foreach (var direction in new[] { "input", "output" })
        {
            Assert.DoesNotContain(direction, Required(policy));
            var ruleProperty = policy.GetProperty("properties").GetProperty(direction);
            Assert.True(AllowsNull(ruleProperty, schemas));
            var rule = Resolve(ruleProperty, schemas);
            Assert.Contains("mode", Required(rule));
            var mode = Resolve(rule.GetProperty("properties").GetProperty("mode"), schemas);
            Assert.Equal("string", mode.GetProperty("type").GetString());
            Assert.Equal(new[] { "score", "block" }, mode.GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        }

        var speech = schemas.GetProperty("TextToSpeechRequest");
        Assert.Contains("voice", Required(speech));
        var voiceProperty = speech.GetProperty("properties").GetProperty("voice");
        Assert.False(AllowsNull(voiceProperty, schemas));
        var voice = Resolve(voiceProperty, schemas);
        var alternatives = voice.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(2, alternatives.Length);
        Assert.Single(alternatives, alternative => alternative.GetProperty("type").GetString() == "string");
        var custom = Assert.Single(alternatives, alternative => alternative.GetProperty("type").GetString() == "object");
        Assert.Equal(new[] { "id" }, Required(custom));
        Assert.Equal("string", custom.GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.False(voice.TryGetProperty("properties", out _));
    }

    private static string[] Required(JsonElement schema) => schema.TryGetProperty("required", out var required)
        ? required.EnumerateArray().Select(value => value.GetString()!).ToArray() : [];

    private static JsonElement Resolve(JsonElement schema, JsonElement schemas)
    {
        if (schema.TryGetProperty("$ref", out var reference))
            return schemas.GetProperty(reference.GetString()!.Split('/')[^1]);
        foreach (var union in new[] { "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(union, out var alternatives)) continue;
            var branches = alternatives.EnumerateArray().ToArray();
            // Unwrap nullable references while keeping actual value unions, such as
            // the stock/custom speech voice alternatives, available to assertions.
            if (branches.Any(IsNullAlternative))
                return Resolve(branches.First(branch => !IsNullAlternative(branch)), schemas);
        }
        return schema;
    }

    private static bool IsNullAlternative(JsonElement schema) =>
        schema.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String && type.GetString() == "null";

    private static bool AllowsNull(JsonElement schema, JsonElement schemas)
    {
        if (schema.TryGetProperty("type", out var type) &&
            (type.ValueKind == JsonValueKind.String && type.GetString() == "null" ||
             type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(value => value.GetString() == "null")))
            return true;
        if (schema.TryGetProperty("$ref", out var reference))
            return AllowsNull(schemas.GetProperty(reference.GetString()!.Split('/')[^1]), schemas);
        foreach (var union in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(union, out var alternatives) &&
                alternatives.EnumerateArray().Any(alternative => AllowsNull(alternative, schemas)))
                return true;
        return false;
    }
}
