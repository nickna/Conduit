using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models
{
    /// <summary>
    /// Represents a request to generate a video based on a text prompt.
    /// </summary>
    public class VideoGenerationRequest
    {
        /// <summary>
        /// The text prompt that describes what video to generate.
        /// </summary>
        [JsonPropertyName("prompt")]
        public required string Prompt { get; set; }

        /// <summary>
        /// The model to use for video generation (e.g., "minimax-video-01").
        /// </summary>
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        /// <summary>
        /// The duration of the video in seconds. Defaults to 5 seconds.
        /// </summary>
        [JsonPropertyName("duration")]
        public int? Duration { get; set; }

        /// <summary>
        /// The size/resolution of the video (e.g., "1920x1080", "1280x720").
        /// </summary>
        [JsonPropertyName("size")]
        public string? Size { get; set; }

        /// <summary>
        /// Frames per second for the video. Common values: 24, 30, 60.
        /// </summary>
        [JsonPropertyName("fps")]
        public int? Fps { get; set; }

        /// <summary>
        /// The style or aesthetic of the video generation.
        /// </summary>
        [JsonPropertyName("style")]
        public string? Style { get; set; }

        /// <summary>
        /// The format in which the generated video is returned.
        /// Options: "url" (default) or "b64_json" (base64 encoded).
        /// </summary>
        [JsonPropertyName("response_format")]
        public string? ResponseFormat { get; set; }

        /// <summary>
        /// A unique identifier representing your end-user.
        /// </summary>
        [JsonPropertyName("user")]
        public string? User { get; set; }

        /// <summary>
        /// Optional seed for deterministic generation.
        /// </summary>
        [JsonPropertyName("seed")]
        public int? Seed { get; set; }

        /// <summary>
        /// The number of videos to generate. Defaults to 1.
        /// </summary>
        [JsonPropertyName("n")]
        public int N { get; set; } = 1;

        /// <summary>
        /// Optional webhook URL to receive notifications when video generation completes.
        /// </summary>
        [JsonPropertyName("webhook_url")]
        public string? WebhookUrl { get; set; }

        /// <summary>
        /// Optional headers to include in the webhook request.
        /// </summary>
        [JsonPropertyName("webhook_headers")]
        public Dictionary<string, string>? WebhookHeaders { get; set; }

        /// <summary>
        /// Additional model-specific parameters that are passed through to the provider API.
        /// These parameters are not explicitly mapped but are forwarded as-is to support
        /// various open-source video models with different parameter requirements.
        /// Examples include negative_prompt, seed, guidance_scale, num_inference_steps, etc.
        /// </summary>
        /// <remarks>
        /// This property captures any JSON properties not explicitly mapped to other properties.
        /// Common parameters for open-source video models include:
        /// - negative_prompt: Text describing what to avoid in the generation
        /// - seed: Random seed for reproducible results
        /// - guidance_scale: How closely to follow the prompt
        /// - num_inference_steps: Number of denoising steps
        /// - motion_bucket_id: Controls amount of motion (for SVD models)
        /// - decode_chunk_size: Number of frames to decode at once
        /// - aspect_ratio: Alternative to size (e.g., "16:9", "9:16")
        /// </remarks>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    /// <summary>
    /// Represents the response from a video generation request.
    /// </summary>
    public class VideoGenerationResponse
    {
        /// <summary>
        /// Unix timestamp of when the response was created.
        /// </summary>
        [JsonPropertyName("created")]
        public long Created { get; set; }

        /// <summary>
        /// List of generated video data.
        /// </summary>
        [JsonPropertyName("data")]
        public List<VideoData> Data { get; set; } = new();

        /// <summary>
        /// The model used for generation.
        /// </summary>
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        /// <summary>
        /// Usage information if available.
        /// </summary>
        [JsonPropertyName("usage")]
        public VideoGenerationUsage? Usage { get; set; }
    }

    /// <summary>
    /// Represents a single generated video.
    /// </summary>
    public class VideoData : IGeneratedMediaData
    {
        /// <summary>
        /// The URL of the generated video (if response_format is "url").
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>
        /// Base64-encoded video data (if response_format is "b64_json").
        /// </summary>
        [JsonPropertyName("b64_json")]
        public string? B64Json { get; set; }

        /// <summary>
        /// Metadata about the generated video.
        /// </summary>
        [JsonPropertyName("metadata")]
        public VideoMetadata? Metadata { get; set; }

        /// <summary>
        /// Revised prompt if the model modified the original prompt.
        /// </summary>
        [JsonPropertyName("revised_prompt")]
        public string? RevisedPrompt { get; set; }
    }

    /// <summary>
    /// Metadata about a video file.
    /// </summary>
    public class VideoMetadata
    {
        /// <summary>
        /// Width of the video in pixels.
        /// </summary>
        [JsonPropertyName("width")]
        public int Width { get; set; }

        /// <summary>
        /// Height of the video in pixels.
        /// </summary>
        [JsonPropertyName("height")]
        public int Height { get; set; }

        /// <summary>
        /// Duration of the video in seconds.
        /// </summary>
        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        /// <summary>
        /// Frames per second of the video.
        /// </summary>
        [JsonPropertyName("fps")]
        public double Fps { get; set; }

        /// <summary>
        /// Video codec used (e.g., "h264", "vp9").
        /// </summary>
        [JsonPropertyName("codec")]
        public string? Codec { get; set; }

        /// <summary>
        /// Audio codec if audio is present.
        /// </summary>
        [JsonPropertyName("audio_codec")]
        public string? AudioCodec { get; set; }

        /// <summary>
        /// File size in bytes.
        /// </summary>
        [JsonPropertyName("file_size_bytes")]
        public long FileSizeBytes { get; set; }

        /// <summary>
        /// Bitrate in bits per second.
        /// </summary>
        [JsonPropertyName("bitrate")]
        public long? Bitrate { get; set; }

        /// <summary>
        /// MIME type of the video file.
        /// </summary>
        [JsonPropertyName("mime_type")]
        public string? MimeType { get; set; }

        /// <summary>
        /// Container format (e.g., "mp4", "webm").
        /// </summary>
        [JsonPropertyName("format")]
        public string? Format { get; set; }
    }

    /// <summary>
    /// Usage information for video generation.
    /// </summary>
    public class VideoGenerationUsage
    {
        /// <summary>
        /// Number of videos generated.
        /// </summary>
        [JsonPropertyName("videos_generated")]
        public int VideosGenerated { get; set; }

        /// <summary>
        /// Total duration of all generated videos in seconds.
        /// </summary>
        [JsonPropertyName("total_duration_seconds")]
        public double TotalDurationSeconds { get; set; }

        /// <summary>
        /// Estimated cost if available.
        /// </summary>
        [JsonPropertyName("estimated_cost")]
        public decimal? EstimatedCost { get; set; }
    }

    /// <summary>
    /// Represents video content in a multimodal message.
    /// </summary>
    public class VideoUrlContentPart
    {
        /// <summary>
        /// The type of content part. Always "video_url" for video content.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type => "video_url";

        /// <summary>
        /// The video URL information.
        /// </summary>
        [JsonPropertyName("video_url")]
        public required VideoUrl VideoUrl { get; set; }
    }

    /// <summary>
    /// Represents a video URL and its processing options.
    /// </summary>
    public class VideoUrl
    {
        /// <summary>
        /// The URL of the video. Can be a data URL or HTTP URL.
        /// </summary>
        [JsonPropertyName("url")]
        public required string Url { get; set; }

        /// <summary>
        /// Processing detail level. Options: "low", "high", "auto".
        /// </summary>
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        /// <summary>
        /// Maximum number of frames to extract for analysis.
        /// </summary>
        [JsonPropertyName("max_frames")]
        public int? MaxFrames { get; set; }

        /// <summary>
        /// Sample rate in seconds (extract one frame every N seconds).
        /// </summary>
        [JsonPropertyName("sample_rate")]
        public double? SampleRate { get; set; }

        /// <summary>
        /// Start time in seconds for processing a segment of the video.
        /// </summary>
        [JsonPropertyName("start_time")]
        public double? StartTime { get; set; }

        /// <summary>
        /// End time in seconds for processing a segment of the video.
        /// </summary>
        [JsonPropertyName("end_time")]
        public double? EndTime { get; set; }
    }
}
