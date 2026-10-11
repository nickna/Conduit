using ConduitLLM.Core.OpenApi;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Models;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Gateway.Services;
using Microsoft.AspNetCore.Mvc;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Models.Responses;
using ConduitLLM.Core.Models.Rerank;
using ConduitLLM.Gateway.DTOs;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services.Strategies;
using ConduitLLM.Gateway.RateLimiting;

namespace ConduitLLM.Gateway.Endpoints;

/// <summary>Registers Gateway endpoint handlers and maps their HTTP contracts.</summary>
public static class GatewayApiEndpoints
{
    public static IServiceCollection AddGatewayEndpointHandlers(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<AuthEndpoints>();
        services.AddScoped<CompletionsEndpoints>();
        services.AddScoped<DiscoveryEndpoints>();
        services.AddScoped<TasksEndpoints>();
        services.AddScoped<FunctionsEndpoints>();
        services.AddScoped<RerankEndpoints>();
        services.AddScoped<EmbeddingsEndpoints>();
        services.AddScoped<AudioEndpoints>();
        services.AddScoped<MediaEndpoints>();
        services.AddScoped<DownloadsEndpoints>();
        services.AddScoped<ImagesEndpoints>();
        services.AddScoped<Base64MediaProcessor>();
        services.AddScoped<UrlMediaProcessor>();
        services.AddScoped<VideosEndpoints>();
        services.AddScoped<ChatEndpoints>();
        services.AddScoped<IResponsesChatExecutor, ResponsesChatExecutor>();
        services.AddScoped<ResponsesEndpoints>();
        return services;
    }

    public static IEndpointRouteBuilder MapGatewayApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/conduit/auth/ephemeral-key", ([FromServices] AuthEndpoints endpoints, GenerateEphemeralKeyRequest? request) => endpoints.GenerateEphemeralKey(request))
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Authentication").WithName("Auth_GenerateEphemeralKey")
            .WithSummary("Generate an ephemeral key for the authenticated virtual key")
            .Produces<EphemeralKeyResponse>(StatusCodes.Status200OK)
            .Produces<OpenAIErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<OpenAIErrorResponse>(StatusCodes.Status500InternalServerError);

        app.MapPost("/v1/completions", ([FromServices] CompletionsEndpoints endpoints) => endpoints.CreateCompletion())
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Completions").WithName("Completions_Create")
            .Produces<OpenAIErrorResponse>(StatusCodes.Status501NotImplemented)
            .ExcludeFromDescription();

        var discovery = app.MapGroup("/v1/conduit/discovery")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Discovery");
        discovery.MapGet("/models", ([FromServices] DiscoveryEndpoints endpoints, string? capability) => endpoints.GetModels(capability))
            .WithName("Discovery_GetModels")
            .WithSummary("Gets all discovered models and their capabilities for authenticated virtual keys.")
            .WithParameterDescription("capability", "Optional capability filter (e.g., \"video_generation\", \"vision\")").Produces<DiscoveryModelsResponse>();
        discovery.MapGet("/capabilities", ([FromServices] DiscoveryEndpoints endpoints) => endpoints.GetCapabilities())
            .WithName("Discovery_GetCapabilities")
            .WithSummary("Gets all available capabilities in the system.").Produces<DiscoveryCapabilitiesResponse>();
        discovery.MapGet("/models/{model}/parameters", ([FromServices] DiscoveryEndpoints endpoints, string model) => endpoints.GetModelParameters(model))
            .WithName("Discovery_GetModelParameters")
            .WithSummary("Gets UI parameters for a specific model to enable dynamic UI generation.")
            .WithParameterDescription("model", "The model alias or identifier to get parameters for").Produces<ModelParametersResponse>()
            .Produces<OpenAIErrorResponse>(404);
        discovery.MapGet("/functions", ([FromServices] DiscoveryEndpoints endpoints, string? purpose, string? providerType) => endpoints.GetFunctions(purpose, providerType))
            .WithName("Discovery_GetFunctions")
            .WithSummary("Gets all available function configurations for authenticated virtual keys.")
            .WithParameterDescription("purpose", "Optional purpose filter (e.g., \"Search\", \"Answer\", \"RAG\")")
            .WithParameterDescription("providerType", "Optional provider type filter (e.g., \"Exa\", \"Perplexity\")").Produces<FunctionDiscoveryResponse>();
        discovery.MapGet("/functions/{functionConfigurationId}/parameters", ([FromServices] DiscoveryEndpoints endpoints, int functionConfigurationId) => endpoints.GetFunctionParameters(functionConfigurationId))
            .WithName("Discovery_GetFunctionParameters")
            .WithSummary("Get the parameter schema for a function configuration")
            .WithDescription("Use the schema to generate input controls for function execution.")
            .WithParameterDescription("functionConfigurationId", "The function configuration ID").Produces<FunctionParametersResponseDto>()
            .Produces<OpenAIErrorResponse>(404);

