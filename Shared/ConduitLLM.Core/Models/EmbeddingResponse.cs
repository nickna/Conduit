using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models
{
    /// <summary>
    /// Represents the response from an embedding generation request.
    /// </summary>
    public class EmbeddingResponse
    {
        /// <summary>
        /// The type of object returned, typically "list".
        /// </summary>
        [JsonPropertyName("object")]
        public required string Object { get; set; }

        /// <summary>
        /// A list of embedding data objects.
        /// </summary>
        [JsonPropertyName("data")]
        public required List<EmbeddingData> Data { get; set; }

        /// <summary>
        /// The ID of the model used for generating embeddings.
        /// </summary>
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        /// <summary>
        /// Usage statistics for the embedding request.
        /// </summary>
        [JsonPropertyName("usage")]
        public required Usage Usage { get; set; }
    }

    public class EmbeddingData
    {
        /// <summary>
        /// The type of object, typically "embedding".
        /// </summary>
        [JsonPropertyName("object")]
        public required string Object { get; set; }

        /// <summary>
        /// The embedding vector, represented as floating-point numbers or a base64 float32 string.
        /// </summary>
        [JsonPropertyName("embedding")]
        public required EmbeddingValue Embedding { get; set; }

        /// <summary>
        /// The index of the embedding in the list.
        /// </summary>
        [JsonPropertyName("index")]
        public int Index { get; set; }
    }
}
