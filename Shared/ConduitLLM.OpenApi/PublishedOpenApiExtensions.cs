using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;

[assembly: InternalsVisibleTo("ConduitLLM.Tests")]

namespace ConduitLLM.OpenApi;

/// <summary>Preserves the published OpenAPI 3.1.1 contract across writer upgrades.</summary>
public static class PublishedOpenApiExtensions
{
    internal const int MaximumDocumentBytes = 16 * 1024 * 1024;

    /// <summary>Maps framework OpenAPI generation with the published JSON patch version.</summary>
    public static IEndpointConventionBuilder MapPublishedOpenApi(this IEndpointRouteBuilder endpoints, string pattern)
    {
        var endpoint = endpoints.MapOpenApi(pattern);
        endpoint.Add(builder =>
        {
            var next = builder.RequestDelegate ?? throw new InvalidOperationException("OpenAPI endpoint has no request delegate.");
            builder.RequestDelegate = context => WritePublishedResponseAsync(context, next);
        });
        return endpoint;
    }

    /// <summary>Serializes the document without changing the published 3.1.1 version.</summary>
    public static async Task SerializeAsPublishedJsonAsync(this OpenApiDocument document, Stream destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var buffer = new DocumentBuffer();
        await document.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_1, cancellationToken);
        NormalizeVersion(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        buffer.Position = 0;
        await buffer.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    internal static async Task WritePublishedResponseAsync(HttpContext context, RequestDelegate next)
    {
        // Keep MapOpenApi's request-scoped generation and endpoint metadata. This
        // adapter applies only to its documentation response, not API traffic.
        var originalBody = context.Response.Body;
        await using var buffer = new DocumentBuffer();
        System.IO.Pipelines.PipeWriter? documentWriter = null;
        Exception? generationFailure = null;
        try
        {
            context.Response.Body = buffer;
            documentWriter = context.Response.BodyWriter;
            await next(context);
        }
        catch (Exception error)
        {
            generationFailure = error;
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            // Return the temporary pipe's pooled memory without completing the
            // network response. Failed generation must discard pending bytes.
            if (documentWriter is not null) await documentWriter.CompleteAsync(generationFailure);
        }

        if (context.Response.StatusCode == StatusCodes.Status200OK &&
            context.Response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            NormalizeVersion(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        }
        // The replacement is one byte, so a framework Content-Length remains valid.
        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);
    }

    internal static void NormalizeVersion(Span<byte> json)
    {
        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("The OpenAPI document must be a JSON object.");

        var versionCount = 0;
        var patchOffset = -1;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("openapi"u8))
                continue;

            versionCount++;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped)
                throw new InvalidDataException("The OpenAPI version must be an unescaped 3.1.1 or 3.1.2 string.");
            if (reader.ValueTextEquals("3.1.2"u8))
                patchOffset = checked((int)reader.TokenStartIndex) + 5;
            else if (!reader.ValueTextEquals("3.1.1"u8))
                throw new InvalidDataException("Unexpected OpenAPI version; the published contract requires 3.1.1.");
        }

        if (versionCount != 1)
            throw new InvalidDataException("The OpenAPI document must contain exactly one root openapi version.");
        // Validate the complete JSON and duplicate-root guard before changing any byte.
        if (patchOffset >= 0) json[patchOffset] = (byte)'1';
    }

    private sealed class DocumentBuffer : MemoryStream
    {
        public override int Capacity
        {
            get => base.Capacity;
            set => base.Capacity = Math.Min(value, MaximumDocumentBytes);
        }

        private void CheckWrite(int count)
        {
            if (count > MaximumDocumentBytes - Position)
                throw new InvalidDataException("The OpenAPI document exceeds the 16 MiB serialization limit.");
        }

        public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckWrite(1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { CheckWrite(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { CheckWrite(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
        public override void SetLength(long value)
        {
            if (value > MaximumDocumentBytes) throw new InvalidDataException("The OpenAPI document exceeds the 16 MiB serialization limit.");
            base.SetLength(value);
        }
    }
}
