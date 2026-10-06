using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Serialization;
using CoreModels = ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.OpenAICompatible
{
    /// <summary>
    /// OpenAICompatibleClient partial adding OpenAI-compatible audio: speech-to-text
    /// (<c>/audio/transcriptions</c>, multipart) and text-to-speech (<c>/audio/speech</c>, raw bytes).
    /// Every OpenAI-compatible provider (including OpenRouter by inheritance) gains these; whether a
    /// given model actually serves them is gated at the controller by the DB capability flags.
    /// </summary>
    public abstract partial class OpenAICompatibleClient : IAudioTranscriptionClient, ITextToSpeechClient
    {
        /// <inheritdoc />
        public async Task<AudioTranscriptionResponse> TranscribeAudioAsync(
            AudioTranscriptionRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);

                using var form = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(request.AudioData);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType ?? "application/octet-stream");
                form.Add(fileContent, "file", request.FileName);
                form.Add(new StringContent(string.IsNullOrEmpty(request.Model) ? ProviderModelId : request.Model), "model");
                if (!string.IsNullOrEmpty(request.Language))
                    form.Add(new StringContent(request.Language), "language");
                if (!string.IsNullOrEmpty(request.Prompt))
                    form.Add(new StringContent(request.Prompt), "prompt");
                if (request.Temperature.HasValue)
                    form.Add(new StringContent(request.Temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");
                form.Add(new StringContent(request.ResponseFormat ?? "json"), "response_format");
                if (request.ChunkingStrategy.HasValue)
                    form.Add(new StringContent(request.ChunkingStrategy.Value.GetRawText()), "chunking_strategy");
                AddFormValues(form, "include[]", request.Include);
                AddFormValues(form, "known_speaker_names[]", request.KnownSpeakerNames);
                if (request.KnownSpeakerReferences != null)
                {
                    foreach (var reference in request.KnownSpeakerReferences)
                    {
                        var referenceContent = new ByteArrayContent(reference.AudioData);
                        referenceContent.Headers.ContentType = new MediaTypeHeaderValue(
                            reference.ContentType ?? "application/octet-stream");
                        form.Add(referenceContent, "known_speaker_references[]", reference.FileName);
                    }
                }
                if (request.Stream.HasValue)
                    form.Add(new StringContent(request.Stream.Value ? "true" : "false"), "stream");
                AddFormValues(form, "timestamp_granularities[]", request.TimestampGranularities);
                if (request.ExtensionData != null)
                {
                    foreach (var kvp in request.ExtensionData)
                        form.Add(new StringContent(kvp.Value.ToString()), kvp.Key);
                }

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, GetAudioTranscriptionEndpoint())
                {
                    Content = form
                };
                foreach (var header in CreateStandardHeaders(apiKey))
                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

                using var httpResponse = await client.SendAsync(httpRequest, cancellationToken);
                var responseText = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    throw new LLMCommunicationException(
                        $"{ProviderName} transcription failed ({(int)httpResponse.StatusCode}): {responseText}");
                }

                AudioTranscriptionResponse result;
                if ((request.ResponseFormat ?? "json").Equals("text", StringComparison.OrdinalIgnoreCase))
                {
                    result = new AudioTranscriptionResponse { Text = responseText };
                }
                else
                {
                    result = JsonSerializer.Deserialize(
                        responseText,
                        CoreHttpJsonContext.Default.AudioTranscriptionResponse)
                        ?? new AudioTranscriptionResponse { Text = string.Empty };
                }

                // Capture provider-reported cost (and strip it) for authoritative billing.
                ExtractProviderUsageFromExtensionData(result.Usage);
                if (result.Usage != null && result.DurationSeconds > 0)
                    result.Usage.AudioDurationSeconds ??= result.DurationSeconds;

                CaptureGenerationId(httpResponse, result);
                return result;
            }, "CreateTranscription", cancellationToken);
        }

        /// <inheritdoc />
        public async Task<TextToSpeechResponse> CreateSpeechAsync(
            TextToSpeechRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteApiRequestAsync(async () =>
            {
                using var client = CreateHttpClient(apiKey);

                var body = new Dictionary<string, object?>
                {
                    ["model"] = string.IsNullOrEmpty(request.Model) ? ProviderModelId : request.Model,
                    ["input"] = request.Input,
                    ["voice"] = request.Voice,
                    ["response_format"] = request.ResponseFormat ?? "mp3"
                };
                if (request.Speed.HasValue)
                    body["speed"] = request.Speed.Value;
                if (!string.IsNullOrWhiteSpace(request.Instructions))
                    body["instructions"] = request.Instructions;
                if (!string.IsNullOrWhiteSpace(request.StreamFormat))
                    body["stream_format"] = request.StreamFormat;
                if (request.ExtensionData != null)
                {
                    foreach (var kvp in request.ExtensionData)
                        if (!body.ContainsKey(kvp.Key))
                            body[kvp.Key] = kvp.Value;
                }

                var json = JsonSerializer.Serialize(
                    body,
                    CoreHttpJsonContext.Default.DictionaryStringObject);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, GetTextToSpeechEndpoint())
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                foreach (var header in CreateStandardHeaders(apiKey))
                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

                using var httpResponse = await client.SendAsync(httpRequest, cancellationToken);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    var err = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                    throw new LLMCommunicationException(
                        $"{ProviderName} speech synthesis failed ({(int)httpResponse.StatusCode}): {err}");
                }

                var audioBytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = httpResponse.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

                // Characters are the billable unit for TTS (the byte response carries no usage JSON).
                var usage = new CoreModels.Usage { TtsCharacters = request.Input?.Length ?? 0 };
                if (httpResponse.Headers.TryGetValues("X-Generation-Id", out var genIds))
                    (usage.Metadata ??= new Dictionary<string, object>())["generation_id"] = genIds.FirstOrDefault() ?? string.Empty;

                return new TextToSpeechResponse
                {
                    AudioData = audioBytes,
                    ContentType = contentType,
                    Model = request.Model,
                    Usage = usage
                };
            }, "CreateSpeech", cancellationToken);
        }

        /// <summary>
        /// Captures the provider's X-Generation-Id header into usage metadata so the provider-cost
        /// path can later resolve the authoritative cost via the provider's /generation endpoint.
        /// </summary>
        private static void CaptureGenerationId(HttpResponseMessage httpResponse, AudioTranscriptionResponse result)
        {
            if (httpResponse.Headers.TryGetValues("X-Generation-Id", out var genIds))
            {
                result.Usage ??= new CoreModels.Usage();
                (result.Usage.Metadata ??= new Dictionary<string, object>())["generation_id"] = genIds.FirstOrDefault() ?? string.Empty;
            }
        }

        private static void AddFormValues(
            MultipartFormDataContent form,
            string name,
            IEnumerable<string>? values)
        {
            if (values == null)
                return;
            foreach (var value in values)
                form.Add(new StringContent(value), name);
        }
    }
}
