using ConduitLLM.Core.OpenApi;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Security;

using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints;

public static class FunctionCredentialsEndpoints
{
    public static IEndpointRouteBuilder MapFunctionCredentialsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/function-credentials")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("FunctionCredentials");

        group.MapGet("/", List).WithName("FunctionCredentials_List")
            .WithSummary("List function provider credentials")
            .WithDescription("Returns credential metadata with protected API keys masked; plaintext secrets are not returned.").Produces<List<FunctionCredentialDto>>();
        group.MapGet("/configuration/{functionConfigurationId:int}", GetByConfiguration)
            .WithName("FunctionCredentials_GetByConfiguration")
            .WithSummary("List credentials for a function configuration")
            .WithDescription("Returns masked credentials for the provider type used by this function configuration.")
            .WithParameterDescription("functionConfigurationId", "Identifier of the function configuration.").Produces<List<FunctionCredentialDto>>()
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapGet("/{id:int}", GetById).WithName("FunctionCredentials_GetById")
            .WithSummary("Get a function provider credential")
            .WithDescription("Returns credential metadata with the protected API key masked.")
            .WithParameterDescription("id", "Identifier of the FunctionCredentials resource.")
            .Produces<FunctionCredentialDto>().Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/", Create).WithName("FunctionCredentials_Create")
            .WithSummary("Create a function provider credential")
            .WithDescription("Creates credential metadata and masks the API key in the response. Configuration-scoped secrets are encrypted before storage; provider-global credentials use their existing storage policy.")
            .Produces<FunctionCredentialDto>(StatusCodes.Status201Created)
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapPatch("/{id:int}", Update).AcceptsJsonMergePatch<UpdateFunctionCredentialRequest>().WithName("FunctionCredentials_Update")
            .WithSummary("Update a function provider credential")
            .WithDescription("Applies a JSON Merge Patch and masks the API key in the response. Configuration-scoped secrets are encrypted before storage; provider-global credentials use their existing storage policy.")
            .WithParameterDescription("id", "Identifier of the FunctionCredentials resource.")
            .Produces<FunctionCredentialDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapDelete("/{id:int}", Delete).WithName("FunctionCredentials_Delete")
            .WithSummary("Delete a function provider credential")
            .WithParameterDescription("id", "Identifier of the FunctionCredentials resource.")
            .Produces(StatusCodes.Status204NoContent).Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
        group.MapPost("/test", Test).WithName("FunctionCredentials_Test")
            .WithSummary("Test a function provider credential")
            .Produces<FunctionCredentialTestResultDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        return app;
    }

    private static async Task<IResult> List([FromServices] IFunctionCredentialRepository repository) =>
        Results.Ok((await repository.GetAllUnboundedAsync()).Select(ToDto).ToList());

    private static async Task<IResult> GetByConfiguration(
        int functionConfigurationId,
        [FromServices] IFunctionCredentialRepository credentials,
        [FromServices] IFunctionConfigurationRepository configurations)
    {
        var configuration = await configurations.GetByIdAsync(functionConfigurationId)
            ?? throw new KeyNotFoundException();
        return Results.Ok((await credentials.GetByProviderTypeAsync(configuration.ProviderType)).Select(ToDto).ToList());
    }

    private static async Task<IResult> GetById(int id, [FromServices] IFunctionCredentialRepository repository)
    {
        var credential = await repository.GetByIdAsync(id);
        return credential is null
            ? AdminResults.NotFoundEntity("Function credential", id)
            : Results.Ok(ToDto(credential));
    }

    private static async Task<IResult> Create(
        [FromBody] CreateFunctionCredentialRequest request,
        [FromServices] IFunctionCredentialRepository repository,
        [FromServices] IFunctionCredentialProtector protector,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        var credential = new FunctionCredential
        {
            ProviderType = request.ProviderType,
            FunctionConfigurationId = request.FunctionConfigurationId,
            ApiKey = request.ApiKey,
            BaseUrl = request.BaseUrl,
            Organization = request.Organization,
            FunctionAccountGroup = request.FunctionAccountGroup,
            IsPrimary = request.IsPrimary,
            IsEnabled = request.IsEnabled,
            KeyName = request.KeyName
        };
        EncryptScopedSecret(credential, protector);
        var id = await repository.CreateAsync(credential);
        var created = await repository.GetByIdAsync(id);
        Audit(httpContext, loggerFactory, "Created", id, credential);
        return Results.Created($"/v1/admin/function-credentials/{id}", created is null ? null : ToDto(created));
    }

    private static async Task<IResult> Update(
        int id,
        JsonMergePatch<UpdateFunctionCredentialRequest> patch,
        [FromServices] IFunctionCredentialRepository repository,
        [FromServices] IFunctionCredentialProtector protector,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        var request = patch.Value;
        var credential = await repository.GetByIdAsync(id) ?? throw new KeyNotFoundException();
        if (request.TryGetPatchedProperty(nameof(request.ApiKey), credential.ApiKey, out string? apiKey))
            credential.ApiKey = apiKey;
        if (request.TryGetPatchedProperty(nameof(request.BaseUrl), credential.BaseUrl, out string? baseUrl))
            credential.BaseUrl = baseUrl;
        if (request.TryGetPatchedProperty(nameof(request.Organization), credential.Organization, out string? organization))
            credential.Organization = organization;
        if (request.TryGetPatchedProperty(nameof(request.FunctionAccountGroup), credential.FunctionAccountGroup, out short accountGroup))
            credential.FunctionAccountGroup = accountGroup;
        if (request.TryGetPatchedProperty(nameof(request.IsPrimary), credential.IsPrimary, out bool isPrimary))
            credential.IsPrimary = isPrimary;
        if (request.TryGetPatchedProperty(nameof(request.IsEnabled), credential.IsEnabled, out bool isEnabled))
            credential.IsEnabled = isEnabled;
        if (request.TryGetPatchedProperty(nameof(request.KeyName), credential.KeyName, out string? keyName))
            credential.KeyName = keyName;
        EncryptScopedSecret(credential, protector);
        await repository.UpdateAsync(credential);
        var updated = await repository.GetByIdAsync(id) ?? throw new KeyNotFoundException();
        Audit(httpContext, loggerFactory, "Updated", id, credential);
        return Results.Ok(ToDto(updated));
    }

    private static FunctionCredentialDto ToDto(FunctionCredential credential) => new()
    {
        Id = credential.Id,
        FunctionConfigurationId = credential.FunctionConfigurationId ?? 0,
        MaskedApiKey = string.IsNullOrEmpty(credential.ApiKey) ? null : "********",
        BaseUrl = credential.BaseUrl,
        Organization = credential.Organization,
        FunctionAccountGroup = credential.FunctionAccountGroup,
        IsPrimary = credential.IsPrimary,
        IsEnabled = credential.IsEnabled,
        KeyName = credential.KeyName,
        CreatedAt = credential.CreatedAt,
        UpdatedAt = credential.UpdatedAt
    };

    private static async Task<IResult> Delete(
        int id,
        [FromServices] IFunctionCredentialRepository repository,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        var credential = await repository.GetByIdAsync(id);
        await repository.DeleteAsync(id);
        if (credential is not null)
        {
            Audit(httpContext, loggerFactory, "Deleted", id, credential);
        }
        else
        {
            AdminAudit.Log(httpContext, Logger(loggerFactory), "Deleted", "FunctionCredential", id);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> Test(
        [FromBody] TestFunctionCredentialRequest testRequest,
        [FromServices] IFunctionCredentialRepository credentials,
        [FromServices] IFunctionConfigurationRepository configurations,
        [FromServices] IFunctionClientFactory clientFactory,
        [FromServices] IFunctionCredentialProtector protector)
    {
        var credential = await credentials.GetByIdAsync(testRequest.CredentialId)
            ?? throw new KeyNotFoundException();

        // Prefer the credential's own configuration (config-scoped/MCP); fall back to any config of
        // the provider type (provider-global Exa/Tavily).
        var configuration = (credential.FunctionConfigurationId is int scopedConfigId
                ? await configurations.GetByIdAsync(scopedConfigId)
                : null)
            ?? (await configurations.GetByProviderTypeAsync(credential.ProviderType)).FirstOrDefault()
            ?? throw new KeyNotFoundException();

        var client = await clientFactory.GetClientAsync(credential.ProviderType, configuration.Id);
        var apiKey = testRequest.ApiKeyOverride ?? protector.Reveal(credential.ApiKey);
        var result = await client.VerifyAuthenticationAsync(apiKey);
        return Results.Ok(new
        {
            success = result.IsSuccess,
            message = result.Message,
            details = result.Details,
            durationMs = result.ResponseTimeMs
        });
    }

    /// <summary>
    /// Encrypts the secret of a config-scoped credential (e.g. an MCP server token) at rest.
    /// Provider-global credentials (Exa/Tavily) are left as-is pending a separate encryption pass.
    /// <see cref="IFunctionCredentialProtector.Protect"/> is idempotent, so re-saving an already
    /// encrypted value does not double-encrypt.
    /// </summary>
    private static void EncryptScopedSecret(FunctionCredential credential, IFunctionCredentialProtector protector)
    {
        if (credential.FunctionConfigurationId.HasValue)
        {
            credential.ApiKey = protector.Protect(credential.ApiKey);
        }
    }

    private static void Audit(
        HttpContext context,
        ILoggerFactory loggerFactory,
        string operation,
        int id,
        FunctionCredential credential) =>
        AdminAudit.Log(context, Logger(loggerFactory), operation, "FunctionCredential", id,
            $"ProviderType: {credential.ProviderType}");

    private static ILogger Logger(ILoggerFactory factory) =>
        factory.CreateLogger("ConduitLLM.Admin.Endpoints.FunctionCredentials");
}

public sealed class TestFunctionCredentialRequest
{
    public int CredentialId { get; set; }
    public string? ApiKeyOverride { get; set; }
}
