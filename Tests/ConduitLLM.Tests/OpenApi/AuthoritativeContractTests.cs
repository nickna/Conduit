using System.Net.Http;
using System.Text.Json;

using AdminOperationIdValidator = ConduitLLM.Core.OpenApi.OperationIdValidationDocumentTransformer;

using AwesomeAssertions;

using Microsoft.OpenApi;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Unit")]
[Trait("Component", "OpenApi")]
public sealed class AuthoritativeContractTests : IDisposable
{
    private readonly JsonDocument _admin = LoadContract("Services", "ConduitLLM.Admin", "openapi-admin.json");
    private readonly JsonDocument _gateway = LoadContract("Services", "ConduitLLM.Gateway", "openapi-gateway.json");

    [Fact]
    public void Contracts_UseCanonicalLineEndingsInSchemaDocumentation()
    {
        foreach (var document in new[] { _admin, _gateway })
        {
            foreach (var component in document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject())
            {
                var schemas = new List<JsonElement> { component.Value };
                if (component.Value.TryGetProperty("properties", out var properties))
                    schemas.AddRange(properties.EnumerateObject().Select(property => property.Value));
                foreach (var schema in schemas)
                {
                    if (schema.TryGetProperty("description", out var value))
                        value.GetString().Should().NotContain("\r", "documentation must export identically on Windows and Linux");
                }
            }
        }
    }

    [Fact]
    public void Contracts_PublishStableGeneratedAndExplicitOperationIds()
    {
        Operation(_admin, "/v1/admin/virtual-keys", "get").GetProperty("operationId").GetString()
            .Should().Be("VirtualKeys_GetAll");
        Operation(_admin, "/v1/admin/model-authors", "get").GetProperty("operationId").GetString()
            .Should().Be("ModelAuthors_List");
        Operation(_admin, "/v1/admin/model-authors/{id}", "get").GetProperty("operationId").GetString()
            .Should().Be("ModelAuthors_GetById", "explicit operation IDs must follow Tag_Action");
    }

    [Fact]
    public void DuplicateOperationIds_AreRejected()
    {
        var document = new OpenApiDocument
        {
            Paths = new OpenApiPaths
            {
                ["/one"] = PathWith(HttpMethod.Get, "Duplicate_Id"),
                ["/two"] = PathWith(HttpMethod.Post, "Duplicate_Id")
            }
        };

        var act = () => AdminOperationIdValidator.Validate(document);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate_Id*");
    }

    [Fact]
    public void Contracts_DefineCanonicalSecuritySchemes()
    {
        var adminScheme = Scheme(_admin, "MasterKey");
        adminScheme.GetProperty("type").GetString().Should().Be("apiKey");
        adminScheme.GetProperty("name").GetString().Should().Be("X-Master-Key");
        adminScheme.GetProperty("in").GetString().Should().Be("header");

        var gatewayScheme = Scheme(_gateway, "VirtualKey");
        gatewayScheme.GetProperty("type").GetString().Should().Be("http");
        gatewayScheme.GetProperty("scheme").GetString().Should().Be("bearer");
        gatewayScheme.GetProperty("bearerFormat").GetString().Should().Be("opaque virtual key");
    }

    [Theory]
    [InlineData("admin", "/v1/admin/virtual-keys/validate", "post")]
    [InlineData("admin", "/v1/admin/ip-filters/check/{ipAddress}", "get")]
    [InlineData("gateway", "/v1/conduit/media/{storageKey}", "get")]
    [InlineData("gateway", "/v1/conduit/media/{storageKey}", "head")]
    public void AnonymousOperations_PublishAnExplicitEmptySecurityRequirement(
        string contract,
        string path,
        string method)
    {
        var document = contract == "admin" ? _admin : _gateway;

        Operation(document, path, method).GetProperty("security").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("admin", "/v1/admin/virtual-keys", "get", "MasterKey")]
    [InlineData("gateway", "/v1/chat/completions", "post", "VirtualKey")]
    public void AuthenticatedOperations_ReferenceTheirCanonicalScheme(
        string contract,
        string path,
        string method,
        string scheme)
    {
        var document = contract == "admin" ? _admin : _gateway;
        var requirement = Operation(document, path, method).GetProperty("security")[0];

        requirement.TryGetProperty(scheme, out _).Should().BeTrue();
    }

