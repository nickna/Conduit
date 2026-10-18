using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Providers;

namespace ConduitLLM.SerializationTests;

public sealed class ProviderContractInventoryTests
{
    [Fact]
    public void EveryProviderContextRootAndNestedContractResolvesWithoutReflectionFallback()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var contexts = ProviderContexts().ToArray();
        Assert.Contains(contexts, context => context.GetType().Name == "ProvidersJsonContext");
        Assert.Contains(contexts, context => context.GetType().Name == "MiniMaxStreamJsonContext");
        Assert.Contains(contexts, context => context.GetType().Name == "GoogleServiceAccountJsonContext");
        var failures = new List<string>();

        foreach (var context in contexts)
        {
            var roots = DeclaredRoots(context).ToArray();
            Assert.NotEmpty(roots);
            Assert.DoesNotContain(context.Options.TypeInfoResolverChain,
                resolver => resolver is DefaultJsonTypeInfoResolver);
            var visited = new HashSet<Type>();
            foreach (var root in roots)
            {
                try
                {
                    var pending = new Stack<Type>();
                    pending.Push(root);
                    while (pending.TryPop(out var type))
                    {
                        if (!visited.Add(type))
                            continue;
                        var metadata = context.Options.GetTypeInfo(type);
                        Assert.NotNull(metadata);
                        foreach (var property in metadata.Properties)
                            pending.Push(property.PropertyType);
                        if (Nullable.GetUnderlyingType(type) is { } underlyingType)
                            pending.Push(underlyingType);
                        if (metadata.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
                        {
                            foreach (var argument in type.GetGenericArguments())
                                pending.Push(argument);
                            if (type.GetElementType() is { } elementType)
                                pending.Push(elementType);
                        }
                        if (metadata.PolymorphismOptions is { } polymorphism)
                            foreach (var derived in polymorphism.DerivedTypes)
                                pending.Push(derived.DerivedType);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"{context.GetType().FullName}, root {root}: {exception.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    public static IEnumerable<object[]> ProviderWireFixtures()
    {
        yield return Fixture("ProvidersJsonContext", "Dictionary`2", """
            {"prompt":"fixture image","width":512,"height":512,"num_steps":4,"guidance":3.5,
             "seed":2147483648,"negative_prompt":"blur","extra":{"enabled":true,"weights":[1,0.5]}}
            """, dictionary: true);
        yield return Fixture("ProvidersJsonContext", "CloudflareImageResponse", """
            {"result":{"image":"aW1hZ2U="},"success":true,"errors":[],"messages":[]}
            """);
        yield return Fixture("ProvidersJsonContext", "CloudflareImageResponse", """
            {"success":false,"errors":[{"code":10000,"message":"Authentication error"}],"messages":[]}
            """);
        yield return Fixture("ProvidersJsonContext", "CloudflareModelsSearchResponse", """
            {"result":[{"id":"model-id","name":"@cf/fixture/model","description":"Fixture model"}],
             "success":true,"errors":[]}
            """);
        yield return Fixture("ProvidersJsonContext", "BedrockConverseRequest", """
            {"messages":[{"role":"user","content":[{"text":"fixture question"},
              {"image":{"format":"png","source":{"bytes":"aW1hZ2U="}}}]},
              {"role":"assistant","content":[{"toolUse":{"toolUseId":"tool-1","name":"search","input":{"query":"fixture"}}}]},
              {"role":"user","content":[{"toolResult":{"toolUseId":"tool-1","content":[{"json":{"found":true}}]}}]}],
             "system":[{"text":"fixture system"}],
             "inferenceConfig":{"maxTokens":128,"temperature":0.5,"topP":0.9,"stopSequences":["stop"]},
             "toolConfig":{"tools":[{"toolSpec":{"name":"search","description":"Fixture tool",
               "inputSchema":{"json":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}}}],
               "toolChoice":{"tool":{"name":"search"}}},
             "additionalModelRequestFields":{"top_k":20,"flags":[true,false]}}
            """);
        yield return Fixture("ProvidersJsonContext", "BedrockListFoundationModelsResponse", """
            {"modelSummaries":[{"modelId":"fixture.model","modelName":"Fixture model","providerName":"Fixture",
              "outputModalities":["TEXT"],"responseStreamingSupported":true}]}
            """);
        yield return Fixture("ProvidersJsonContext", "BedrockStreamMessageStart", """{"role":"assistant"}""");
        yield return Fixture("ProvidersJsonContext", "BedrockStreamContentBlockStart", """
            {"contentBlockIndex":1,"start":{"toolUse":{"toolUseId":"tool-1","name":"search"}}}
            """);
        yield return Fixture("ProvidersJsonContext", "BedrockStreamContentBlockDelta", """
            {"contentBlockIndex":0,"delta":{"text":"fixture answer"}}
            """);
        yield return Fixture("ProvidersJsonContext", "BedrockStreamContentBlockDelta", """
            {"contentBlockIndex":1,"delta":{"toolUse":{"input":"{\"query\":\"fixture\"}"}}}
            """);
        yield return Fixture("ProvidersJsonContext", "BedrockStreamContentBlockDelta", """
            {"contentBlockIndex":0,"delta":{"reasoningContent":{"text":"fixture reasoning"}}}
            """);
        yield return Fixture("ProvidersJsonContext", "BedrockStreamMessageStop", """{"stopReason":"tool_use"}""");
        yield return Fixture("ProvidersJsonContext", "BedrockStreamMetadata", """
            {"usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5}}
            """);
        yield return Fixture("ProvidersJsonContext", "ListDeploymentsResponse", """
            {"data":[{"id":"deployment-1","model":"fixture-model","deploymentId":"fixture-deployment",
              "status":"succeeded","provisioningState":"Succeeded"}]}
            """);
        yield return Fixture("ProvidersJsonContext", "OpenRouterVideoSubmitResponse", """{"id":"video-1"}""");
        yield return Fixture("ProvidersJsonContext", "OpenRouterVideoStatus", """
            {"id":"video-1","status":"completed","generation_id":"generation-1",
             "unsigned_urls":["https://example.test/video.mp4"],"usage":{"cost":0.125,"is_byok":false}}
            """);
        yield return Fixture("GoogleServiceAccountJsonContext", "OAuthTokenResponse", """
            {"access_token":"fixture-token","expires_in":3600}
            """);
        yield return Fixture("GoogleServiceAccountJsonContext", "GoogleServiceAccountCredential", """
            {"type":"service_account","client_email":"fixture@example.test","private_key":"fixture-key",
             "private_key_id":"fixture-key-id","token_uri":"https://oauth2.example.test/token"}
            """);
    }

    [Theory]
    [MemberData(nameof(ProviderWireFixtures))]
    public void ProviderFixturesAreBidirectionallyCompatibleWithPreviousSerializer(
        string contextName, string rootName, string fixture, bool dictionary)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var context = Assert.Single(ProviderContexts(), context => context.GetType().Name == contextName);
        var root = dictionary
            ? typeof(Dictionary<string, object>)
            : Assert.Single(DeclaredRoots(context), type => type.Name == rootName);
        var metadata = context.Options.GetTypeInfo(root);
        // Retain the context's real naming/null/converter policy while replacing
        // only the metadata provider with the previous reflection implementation.
        var legacy = new JsonSerializerOptions(context.Options)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        var generatedReadsLegacy = JsonSerializer.Deserialize(fixture, metadata);
        Assert.NotNull(generatedReadsLegacy);
        var generatedJson = JsonSerializer.Serialize(generatedReadsLegacy, metadata);
        AssertJsonEqual(fixture, generatedJson);
        var legacyReadsGenerated = JsonSerializer.Deserialize(generatedJson, root, legacy);
        Assert.NotNull(legacyReadsGenerated);
        var legacyJson = JsonSerializer.Serialize(legacyReadsGenerated, root, legacy);
        AssertJsonEqual(fixture, legacyJson);
        AssertJsonEqual(legacyJson, JsonSerializer.Serialize(
            JsonSerializer.Deserialize(legacyJson, metadata), metadata));
    }

    private static IEnumerable<JsonSerializerContext> ProviderContexts() =>
        // Reflection discovers the finite inventory in this JIT test only. Runtime
        // serialization still uses each production context's generated metadata.
        typeof(BaseLLMClient).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(JsonSerializerContext).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => Assert.IsAssignableFrom<JsonSerializerContext>(
                type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)));

    private static IEnumerable<Type> DeclaredRoots(JsonSerializerContext context) =>
        context.GetType().GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(JsonSerializableAttribute))
            .Select(attribute => Assert.IsAssignableFrom<Type>(attribute.ConstructorArguments[0].Value))
            .Distinct();

    private static object[] Fixture(string context, string root, string json, bool dictionary = false) =>
        [context, root, json, dictionary];

    private static void AssertJsonEqual(string expected, string actual) => Assert.True(
        JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
        $"Expected: {expected}{Environment.NewLine}Actual: {actual}");
}
