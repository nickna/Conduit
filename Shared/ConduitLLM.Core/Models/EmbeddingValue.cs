using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduitLLM.Core.Models;

/// <summary>An embedding represented as float values or a base64-encoded float32 vector.</summary>
[JsonConverter(typeof(EmbeddingValueConverter))]
public sealed class EmbeddingValue
{
    private EmbeddingValue(IReadOnlyList<float>? floats, string? base64)
    {
        Floats = floats;
        Base64 = base64;
    }

    /// <summary>The numeric vector, or null when the provider returned base64.</summary>
    public IReadOnlyList<float>? Floats { get; }

    /// <summary>The encoded vector, or null when the provider returned float values.</summary>
    public string? Base64 { get; }

    /// <summary>Whether the wire representation is a base64 string.</summary>
    public bool IsBase64 => Base64 is not null;

    /// <summary>Copies finite float values into an immutable numeric embedding.</summary>
    public static EmbeddingValue FromFloats(IEnumerable<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.ToArray();
        if (copy.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("Embedding values must be finite numbers.", nameof(values));
        return new EmbeddingValue(Array.AsReadOnly(copy), null);
    }

    /// <summary>Preserves a base64 string containing a complete float32 vector.</summary>
    public static EmbeddingValue FromBase64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Embedding must contain valid base64-encoded float32 bytes.", nameof(value), exception);
        }
        if (bytes.Length % sizeof(float) != 0)
            throw new ArgumentException("Embedding base64 must contain complete float32 values.", nameof(value));
        return new EmbeddingValue(null, value);
    }

    /// <summary>Creates a numeric embedding from an existing vector.</summary>
    public static implicit operator EmbeddingValue(float[] values) => FromFloats(values);

    /// <summary>Creates a numeric embedding from an existing list.</summary>
    public static implicit operator EmbeddingValue(List<float> values) => FromFloats(values);

    /// <summary>Creates an encoded embedding from its base64 representation.</summary>
    public static implicit operator EmbeddingValue(string value) => FromBase64(value);
}

/// <summary>Reads and writes both embedding wire variants without reflection-based serialization.</summary>
public sealed class EmbeddingValueConverter : JsonConverter<EmbeddingValue>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override EmbeddingValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            try
            {
                return EmbeddingValue.FromBase64(reader.GetString()!);
            }
            catch (ArgumentException exception)
            {
                throw new JsonException("Embedding must contain valid base64-encoded float32 values.", exception);
            }
        }

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Embedding must be an array of numbers or a base64 string.");

        var values = new List<float>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return EmbeddingValue.FromFloats(values);
            if (reader.TokenType != JsonTokenType.Number ||
                !reader.TryGetSingle(out var value) || !float.IsFinite(value))
                throw new JsonException("Embedding arrays must contain finite float32 numbers.");
            values.Add(value);
        }
        throw new JsonException("Embedding array is incomplete.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, EmbeddingValue value, JsonSerializerOptions options)
    {
        if (value is null)
            throw new JsonException("Embedding cannot be null.");
        if (value.Base64 is { } encoded)
        {
            writer.WriteStringValue(encoded);
            return;
        }

        writer.WriteStartArray();
        foreach (var number in value.Floats!) writer.WriteNumberValue(number);
        writer.WriteEndArray();
    }
}
