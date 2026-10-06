using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.UsageTracking;


namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// Handles audio transcription (speech-to-text) and speech synthesis (text-to-speech)
    /// following OpenAI's API format.
    /// </summary>
    public class AudioEndpoints : GatewayEndpointHandlerBase
    {
        private readonly ILLMClientFactory _clientFactory;
        private readonly IModelProviderMappingService _modelMappingService;
        private readonly ILogger<AudioEndpoints> _logger;

        public AudioEndpoints(
            ILLMClientFactory clientFactory,
            IModelProviderMappingService modelMappingService,
            ILogger<AudioEndpoints> logger,
            IEventPublisher eventPublisher,
            IHttpContextAccessor httpContextAccessor)
            : base(eventPublisher, httpContextAccessor, logger)
        {
            _clientFactory = clientFactory;
            _modelMappingService = modelMappingService;
            _logger = logger;
        }

        /// <summary>
        /// Transcribes uploaded audio to text (OpenAI <c>/audio/transcriptions</c> compatible).
        /// </summary>
        public async Task<IResult> CreateTranscription(
            IFormFile file,
            string model,
            string? language = null,
            string? prompt = null,
            string? responseFormat = null,
            double? temperature = null,
            string? chunkingStrategy = null,
            bool? stream = null,
            CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length == 0)
                return OpenAIError(400, "An audio file is required.", "invalid_request");
            if (string.IsNullOrEmpty(model))
                return OpenAIError(400, "A model is required.", "invalid_request");

            var mapping = await _modelMappingService.GetMappingByModelAliasAsync(model);
            var supported = mapping?.ModelProviderTypeAssociation?.Model?.SupportsSpeechToText ?? false;
            if (mapping == null || !supported)
                return OpenAIError(400, $"Model {model} does not support speech-to-text transcription.", "unsupported_model");

            StampBillingItems(mapping);

            var client = await _clientFactory.GetClientAsync(model, cancellationToken);
            var stt = client.FindInChain<IAudioTranscriptionClient>();
            if (stt == null)
                return OpenAIError(400, $"Model {model} does not support speech-to-text transcription.", "unsupported_model");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, cancellationToken);

            var request = new AudioTranscriptionRequest
            {
                Model = mapping.ProviderModelId,
                AudioData = ms.ToArray(),
                FileName = file.FileName,
                ContentType = file.ContentType,
                Language = language,
                Prompt = prompt,
                Temperature = temperature,
                ResponseFormat = responseFormat,
                ChunkingStrategy = ParseChunkingStrategy(chunkingStrategy),
                Include = ReadFormValues(HttpContext.Request.Form, "include"),
                KnownSpeakerNames = ReadFormValues(HttpContext.Request.Form, "known_speaker_names"),
                Stream = stream,
                TimestampGranularities = ReadFormValues(
                    HttpContext.Request.Form, "timestamp_granularities")
            };
            var knownSpeakerReferences =
                HttpContext.Request.Form.Files.GetFiles("known_speaker_references");
            if (knownSpeakerReferences.Count > 0)
            {
                request.KnownSpeakerReferences = [];
                foreach (var reference in knownSpeakerReferences)
                {
                    using var referenceStream = new MemoryStream();
                    await reference.CopyToAsync(referenceStream, cancellationToken);
                    request.KnownSpeakerReferences.Add(new AudioTranscriptionReference
                    {
                        AudioData = referenceStream.ToArray(),
                        FileName = reference.FileName,
                        ContentType = reference.ContentType
                    });
                }
            }

            var result = await stt.TranscribeAudioAsync(request, cancellationToken: cancellationToken);
            result.Model = model; // echo the caller's alias, not the provider model id

            // Hand the billable audio duration to the middleware, which never parses the response body.
            HttpContext.SetUsageContext(new AudioUsageContext
            {
                Model = model,
                AudioDurationSeconds = result.DurationSeconds
            });
            var transcriptionUsage = new ConduitLLM.Core.Models.Usage
            {
                AudioDurationSeconds = result.DurationSeconds
            };
            var transcriptionAccounting = HttpContext.GetOrCreateRequestAccountingContext();
            transcriptionAccounting.SetOperation(RequestOperation.Audio, CurrentVirtualKeyId, model);
            transcriptionAccounting.RecordProviderUsage(
                transcriptionUsage,
                model,
                UsageEvidenceSource.Provider);

            if (string.Equals(responseFormat, "text", StringComparison.OrdinalIgnoreCase))
                return Content(result.Text, "text/plain");
            return Ok(result);
        }

        private static System.Text.Json.JsonElement? ParseChunkingStrategy(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(value);
                return document.RootElement.Clone();
            }
            catch (System.Text.Json.JsonException)
            {
                return System.Text.Json.JsonSerializer.SerializeToElement(
                    value,
                    ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.String);
            }
        }

        private static List<string>? ReadFormValues(IFormCollection form, string name)
        {
            var values = form[name].Concat(form[$"{name}[]"])
                .Where(value => value is not null)
                .Select(value => value!)
                .ToList();
            if (values.Count == 1 &&
                values[0] is { } value &&
                value.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize(
                        value,
                        ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.ListString);
                }
                catch (System.Text.Json.JsonException)
                {
                    // Preserve malformed JSON-looking values for provider validation.
                }
            }

            return values.Count == 0 ? null : values!;
        }

        /// <summary>
        /// Synthesizes speech from text (OpenAI <c>/audio/speech</c> compatible). Returns raw audio bytes.
        /// </summary>
        public async Task<IResult> CreateSpeech(
            TextToSpeechRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null || string.IsNullOrEmpty(request.Model))
                return OpenAIError(400, "A model is required.", "invalid_request");
            if (string.IsNullOrEmpty(request.Input))
                return OpenAIError(400, "Input text is required.", "invalid_request");
            if (string.IsNullOrEmpty(request.Voice))
                return OpenAIError(400, "A voice is required.", "invalid_request");

            var alias = request.Model;
            var mapping = await _modelMappingService.GetMappingByModelAliasAsync(alias);
            var supported = mapping?.ModelProviderTypeAssociation?.Model?.SupportsTextToSpeech ?? false;
            if (mapping == null || !supported)
                return OpenAIError(400, $"Model {alias} does not support text-to-speech.", "unsupported_model");

            StampBillingItems(mapping);

            var client = await _clientFactory.GetClientAsync(alias, cancellationToken);
            var tts = client.FindInChain<ITextToSpeechClient>();
            if (tts == null)
                return OpenAIError(400, $"Model {alias} does not support text-to-speech.", "unsupported_model");

            var characterCount = request.Input.Length;
            request.Model = mapping.ProviderModelId; // swap alias -> provider model id before dispatch

            var result = await tts.CreateSpeechAsync(request, cancellationToken: cancellationToken);

            // Hand the billable character count to the middleware, which never parses the binary body.
            HttpContext.SetUsageContext(new AudioUsageContext
            {
                Model = alias,
                TtsCharacters = characterCount
            });
            var speechUsage = new ConduitLLM.Core.Models.Usage
            {
                TtsCharacters = characterCount
            };
            var speechAccounting = HttpContext.GetOrCreateRequestAccountingContext();
            speechAccounting.SetOperation(RequestOperation.Audio, CurrentVirtualKeyId, alias);
            speechAccounting.RecordProviderUsage(
                speechUsage,
                alias,
                UsageEvidenceSource.Estimated);

            return File(result.AudioData, result.ContentType);
        }

        private void StampBillingItems(ConduitLLM.Configuration.Entities.ModelProviderMapping mapping)
        {
            HttpContext.Items["ProviderId"] = mapping.ProviderId;
            HttpContext.Items["ProviderType"] = mapping.Provider?.ProviderType;
            if (mapping.ModelProviderTypeAssociation?.ModelCostId != null)
                HttpContext.Items[HttpContextKeys.ModelCostId] = mapping.ModelProviderTypeAssociation.ModelCostId;
        }
    }
}