        var tasks = app.MapGroup("/v1/conduit/tasks")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Tasks");
        tasks.MapGet("/{taskId}", ([FromServices] TasksEndpoints endpoints, string taskId) => endpoints.GetTaskStatus(taskId))
            .WithName("Tasks_GetStatus")
            .WithSummary("Gets the status of a specific task.")
            .WithParameterDescription("taskId", "The ID of the task to retrieve.").Produces<AsyncTaskStatus>()
            .Produces<OpenAIErrorResponse>(404);
        tasks.MapPost("/{taskId}/cancel", ([FromServices] TasksEndpoints endpoints, string taskId) => endpoints.CancelTask(taskId))
            .WithName("Tasks_Cancel")
            .WithSummary("Cancels a running task.")
            .WithParameterDescription("taskId", "The ID of the task to cancel.").Produces(StatusCodes.Status204NoContent)
            .Produces<OpenAIErrorResponse>(404);
        tasks.MapGet("/{taskId}/poll", ([FromServices] TasksEndpoints endpoints, string taskId, int timeout = 300, int interval = 2) => endpoints.PollTask(taskId, timeout, interval))
            .WithName("Tasks_Poll")
            .WithSummary("Polls a task until it completes or times out.")
            .WithDescription("Waits for task status changes up to the requested timeout. Access is restricted to tasks owned by the authenticated virtual key.")
            .WithParameterDescription("taskId", "The ID of the task to poll.")
            .WithParameterDescription("timeout", "Maximum time to wait in seconds (default: 300, max: 600).")
            .WithParameterDescription("interval", "Polling interval in seconds (default: 2, min: 1).").Produces<AsyncTaskStatus>()
            .Produces<OpenAIErrorResponse>(404)
            .Produces<OpenAIErrorResponse>(408);

        var functions = app.MapGroup("/v1/conduit/functions")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Functions");
        functions.MapPost("/execute", ([FromServices] FunctionsEndpoints endpoints, FunctionsEndpoints.FunctionExecutionRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) => endpoints.ExecuteFunction(request, idempotencyKey, cancellationToken))
            .WithName("Functions_Execute")
            .WithParameterDescription("Idempotency-Key", "Client-supplied key for deduplicating repeated requests. Reuse the same key only for the same intended operation.")
            .WithSummary("Executes a function with the provided parameters.").Produces<FunctionExecutionDto>().Produces<OpenAIErrorResponse>(400).Produces<OpenAIErrorResponse>(402).Produces<OpenAIErrorResponse>(404).Produces(500);
        functions.MapGet("/executions/{executionId}", ([FromServices] FunctionsEndpoints endpoints, Guid executionId, CancellationToken cancellationToken) => endpoints.GetExecution(executionId, cancellationToken))
            .WithName("Functions_GetExecution")
            .WithSummary("Gets the status and result of a function execution.")
            .WithParameterDescription("executionId", "The execution ID").Produces<FunctionExecutionDto>().Produces<OpenAIErrorResponse>(402).Produces<OpenAIErrorResponse>(404).Produces(500);

