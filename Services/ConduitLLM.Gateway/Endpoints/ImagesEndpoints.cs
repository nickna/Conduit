using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Services.Strategies;
using ConduitLLM.Configuration.Interfaces;

namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// Handles image generation requests following OpenAI's API format.
    /// </summary>
    public partial class ImagesEndpoints : GatewayEndpointHandlerBase
    {
        private readonly ILLMClientFactory _clientFactory;
        private readonly IMediaStorageService _storageService;
        private readonly ILogger<ImagesEndpoints> _logger;
        private readonly IModelProviderMappingService _modelMappingService;
        private readonly IAsyncTaskService _taskService;
        private readonly IMediaTaskSubmission _mediaTaskSubmission;
        private readonly ConduitLLM.Core.Interfaces.IVirtualKeyService _virtualKeyService;
        private readonly IMediaLifecycleService _mediaLifecycleService;
        private readonly IProviderErrorTrackingService _errorTrackingService;
        private readonly Base64MediaProcessor _base64MediaProcessor;
        private readonly UrlMediaProcessor _urlMediaProcessor;

        public ImagesEndpoints(
            ILLMClientFactory clientFactory,
            IMediaStorageService storageService,
            ILogger<ImagesEndpoints> logger,
            IModelProviderMappingService modelMappingService,
            IAsyncTaskService taskService,
            IEventPublisher eventPublisher,
            ConduitLLM.Core.Interfaces.IVirtualKeyService virtualKeyService,
            IMediaLifecycleService mediaLifecycleService,
            IProviderErrorTrackingService errorTrackingService,
            Base64MediaProcessor base64MediaProcessor,
            UrlMediaProcessor urlMediaProcessor,
            IHttpContextAccessor httpContextAccessor,
            IMediaTaskSubmission mediaTaskSubmission)
            : base(eventPublisher, httpContextAccessor, logger)
        {
            _clientFactory = clientFactory;
            _storageService = storageService;
            _logger = logger;
            _modelMappingService = modelMappingService;
            _taskService = taskService;
            _mediaTaskSubmission = mediaTaskSubmission;
            _virtualKeyService = virtualKeyService;
            _mediaLifecycleService = mediaLifecycleService;
            _errorTrackingService = errorTrackingService;
            _base64MediaProcessor = base64MediaProcessor;
            _urlMediaProcessor = urlMediaProcessor;
        }
    }
}