    [Fact]
    public void Gateway_PublishesBinaryAndStreamingResponses()
    {
        var mediaSchema = Operation(_gateway, "/v1/conduit/media/{storageKey}", "get")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/octet-stream").GetProperty("schema");
        mediaSchema.GetProperty("type").GetString().Should().Be("string");
        mediaSchema.GetProperty("format").GetString().Should().Be("binary");

        var downloadSchema = Operation(_gateway, "/v1/conduit/downloads/{fileId}", "get")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/octet-stream").GetProperty("schema");
        downloadSchema.GetProperty("type").GetString().Should().Be("string");
        downloadSchema.GetProperty("format").GetString().Should().Be("binary");

        var streamSchema = Operation(_gateway, "/v1/chat/completions", "post")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("text/event-stream").GetProperty("schema");
        streamSchema.GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public void Gateway_OpenAICompatibleOperationsPublishStandardFailuresAndRetryHeaders()
    {
        var operations = new[]
        {
            ("/v1/models", "get"),
            ("/v1/models/{model}", "get"),
            ("/v1/embeddings", "post"),
            ("/v1/chat/completions", "post"),
            ("/v1/audio/transcriptions", "post"),
            ("/v1/audio/speech", "post"),
            ("/v1/images/generations", "post")
        };

        foreach (var (path, method) in operations)
        {
            var responses = Operation(_gateway, path, method).GetProperty("responses");
            foreach (var status in new[] { "401", "403", "408", "413", "429", "503" })
                responses.TryGetProperty(status, out _).Should().BeTrue($"{method} {path} must document {status}");

            foreach (var status in new[] { "429", "503" })
                responses.GetProperty(status).GetProperty("headers")
                    .TryGetProperty("Retry-After", out _).Should().BeTrue(
                        $"{method} {path} {status} must document retry guidance");
        }
    }

    [Fact]
    public void Gateway_OpenAICompatibleMediaContractsMatchOfficialShapes()
    {
        var speechTypes = Operation(_gateway, "/v1/audio/speech", "post")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .EnumerateObject().Select(item => item.Name);
        speechTypes.Should().BeEquivalentTo(
            "audio/mpeg", "audio/opus", "audio/aac", "audio/flac",
            "audio/wav", "audio/pcm", "application/octet-stream");

        var transcription = Operation(_gateway, "/v1/audio/transcriptions", "post")
            .GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");
        transcription.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Should().BeEquivalentTo("file", "model");
        transcription.GetProperty("properties")
            .TryGetProperty("known_speaker_references", out var speakerReferences).Should().BeTrue();
        speakerReferences.GetProperty("type").GetString().Should().Be("array");
        speakerReferences.GetProperty("items").GetProperty("format").GetString().Should().Be("binary");

        var imageRequest = ResolveSchema(_gateway,
            Operation(_gateway, "/v1/images/generations", "post")
                .GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema"));
        imageRequest.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Should().Equal("prompt");

        var embeddingRequest = ResolveSchema(_gateway,
            Operation(_gateway, "/v1/embeddings", "post")
                .GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema"));
        embeddingRequest.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Should().NotContain("encoding_format");

        embeddingRequest.GetProperty("properties").GetProperty("input")
            .GetProperty("oneOf").GetArrayLength().Should().Be(4);
        _gateway.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("Message").GetProperty("properties").GetProperty("content")
            .GetProperty("oneOf").GetArrayLength().Should().Be(2);
        _gateway.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("ChatCompletionRequest").GetProperty("properties").GetProperty("stop")
            .GetProperty("oneOf").GetArrayLength().Should().Be(2);
        _gateway.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("ToolChoice").GetProperty("oneOf").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Admin_Universal500UsesTheStandardErrorShape()
    {
        var schema = Operation(_admin, "/v1/admin/virtual-keys", "get")
            .GetProperty("responses").GetProperty("500").GetProperty("content")
            .GetProperty("application/problem+json").GetProperty("schema");

        schema.GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/AdminProblemDetails");
    }

    [Fact]
    public void Contracts_PublishRequestIdAndCanonicalErrorShapeOnEveryResponse()
    {
        foreach (var (name, document, mediaType, schemaName) in new[]
        {
            ("admin", _admin, "application/problem+json", "AdminProblemDetails"),
            ("gateway", _gateway, "application/json", "OpenAIErrorResponse")
        })
        {
            foreach (var (operationName, path, operation) in Operations(document))
            {
                foreach (var response in operation.GetProperty("responses").EnumerateObject())
                {
                    response.Value.GetProperty("headers").TryGetProperty("x-request-id", out _)
                        .Should().BeTrue($"{name} {operationName} {path} response {response.Name} must expose its request ID");

                    if (!int.TryParse(response.Name, out var status) || status < 400)
                    {
                        continue;
                    }

                    var content = response.Value.GetProperty("content");
                    content.EnumerateObject().Select(item => item.Name)
                        .Should().Equal(mediaType);
                    content.GetProperty(mediaType).GetProperty("schema")
                        .GetProperty("$ref").GetString()
                        .Should().Be($"#/components/schemas/{schemaName}");
                }
            }
        }
    }

    [Fact]
    public void Contracts_UseStringEnumsAndCanonicalMediaTypes()
    {
        foreach (var (name, document) in new[] { ("admin", _admin), ("gateway", _gateway) })
        {
            foreach (var element in Descendants(document.RootElement))
            {
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("enum", out var values))
                {
                    values.EnumerateArray().Should().NotContain(
                        value => value.ValueKind == JsonValueKind.Number,
                        $"{name} enum values must not use wire-unstable integer ordinals");
                }

                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Object)
                {
                    content.EnumerateObject().Select(item => item.Name)
                        .Should().NotContain(mediaType =>
                             mediaType == "text/json" || mediaType == "text/plain" ||
                             mediaType.StartsWith("application/", StringComparison.Ordinal) &&
                             mediaType.EndsWith("+json", StringComparison.Ordinal) &&
                             mediaType != "application/problem+json" &&
                             mediaType != "application/merge-patch+json");
                }
            }
        }
    }

    [Fact]
    public void Gateway_IdempotentCommandsUseOnlyTheCanonicalHeader()
    {
        foreach (var path in new[] { "/v1/conduit/functions/execute" })
        {
            var operation = Operation(_gateway, path, "post");
            operation.GetProperty("parameters").EnumerateArray()
                .Should().ContainSingle(parameter =>
                    parameter.GetProperty("in").GetString() == "header" &&
                    parameter.GetProperty("name").GetString() == "Idempotency-Key");

            var requestSchema = operation.GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema");
            ResolveSchema(_gateway, requestSchema).GetProperty("properties").EnumerateObject()
                .Select(property => property.Name)
                .Should().NotContain(name =>
                    name.Contains("idempotency", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Gateway_UsesSnakeCaseForEveryPublishedSchemaProperty()
    {
        foreach (var element in Descendants(_gateway.RootElement))
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            properties.EnumerateObject().Select(property => property.Name)
                .Should().OnlyContain(
                    name => System.Text.RegularExpressions.Regex.IsMatch(
                        name,
                        "^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$"),
                    "Gateway wire properties must use snake_case");
        }
    }

    [Fact]
    public void Gateway_PublicContractContainsOnlyOpenAIAndConduitExtensionRoutes()
    {
        var openAiRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/v1/audio/speech",
            "/v1/audio/transcriptions",
            "/v1/chat/completions",
            "/v1/embeddings",
            "/v1/images/generations",
            "/v1/models",
            "/v1/models/{model}",
            "/v1/responses"
        };
        var paths = _gateway.RootElement.GetProperty("paths").EnumerateObject()
            .Select(path => path.Name)
            .ToList();

        paths.Should().OnlyContain(path =>
            openAiRoutes.Contains(path) ||
            path.StartsWith("/v1/conduit/", StringComparison.Ordinal));
        paths.Should().Contain("/v1/conduit/discovery/models");
        paths.Should().Contain("/v1/conduit/functions/execute");
        paths.Should().Contain("/v1/conduit/videos/generations/async");
        paths.Should().NotContain("/v1/completions",
            "the legacy stub only ever answers 501, so no generated client should type a call to it");
    }

    [Fact]
    public void Gateway_PublishesNoSchemaComponentThatOperationsCannotReach()
    {
        var schemas = _gateway.RootElement.GetProperty("components").GetProperty("schemas");
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(
            SchemaReferences(_gateway.RootElement.GetProperty("paths")));

        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!reachable.Add(name) || !schemas.TryGetProperty(name, out var schema))
                continue;

            foreach (var reference in SchemaReferences(schema))
                pending.Enqueue(reference);
        }

        schemas.EnumerateObject().Select(schema => schema.Name)
            .Should().OnlyContain(name => reachable.Contains(name),
                "orphaned components make generated clients carry contract no route can return");
    }

    [Fact]
    public void Gateway_DiscoveryCapabilityWireProjectionHasExplicitBoundaryName()
    {
        var schemas = _gateway.RootElement.GetProperty("components").GetProperty("schemas");

        schemas.TryGetProperty("DiscoveryModelCapabilitiesDto", out _).Should().BeTrue();
        schemas.TryGetProperty("GatewayModelCapabilitiesDto", out _).Should().BeFalse(
            "Admin previews and Gateway discovery now share one explicit wire contract");
        schemas.TryGetProperty("ModelCapabilitiesDto", out _).Should().BeFalse(
            "the unqualified capability DTO is the internal Configuration projection");
    }

    [Fact]
    public void Admin_PagedResultsDoNotPublishDeprecatedAliases()
    {
        var schemas = _admin.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schema in schemas.EnumerateObject()
            .Where(item => item.Name.StartsWith("PagedResultOf", StringComparison.Ordinal)))
        {
            schema.Value.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name)
                .Should().NotContain(["page", "totalItems"]);
        }
    }