        app.MapPost("/v1/conduit/rerank", ([FromServices] RerankEndpoints endpoints, RerankRequest request, CancellationToken cancellationToken) => endpoints.CreateRerank(request, cancellationToken))
            .RequireAuthorization("VirtualKeyAuthentication").AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>().WithTags("Rerank").WithName("Rerank_Create")
            .WithSummary("Scores and orders documents by relevance to a query.")
            .Produces<RerankResponse>()
            .Produces<OpenAIErrorResponse>(402);

        app.MapPost("/v1/embeddings", ([FromServices] EmbeddingsEndpoints endpoints, EmbeddingRequest request, CancellationToken cancellationToken) => endpoints.CreateEmbedding(request, cancellationToken))
            .RequireAuthorization("VirtualKeyAuthentication").AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<TokenRateLimitFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>().WithTags("Embeddings").WithName("Embeddings_Create")
            .WithSummary("Creates embeddings for the given input.")
            .Produces<EmbeddingResponse>().Produces<OpenAIErrorResponse>(400).Produces<OpenAIErrorResponse>(404).Produces<OpenAIErrorResponse>(500)
            .Produces<OpenAIErrorResponse>(402);

        var audio = app.MapGroup("/v1/audio")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Audio");
        audio.MapPost("/transcriptions", ([FromServices] AudioEndpoints endpoints,
                [FromForm] IFormFile file, [FromForm] string model, [FromForm] string? language,
                [FromForm] string? prompt, [FromForm(Name = "response_format")] string? responseFormat,
                [FromForm] double? temperature,
                [FromForm(Name = "chunking_strategy")] string? chunkingStrategy,
                [FromForm] bool? stream,
                CancellationToken cancellationToken) =>
                endpoints.CreateTranscription(
                    file, model, language, prompt, responseFormat, temperature,
                    chunkingStrategy, stream, cancellationToken))
            .WithName("Audio_CreateTranscription")
            .WithSummary("Transcribes uploaded audio to text (OpenAI /audio/transcriptions compatible).")
            .WithDescription("Accepts multipart audio with required file and model fields. The request body limit is 25 MiB. Response format, streaming, and diarization support depend on the model/provider.").DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitMetadata(26_214_400))
            .Produces<AudioTranscriptionResponse>()
            .Produces<OpenAIErrorResponse>(402);
        audio.MapPost("/speech", ([FromServices] AudioEndpoints endpoints, TextToSpeechRequest request, CancellationToken cancellationToken) => endpoints.CreateSpeech(request, cancellationToken))
            .WithName("Audio_CreateSpeech")
            .WithSummary("Synthesizes speech from text (OpenAI /audio/speech compatible). Returns raw audio bytes.")
            .WithDescription("Returns generated audio bytes. The response_format selects the encoding; supported formats depend on the configured provider.")
            .Produces<byte[]>(200, "application/octet-stream")
            .Produces<OpenAIErrorResponse>(402);

        var media = app.MapGroup("/v1/conduit/media").WithTags("Media");
        media.MapPost("/upload", ([FromServices] MediaEndpoints endpoints, [FromForm] IFormFile file, [FromForm(Name = "media_type")] string? mediaType) => endpoints.UploadMedia(file, mediaType))
            .RequireAuthorization("VirtualKeyAuthentication").AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithName("Media_Upload")
            .WithSummary("Uploads a media file and returns the storage URL.")
            .WithDescription("Uploads a required file and returns its storage key and URL. The optional media_type overrides extension-based detection. Limits are 100 MiB for images, 200 MiB for audio, and 500 MiB for video.").DisableAntiforgery().WithMetadata(new RequestSizeLimitMetadata(524_288_000))
            .Produces<MediaUploadResponse>();
        media.MapGet("/info/{**storageKey}", ([FromServices] MediaEndpoints endpoints, string storageKey) => endpoints.GetMediaInfo(storageKey))
            .RequireAuthorization("VirtualKeyAuthentication").WithName("Media_GetInfo")
            .WithSummary("Gets metadata information about a media file.")
            .WithParameterDescription("storageKey", "The unique storage key.").Produces<MediaInfo>()
            .Produces<OpenAIErrorResponse>(404);
        media.MapGet("/{**storageKey}", ([FromServices] MediaEndpoints endpoints, string storageKey) => endpoints.GetMedia(storageKey))
            .AllowAnonymous().WithName("Media_Get")
            .WithSummary("Retrieves a media file by its storage key.")
            .WithDescription("Retrieves media by storage key. No virtual key is required; IP security policies still apply. Video retrieval supports byte ranges.")
            .WithParameterDescription("storageKey", "The unique storage key.").Produces<byte[]>(200, "application/octet-stream")
            .Produces<OpenAIErrorResponse>(404)
            .Produces<OpenAIErrorResponse>(416);
        media.MapMethods("/{**storageKey}", [HttpMethods.Head], ([FromServices] MediaEndpoints endpoints, string storageKey) => endpoints.CheckMediaExists(storageKey))
            .AllowAnonymous().WithName("Media_Head")
            .WithSummary("Checks if a media file exists.")
            .WithDescription("Checks whether a media object exists without returning its body. No virtual key is required; IP security policies still apply.")
            .WithParameterDescription("storageKey", "The unique storage key.").Produces(200)
            .Produces<OpenAIErrorResponse>(404);

        var downloads = app.MapGroup("/v1/conduit/downloads")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Downloads");
        downloads.MapGet("/metadata/{**fileId}", ([FromServices] DownloadsEndpoints endpoints, string fileId) => endpoints.GetFileMetadata(fileId))
            .WithName("Downloads_GetMetadata")
            .WithSummary("Gets metadata information about a file.")
            .WithParameterDescription("fileId", "The file identifier.").Produces<FileMetadataResponse>()
            .Produces<OpenAIErrorResponse>(404);
        downloads.MapPost("/generate-url", ([FromServices] DownloadsEndpoints endpoints, GenerateUrlRequest request) => endpoints.GenerateDownloadUrl(request))
            .WithName("Downloads_GenerateUrl")
            .WithSummary("Generates a temporary download URL for a file.").Produces<DownloadUrlResponse>()
            .Produces<OpenAIErrorResponse>(404);
        downloads.MapGet("/{**fileId}", ([FromServices] DownloadsEndpoints endpoints, string fileId, bool inline = false) => endpoints.DownloadFile(fileId, inline))
            .WithName("Downloads_Get")
            .WithSummary("Downloads a file by its identifier with support for range requests.")
            .WithDescription("Downloads a file accessible to the authenticated virtual key. Set inline=true for inline Content-Disposition.")
            .WithParameterDescription("fileId", "The file identifier (storage key or URL).")
            .WithParameterDescription("inline", "Whether to display inline (true) or force download (false).").Produces<byte[]>(200, "application/octet-stream")
            .Produces<OpenAIErrorResponse>(404);
        downloads.MapMethods("/{**fileId}", [HttpMethods.Head], ([FromServices] DownloadsEndpoints endpoints, string fileId) => endpoints.CheckFileExists(fileId))
            .WithName("Downloads_Head")
            .WithSummary("Checks if a file exists.")
            .WithParameterDescription("fileId", "The file identifier.").Produces(200)
            .Produces<OpenAIErrorResponse>(404);

        var images = app.MapGroup("/v1/images")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Images");
        images.MapPost("/generations", ([FromServices] ImagesEndpoints endpoints, ImageGenerationRequest request, CancellationToken cancellationToken) => endpoints.CreateImage(request, cancellationToken))
            .WithName("Images_Create")
            .WithSummary("Creates one or more images given a prompt.").Produces<ImageGenerationResponse>()
            .Produces<OpenAIErrorResponse>(402);
        var conduitImages = app.MapGroup("/v1/conduit/images")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Images");
        conduitImages.MapPost("/generations/async", ([FromServices] ImagesEndpoints endpoints, ImageGenerationRequest request) => endpoints.CreateImageAsync(request))
            .WithName("Images_CreateImageAsync")
            .WithSummary("Creates an async image generation task.")
            .WithDescription("Accepts an image generation job and returns a task ID. Poll the task status endpoint for completion; acceptance is not the final generation result.")
            .Produces<AsyncTaskResponse>(StatusCodes.Status202Accepted)
            .Produces<OpenAIErrorResponse>(402);
        conduitImages.MapGet("/generations/{taskId}/status", ([FromServices] ImagesEndpoints endpoints, string taskId) => endpoints.GetGenerationStatus(taskId))
            .WithName("Images_GetStatus")
            .WithSummary("Gets the status of an async image generation task.")
            .WithParameterDescription("taskId", "The task ID.").Produces<AsyncTaskStatusResponse>()
            .Produces<OpenAIErrorResponse>(402)
            .Produces<OpenAIErrorResponse>(404);
        conduitImages.MapDelete("/generations/{taskId}", ([FromServices] ImagesEndpoints endpoints, string taskId) => endpoints.CancelGeneration(taskId))
            .WithName("Images_Cancel")
            .WithSummary("Cancels an async image generation task.")
            .WithParameterDescription("taskId", "The task ID to cancel.").Produces<TaskCancellationResponse>()
            .Produces<OpenAIErrorResponse>(402)
            .Produces<OpenAIErrorResponse>(404);

        var videos = app.MapGroup("/v1/conduit/videos")
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Videos");
        videos.MapPost("/generations/async", ([FromServices] VideosEndpoints endpoints, VideoGenerationRequest request, CancellationToken cancellationToken) => endpoints.GenerateVideoAsync(request, cancellationToken))
            .WithName("Videos_GenerateAsync")
            .WithSummary("Starts an asynchronous video generation task.")
            .WithDescription("Accepts a video generation job and returns a task ID. Status, retry, and cancellation are scoped to the authenticated virtual key.").Produces<AsyncTaskResponse>(202).Produces<OpenAIErrorResponse>(400).Produces<OpenAIErrorResponse>(401).Produces<OpenAIErrorResponse>(403).Produces<OpenAIErrorResponse>(429).Produces<OpenAIErrorResponse>(500)
            .Produces<OpenAIErrorResponse>(402);
        videos.MapGet("/generations/tasks/{taskId}", ([FromServices] VideosEndpoints endpoints, string taskId, CancellationToken cancellationToken) => endpoints.GetTaskStatus(taskId, cancellationToken))
            .WithName("Videos_GetTaskStatus")
            .WithSummary("Gets the status of a video generation task.")
            .WithParameterDescription("taskId", "Identifier returned when the asynchronous task was accepted.").Produces<VideoGenerationTaskStatus>().Produces<OpenAIErrorResponse>(401).Produces<OpenAIErrorResponse>(402).Produces<OpenAIErrorResponse>(404).Produces<OpenAIErrorResponse>(500);
        videos.MapPost("/generations/tasks/{taskId}/retry", ([FromServices] VideosEndpoints endpoints, string taskId, CancellationToken cancellationToken) => endpoints.RetryTask(taskId, cancellationToken))
            .WithName("Videos_RetryTask")
            .WithSummary("Manually retries a failed video generation task.")
            .WithParameterDescription("taskId", "Identifier returned when the asynchronous task was accepted.").Produces<VideoGenerationTaskStatus>().Produces<OpenAIErrorResponse>(400).Produces<OpenAIErrorResponse>(401).Produces<OpenAIErrorResponse>(402).Produces<OpenAIErrorResponse>(404).Produces<OpenAIErrorResponse>(500);
        videos.MapDelete("/generations/{taskId}", ([FromServices] VideosEndpoints endpoints, string taskId, CancellationToken cancellationToken) => endpoints.CancelTask(taskId, cancellationToken))
            .WithName("Videos_CancelTask")
            .WithSummary("Cancels a video generation task.")
            .WithParameterDescription("taskId", "Identifier returned when the asynchronous task was accepted.").Produces(204).Produces<OpenAIErrorResponse>(401).Produces<OpenAIErrorResponse>(402).Produces<OpenAIErrorResponse>(404).Produces<OpenAIErrorResponse>(409).Produces<OpenAIErrorResponse>(500);

        app.MapPost("/v1/chat/completions", ([FromServices] ChatEndpoints endpoints, ChatCompletionRequest request, CancellationToken cancellationToken) => endpoints.CreateChatCompletion(request, cancellationToken))
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<TokenRateLimitFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Chat").WithName("Chat_CreateCompletion")
            .WithSummary("Creates a chat completion.")
            .WithDescription("Returns a chat completion as application/json. Set stream=true to receive OpenAI text/event-stream chunks and a [DONE] sentinel. Set stream_options.include_usage=true for a final usage chunk when the provider reports usage. Conduit progress and metrics events are available at /v1/conduit/chat/completions. Models and capabilities depend on the virtual key and configured provider.")
            .Produces<ChatCompletionResponse>(200, "application/json")
            .Produces<OpenAIErrorResponse>(400)
            .Produces<OpenAIErrorResponse>(404)
            .Produces<OpenAIErrorResponse>(500)
            .Produces<OpenAIErrorResponse>(402);

        app.MapPost("/v1/conduit/chat/completions", ([FromServices] ChatEndpoints endpoints, ChatCompletionRequest request, CancellationToken cancellationToken) => endpoints.CreateChatCompletion(request, cancellationToken))
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<TokenRateLimitFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Chat").WithName("Chat_CreateEnhancedCompletion")
            .WithSummary("Creates a chat completion with Conduit streaming events.")
            .WithDescription("Uses the same chat execution and accounting as /v1/chat/completions. Set stream=true for chat chunks plus named Conduit metrics, reasoning, and tool progress events, followed by [DONE].")
            .Produces<ChatCompletionResponse>(200, "application/json")
            .Produces<OpenAIErrorResponse>(400)
            .Produces<OpenAIErrorResponse>(404)
            .Produces<OpenAIErrorResponse>(408)
            .Produces<OpenAIErrorResponse>(413)
            .Produces<OpenAIErrorResponse>(500)
            .Produces<OpenAIErrorResponse>(402);

        app.MapPost("/v1/responses", ([FromServices] ResponsesEndpoints endpoints, CreateResponseRequest request, CancellationToken cancellationToken) => endpoints.CreateResponse(request, cancellationToken))
            .RequireAuthorization("VirtualKeyAuthentication")
            .AddEndpointFilter<RequireBalanceEndpointFilter>()
            .AddEndpointFilter<TokenRateLimitFilter>()
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Responses").WithName("Responses_Create")
            .WithDescription("Creates a stateless response using the configured chat provider. Set stream=true for text/event-stream events. Stateful response retrieval and storage are not supported.")
            .WithSummary("Create a stateless model response")
            .Produces<ResponseObject>(200, "application/json")
            .Produces<OpenAIErrorResponse>(400)
            .Produces<OpenAIErrorResponse>(401)
            .Produces<OpenAIErrorResponse>(402)
            .Produces<OpenAIErrorResponse>(403)
            .Produces<OpenAIErrorResponse>(404)
            .Produces<OpenAIErrorResponse>(429)
            .Produces<OpenAIErrorResponse>(500);

        return app;
    }

    private sealed record RequestSizeLimitMetadata(long? MaxRequestBodySize) : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata;
}