    [Fact]
    public void Admin_ModelReadsPublishTypedResponseContracts()
    {
        var flatOperation = Operation(_admin, "/v1/admin/models", "get");
        var flatSchema = flatOperation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        flatSchema.GetProperty("properties").GetProperty("data")
            .GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ModelDto");
        flatOperation.GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString())
            .Should().Contain(["page", "pageSize"]);

        ResponseSchema(_admin, "/v1/admin/models/paged")
            .GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/PagedResultOfModelDto");
        ResponseSchema(_admin, "/v1/admin/models/{id}/identifiers")
            .GetProperty("properties").GetProperty("data")
            .GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ModelIdentifierDto");
        ResponseSchema(_admin, "/v1/admin/models/{id}/available-providers")
            .GetProperty("properties").GetProperty("data")
            .GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ModelProviderAvailabilityDto");
    }

    [Fact]
    public void Admin_ModelIdentifierCreationPublishesUnifiedResponseContract()
    {
        Operation(_admin, "/v1/admin/models/{id}/identifiers", "post")
            .GetProperty("responses").GetProperty("201")
            .GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ModelIdentifierDto");
    }

    [Theory]
    [InlineData("/v1/admin/model-authors", "post", "CreateModelAuthorDto", "201", "ModelAuthorDto")]
    [InlineData("/v1/admin/model-authors/{id}", "patch", "UpdateModelAuthorDto", "200", "ModelAuthorDto")]
    [InlineData("/v1/admin/model-authors/{id}", "delete", null, "204", null)]
    [InlineData("/v1/admin/model-series", "post", "CreateModelSeriesDto", "201", "ModelSeriesDto")]
    [InlineData("/v1/admin/model-series/{id}", "patch", "UpdateModelSeriesDto", "200", "ModelSeriesDto")]
    [InlineData("/v1/admin/model-series/{id}", "delete", null, "204", null)]
    [InlineData("/v1/admin/models", "post", "CreateModelDto", "201", "ModelDto")]
    [InlineData("/v1/admin/models/{id}", "patch", "UpdateModelDto", "200", "ModelDto")]
    [InlineData("/v1/admin/models/{id}", "delete", null, "204", null)]
    [InlineData("/v1/admin/models/{id}/identifiers", "post", "ModelIdentifierRequestDto", "201", "ModelIdentifierDto")]
    [InlineData("/v1/admin/models/{id}/identifiers/{identifierId}", "patch", "ModelIdentifierRequestDto", "200", "ModelIdentifierDto")]
    [InlineData("/v1/admin/models/{id}/identifiers/{identifierId}", "delete", null, "204", null)]
    [InlineData("/v1/admin/models/{id}/provider-mappings", "post", "ModelProviderMappingDto", "201", "ModelProviderMappingDto")]
    [InlineData("/v1/admin/models/{id}/provider-mappings/{mappingId}", "patch", "UpdateModelProviderMappingDto", "200", "ModelProviderMappingDto")]
    [InlineData("/v1/admin/models/{id}/provider-mappings/{mappingId}", "delete", null, "204", null)]
    [InlineData("/v1/admin/model-catalogs/import", "post", null, "200", "BundledModelCatalogImportResult")]
    public void Admin_ModelFamilyMutationsPublishConcreteContracts(
        string path,
        string method,
        string? requestSchema,
        string responseStatus,
        string? responseSchema)
    {
        var operation = Operation(_admin, path, method);

        if (requestSchema is null)
        {
            operation.TryGetProperty("requestBody", out _).Should().BeFalse();
        }
        else
        {
            RequestSchema(_admin, path, method)
                .GetProperty("$ref").GetString()
                .Should().Be($"#/components/schemas/{requestSchema}");
        }

        var response = operation.GetProperty("responses").GetProperty(responseStatus);
        if (responseSchema is null)
        {
            response.TryGetProperty("content", out _).Should().BeFalse();
        }
        else
        {
            response.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString()
                .Should().Be($"#/components/schemas/{responseSchema}");
        }
    }

    [Fact]
    public void Admin_TopLevelModelMappingsPublishConcreteContracts()
    {
        ResponseSchema(_admin, "/v1/admin/model-provider-mappings")
            .GetProperty("properties").GetProperty("data")
            .GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ModelProviderMappingDto");
        ResponseSchema(_admin, "/v1/admin/model-provider-mappings/{id}")
            .GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/ModelProviderMappingDto");

        RequestSchema(_admin, "/v1/admin/model-provider-mappings", "post")
            .GetProperty("$ref").GetString().Should().Be("#/components/schemas/CreateModelProviderMappingDto");
        RequestSchema(_admin, "/v1/admin/model-provider-mappings/{id}", "patch")
            .GetProperty("$ref").GetString().Should().Be("#/components/schemas/UpdateModelProviderMappingDto");
        foreach (var schemaName in new[]
        {
            "CreateModelProviderMappingDto",
            "UpdateModelProviderMappingDto",
            "ModelProviderMappingDto"
        })
        {
            _admin.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty(schemaName).GetProperty("properties")
                .TryGetProperty("notes", out _).Should().BeFalse();
        }
        RequestSchema(_admin, "/v1/admin/model-provider-mappings/bulk", "post")
            .GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/BulkModelMappingCreateRequest");
        RequestSchema(_admin, "/v1/admin/model-provider-mappings/bulk/preview", "post")
            .GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/BulkModelMappingPreviewRequest");

        foreach (var (path, responseSchema) in new[]
        {
            ("/v1/admin/model-provider-mappings/bulk/preview", "BulkModelMappingPreviewResponse"),
            ("/v1/admin/model-provider-mappings/bulk", "BulkModelMappingCreateResponse"),
            ("/v1/admin/model-provider-mappings/bulk/delete", "BulkDeleteResult"),
            ("/v1/admin/model-provider-mappings/bulk/enable", "BulkUpdateResult"),
            ("/v1/admin/model-provider-mappings/bulk/disable", "BulkUpdateResult")
        })
        {
            Operation(_admin, path, "post").GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{responseSchema}");
        }

        Operation(_admin, "/v1/admin/model-provider-mappings/{id}", "patch")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref")
            .GetString().Should().Be("#/components/schemas/ModelProviderMappingDto");
        Operation(_admin, "/v1/admin/model-provider-mappings/{id}", "delete")
            .GetProperty("responses").GetProperty("204")
            .TryGetProperty("content", out _).Should().BeFalse();
        Operation(_admin, "/v1/admin/model-provider-mappings/{id}", "patch")
            .GetProperty("responses").TryGetProperty("409", out _).Should().BeTrue();

        Operation(_admin, "/v1/admin/model-provider-mappings/{id}", "get").GetProperty("parameters")[0]
            .GetProperty("schema").GetProperty("format").GetString().Should().Be("int32");
    }

    [Theory]
    [InlineData("ModelProviderMappingDto", "id", "modelAlias", "providerModelId", "providerId", "modelProviderTypeAssociationId", "priority", "weight", "isEnabled", "createdAt", "updatedAt")]
    [InlineData("ProviderReferenceDto", "id", "providerType", "displayName", "isEnabled")]
    [InlineData("ModelCapabilitiesDto", "supportsVision", "supportsImageGeneration", "supportsVideoGeneration", "supportsEmbeddings", "supportsSpeechToText", "supportsTextToSpeech", "supportsRerank", "supportsChat", "supportsFunctionCalling", "supportsStreaming", "maxInputTokens", "maxOutputTokens")]
    [InlineData("BulkModelMappingPreviewResponse", "items", "totalProcessed", "conflictCount")]
    [InlineData("BulkModelMappingCreateResponse", "created", "existing", "failed", "totalProcessed", "createdCount", "existingCount", "successCount", "failureCount", "isSuccess", "isPartialSuccess")]
    [InlineData("BulkDeleteResult", "deletedIds", "errors", "totalProcessed", "successCount", "failureCount")]
    [InlineData("BulkUpdateResult", "updated", "errors", "totalProcessed", "successCount", "failureCount")]
    public void Admin_ModelMappingResponsesRequireAlwaysEmittedProperties(string schema, params string[] properties)
    {
        var required = _admin.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(schema).GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToList();
        required.Should().Contain(properties);
    }

    [Theory]
    [InlineData("GlobalSettingDto", "id", "key", "value", "description", "createdAt", "updatedAt")]
    [InlineData("CacheStats", "hitCount", "missCount", "invalidationCount", "hitRate", "averageGetTime", "lastResetTime", "lastInvalidationTime", "entryCount", "patternMatchCount", "isEnabled", "cachedKeys")]
    public void Admin_GlobalSettingsResponsesRequireAlwaysEmittedProperties(string schema, params string[] properties)
    {
        var required = _admin.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(schema).GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToList();
        required.Should().Contain(properties);
    }

    [Theory]
    [InlineData("IpFilterDto", "id", "filterType", "ipAddressOrCidr", "name", "isEnabled", "createdAt", "updatedAt")]
    [InlineData("IpFilterSettingsDto", "isEnabled", "defaultAllow", "bypassForAdminUi", "excludedEndpoints", "filterMode", "whitelistFilters", "blacklistFilters")]
    [InlineData("IpCheckResult", "isAllowed")]
    public void Admin_IpFilterResponsesRequireAlwaysEmittedProperties(string schema, params string[] properties)
    {
        var required = _admin.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(schema).GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToList();
        required.Should().Contain(properties);
    }

    [Theory]
    [InlineData("/v1/admin/function-configurations", "FunctionConfigurationDto")]
    [InlineData("/v1/admin/function-credentials", "FunctionCredentialDto")]
    [InlineData("/v1/admin/function-costs", "FunctionCostDto")]
    [InlineData("/v1/admin/function-executions/expired-leases", "AdminFunctionExecutionDto")]
    public void Admin_FunctionListsPublishTypedItems(string path, string schema)
    {
        ResponseSchema(_admin, path).GetProperty("properties").GetProperty("data")
            .GetProperty("items").GetProperty("$ref").GetString()
            .Should().Be($"#/components/schemas/{schema}");
    }

    [Theory]
    [InlineData("/v1/admin/function-configurations/{id}", "FunctionConfigurationDto")]
    [InlineData("/v1/admin/function-credentials/{id}", "FunctionCredentialDto")]
    [InlineData("/v1/admin/function-costs/{id}", "FunctionCostDto")]
    [InlineData("/v1/admin/function-executions/{id}", "AdminFunctionExecutionDto")]
    public void Admin_FunctionEntityReadsPublishTypedResponses(string path, string schema)
    {
        ResponseSchema(_admin, path).GetProperty("$ref").GetString()
            .Should().Be($"#/components/schemas/{schema}");
    }

    [Theory]
    [InlineData("/v1/admin/function-credentials/test", "post", "FunctionCredentialTestResultDto")]
    [InlineData("/v1/admin/function-costs/cache/clear", "post", "FunctionCostCacheClearResultDto")]
    [InlineData("/v1/admin/function-executions/cleanup", "delete", "FunctionExecutionCleanupResultDto")]
    public void Admin_FunctionAnonymousResultsUseNamedSchemas(string path, string method, string schema)
    {
        Operation(_admin, path, method).GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString().Should().Be($"#/components/schemas/{schema}");
    }

    [Fact]
    public void Contracts_DoNotPublishBareJsonSuccessSchemas()
    {
        foreach (var (name, document) in new[] { ("admin", _admin), ("gateway", _gateway) })
        {
            foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
            {
                foreach (var operation in path.Value.EnumerateObject()
                    .Where(member => new[] { "get", "put", "post", "delete", "options", "head", "patch", "trace" }
                        .Contains(member.Name)))
                {
                    foreach (var response in operation.Value.GetProperty("responses").EnumerateObject()
                        .Where(member => member.Name.StartsWith('2')))
                    {
                        if (!response.Value.TryGetProperty("content", out var content) ||
                            !content.TryGetProperty("application/json", out var json))
                        {
                            continue;
                        }

                        IsGenericSuccessSchema(json.GetProperty("schema")).Should().BeFalse(
                            $"{name} {operation.Name.ToUpperInvariant()} {path.Name} response {response.Name} must publish a concrete JSON schema");
                    }
                }
            }
        }
    }

    [Fact]
    public void Admin_FunctionConfigurationContractsUseBoundaryDtos()
    {
        RequestSchema(_admin, "/v1/admin/function-configurations", "post")
            .GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/CreateFunctionConfigurationRequest");
        RequestSchema(_admin, "/v1/admin/function-configurations/{id}", "patch")
            .GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/UpdateFunctionConfigurationRequest");

        var schemas = _admin.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.TryGetProperty("FunctionConfiguration", out _).Should().BeFalse(
            "the persistence entity must not be part of the public contract");
    }

    [Fact]
    public void FunctionExecutionContractsShareCanonicalBaseAndScopeAdminDiagnostics()
    {
        var adminSchemas = _admin.RootElement.GetProperty("components").GetProperty("schemas");
        var gatewaySchemas = _gateway.RootElement.GetProperty("components").GetProperty("schemas");
        var adminBase = adminSchemas.GetProperty("FunctionExecutionDto").GetProperty("properties");
        var gatewayBase = gatewaySchemas.GetProperty("FunctionExecutionDto").GetProperty("properties");

        adminBase.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "id", "functionId", "status", "input", "output", "error", "createdAt",
            "startedAt", "completedAt", "durationMs", "cost");
        gatewayBase.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "id", "function_id", "status", "input", "output", "error", "created_at",
            "started_at", "completed_at", "duration_ms", "cost");

        adminBase.GetProperty("input").GetProperty("type").ToString().Should().Contain("object");
        adminBase.GetProperty("output").GetProperty("type").ToString().Should().Contain("object");
        gatewayBase.GetProperty("input").GetProperty("type").ToString().Should().Contain("object");
        gatewayBase.GetProperty("output").GetProperty("type").ToString().Should().Contain("object");

        var adminExtension = adminSchemas.GetProperty("AdminFunctionExecutionDto");
        adminExtension.GetProperty("allOf")[0].GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/FunctionExecutionDto");
        adminExtension.GetProperty("properties").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("admin");

        var diagnostics = adminSchemas.GetProperty("FunctionExecutionAdminDetailsDto")
            .GetProperty("properties");
        diagnostics.TryGetProperty("leasedBy", out _).Should().BeTrue();
        diagnostics.TryGetProperty("leaseExpiresAt", out _).Should().BeTrue();
        adminBase.TryGetProperty("leasedBy", out _).Should().BeFalse();
        adminBase.TryGetProperty("version", out _).Should().BeFalse();
    }

    [Fact]
    public void FunctionFamilyContractsUseStructuredJsonAndUnitSuffixedDuration()
    {
        var adminSchemas = _admin.RootElement.GetProperty("components").GetProperty("schemas");
        var configuration = adminSchemas.GetProperty("FunctionConfigurationDto").GetProperty("properties");
        configuration.GetProperty("providerSettings").GetProperty("type").ToString().Should().Contain("object");
        configuration.GetProperty("parameterSchema").GetProperty("type").ToString().Should().Contain("object");

        foreach (var document in new[] { _admin, _gateway })
        {
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
            var execution = schemas.GetProperty("FunctionExecutionDto").GetProperty("properties");
            execution.TryGetProperty("duration", out _).Should().BeFalse();
            execution.EnumerateObject().Any(property => property.Name is "durationMs" or "duration_ms")
                .Should().BeTrue();
            schemas.GetProperty("FunctionExecutionCostDto").GetProperty("properties")
                .GetProperty("breakdown").GetProperty("type").ToString().Should().Contain("object");
        }
    }

    [Fact]
    public void AdminRoutesUseCanonicalResourceStyle()
    {
        foreach (var path in _admin.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (path.Name == "/metrics")
                continue;

            path.Name.Should().StartWith("/v1/admin/");
            foreach (var segment in path.Name["/v1/admin/".Length..].Split('/'))
            {
                if (segment.StartsWith('{') && segment.EndsWith('}'))
                    continue;
                segment.Should().MatchRegex("^[a-z0-9-]+$");
            }
        }
    }

    [Fact]
    public void AdminCollectionsUseCanonicalPaginationEnvelope()
    {
        var onlyArrayException = new List<string>();
        foreach (var (_, path, operation) in Operations(_admin).Where(item => item.Method == "GET"))
        {
            if (!operation.GetProperty("responses").TryGetProperty("200", out var response) ||
                !response.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("application/json", out var json))
                continue;

            var schema = ResolveSchema(_admin, json.GetProperty("schema"));
            if (schema.TryGetProperty("type", out var type) && type.GetString() == "array")
                onlyArrayException.Add(path);
        }

        // Both exceptions return a bounded set that pagination cannot meaningfully divide: a tail
        // window of recent errors, and the provider settings catalog, which is fixed at the size of
        // the compiled registry. Every other collection read must publish the pagination envelope.
        onlyArrayException.Should().BeEquivalentTo(
            "/v1/admin/provider-errors/recent",
            "/v1/admin/providers/settings-schema");

        var models = ResolveSchema(_admin, ResponseSchema(_admin, "/v1/admin/models"));
        models.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().BeEquivalentTo("data", "pagination");
        models.GetProperty("properties").GetProperty("pagination").GetProperty("required")
            .EnumerateArray().Select(item => item.GetString())
            .Should().BeEquivalentTo("page", "pageSize", "totalItems", "totalPages");
    }

    [Fact]
    public void AdminPatchBodiesArePartialAndDoNotRepeatPathIdentifiers()
    {
        foreach (var (method, path, operation) in Operations(_admin).Where(item => item.Method == "PATCH"))
        {
            var content = operation.GetProperty("requestBody").GetProperty("content");
            content.TryGetProperty("application/json", out _)
                .Should().BeFalse($"{method} {path} only accepts RFC 7386 JSON Merge Patch");
            var schema = ResolveSchema(
                _admin,
                content.GetProperty("application/merge-patch+json").GetProperty("schema"));

            if (schema.TryGetProperty("required", out var required))
                required.GetArrayLength().Should().Be(0, $"{method} {path} must be partial");
            schema.GetProperty("properties").TryGetProperty("id", out _)
                .Should().BeFalse($"{method} {path} already carries its identifier in the path");

            var responses = operation.GetProperty("responses");
            responses.TryGetProperty("200", out _)
                .Should().BeTrue($"{method} {path} returns the updated resource");
            responses.TryGetProperty("204", out _)
                .Should().BeFalse($"{method} {path} returns a response body");
        }
    }

    [Theory]
    [InlineData("/v1/admin/virtual-keys/{id}")]
    [InlineData("/v1/admin/virtual-key-groups/{id}")]
    [InlineData("/v1/admin/ip-filters/{id}")]
    public void VersionedResourcesPublishConditionalRequestContracts(string path)
    {
        Operation(_admin, path, "get").GetProperty("responses").GetProperty("200")
            .GetProperty("headers").TryGetProperty("ETag", out _).Should().BeTrue();

        foreach (var method in new[] { "patch", "delete" })
        {
            var operation = Operation(_admin, path, method);
            operation.GetProperty("parameters").EnumerateArray()
                .Should().Contain(parameter =>
                    parameter.GetProperty("name").GetString() == "If-Match" &&
                    parameter.GetProperty("required").GetBoolean());
            operation.GetProperty("responses").TryGetProperty("412", out _).Should().BeTrue();
            operation.GetProperty("responses").TryGetProperty("428", out _).Should().BeTrue();
        }
    }

    [Fact]
    public void AdminStructuredFieldsAreObjectsInsteadOfSerializedJsonStrings()
    {
        var schemas = _admin.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
                continue;
            foreach (var property in properties.EnumerateObject())
                property.Name.Should().NotEndWith("Json");
        }

        foreach (var (schema, property) in new[]
        {
            ("VirtualKeyDto", "metadata"),
            ("ModelDto", "modelParameters"),
            ("ModelIdentifierDto", "metadata"),
            ("ModelSeriesDto", "parameters"),
            ("ModelProviderMappingDto", "providerOptions"),
            ("ModelCostDto", "pricingConfiguration"),
            ("PricingAuditEventDto", "inputParameters"),
            ("DriftItemDto", "currentValues"),
            ("DriftItemDto", "proposedValues")
        })
        {
            ResolveSchema(_admin, schemas.GetProperty(schema).GetProperty("properties").GetProperty(property))
                .GetProperty("type").ToString().Should().Contain("object");
        }
    }

    public void Dispose()
    {
        _admin.Dispose();
        _gateway.Dispose();
    }

    private static OpenApiPathItem PathWith(HttpMethod method, string operationId) => new()
    {
        Operations = new Dictionary<HttpMethod, OpenApiOperation>
        {
            [method] = new() { OperationId = operationId }
        }
    };

    private static JsonElement Operation(JsonDocument document, string path, string method) =>
        document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static JsonElement Scheme(JsonDocument document, string name) =>
        document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty(name);

    private static JsonElement ResponseSchema(JsonDocument document, string path) =>
        Operation(document, path, "get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

    private static JsonElement RequestSchema(JsonDocument document, string path, string method) =>
        Operation(document, path, method).GetProperty("requestBody").GetProperty("content")
            .GetProperty(method == "patch" ? "application/merge-patch+json" : "application/json")
            .GetProperty("schema");

    private static IEnumerable<(string Method, string Path, JsonElement Operation)> Operations(
        JsonDocument document)
    {
        var methods = new HashSet<string>(
            ["get", "put", "post", "delete", "options", "head", "patch", "trace"],
            StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(item => methods.Contains(item.Name)))
            {
                yield return (operation.Name.ToUpperInvariant(), path.Name, operation.Value);
            }
        }
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement element)
    {
        yield return element;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var descendant in Descendants(property.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in Descendants(item))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static IEnumerable<string> SchemaReferences(JsonElement element)
    {
        const string prefix = "#/components/schemas/";

        // Covers both $ref values and the schema names a discriminator maps to.
        return Descendants(element)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(value => value[prefix.Length..]);
    }

    private static JsonElement ResolveSchema(JsonDocument document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference))
        {
            return schema;
        }

        var schemaName = reference.GetString()!.Split('/').Last();
        return document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(schemaName);
    }

    private static bool IsGenericSuccessSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.EnumerateObject().Any())
        {
            return true;
        }
        if (schema.TryGetProperty("$ref", out _))
        {
            return false;
        }
        foreach (var composition in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (schema.TryGetProperty(composition, out var branches))
            {
                return !branches.EnumerateArray().Any() ||
                    branches.EnumerateArray().All(IsGenericSuccessSchema);
            }
        }
        if (!schema.TryGetProperty("type", out var type) || type.GetString() != "object")
        {
            if (type.ValueKind == JsonValueKind.String && type.GetString() == "array")
            {
                return !schema.TryGetProperty("items", out var items) || IsGenericSuccessSchema(items);
            }
            return false;
        }

        return (!schema.TryGetProperty("properties", out var properties) ||
                !properties.EnumerateObject().Any()) &&
            !schema.TryGetProperty("additionalProperties", out _) &&
            !schema.TryGetProperty("patternProperties", out _);
    }

    private static string? JsonElementReference(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var direct))
        {
            return direct.GetString();
        }

        return schema.GetProperty("oneOf").EnumerateArray()
            .First(branch => branch.TryGetProperty("$ref", out _))
            .GetProperty("$ref").GetString();
    }

    private static JsonDocument LoadContract(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Conduit.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test must run from a ConduitLLM checkout");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine([directory!.FullName, .. relativeSegments])));
    }
}
