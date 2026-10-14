using System.Text;

/// <summary>Bounded HTTP/1 request framing for the loopback native provider fixture.</summary>
internal static class NativeProviderHttpRequestReader
{
    internal const int MaxHeaderBytes = 64 * 1024;
    internal const int MaxBodyBytes = 1024 * 1024;
    internal const int MaxLineBytes = 8 * 1024;
    internal const int MaxFramingBytes = 64 * 1024;

    internal static async Task<NativeProviderHttpRequest?> ReadAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new LineReader(stream, cancellationToken);
        var headerBudget = new ReadBudget(MaxHeaderBytes);
        var requestLine = await reader.ReadLineAsync(headerBudget, allowCleanEof: true);
        if (requestLine is null)
            return null;

        var requestParts = requestLine.Split(' ', 3);
        if (requestParts.Length != 3 || !IsToken(requestParts[0]) ||
            requestParts[1].Length == 0 || requestParts[1].Any(IsWhitespaceOrControl) ||
            requestParts[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            throw new InvalidDataException("Native provider request line is invalid.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync(headerBudget)
                ?? throw new EndOfStreamException("Native provider request headers are incomplete.");
            if (line.Length == 0)
                break;

            var (name, value) = ParseField(line);
            if (headers.ContainsKey(name) &&
                (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Native provider request has duplicate framing headers.");
            headers[name] = value;
        }

        var hasLength = headers.TryGetValue("Content-Length", out var lengthValue);
        var hasEncoding = headers.TryGetValue("Transfer-Encoding", out var encoding);
        if (hasLength && hasEncoding)
            throw new InvalidDataException("Native provider request has conflicting framing headers.");

        byte[] body;
        if (hasEncoding)
        {
            if (requestParts[2] != "HTTP/1.1" ||
                !string.Equals(encoding, "chunked", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native provider only supports the chunked transfer coding.");
            body = await ReadChunkedBodyAsync(stream, reader, cancellationToken);
        }
        else
        {
            var contentLength = hasLength ? ParseContentLength(lengthValue!) : 0;
            body = contentLength == 0 ? Array.Empty<byte>() : new byte[contentLength];
            await stream.ReadExactlyAsync(body.AsMemory(), cancellationToken);
        }

        return new NativeProviderHttpRequest(
            requestParts[0], requestParts[1].Split('?', 2)[0], headers, Encoding.UTF8.GetString(body));
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        Stream stream, LineReader reader, CancellationToken cancellationToken)
    {
        var framingBudget = new ReadBudget(MaxFramingBytes);
        using var body = new MemoryStream();
        var buffer = new byte[MaxLineBytes];
        while (true)
        {
            var line = await reader.ReadLineAsync(framingBudget)
                ?? throw new EndOfStreamException("Native provider chunk framing is incomplete.");
            var length = ParseChunkLength(line);
            if (length == 0)
            {
                while (true)
                {
                    var trailer = await reader.ReadLineAsync(framingBudget)
                        ?? throw new EndOfStreamException("Native provider chunk trailers are incomplete.");
                    if (trailer.Length == 0)
                        return body.ToArray();
                    // Trailers never replace routing, authentication or framing headers.
                    _ = ParseField(trailer);
                }
            }

            if (length > MaxBodyBytes - body.Length)
                throw new InvalidDataException("Native provider request body exceeded 1 MiB.");

            var remaining = length;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                body.Write(buffer, 0, count);
                remaining -= count;
            }
            await reader.ReadCrlfAsync(framingBudget);
        }
    }

    private static int ParseContentLength(string value)
    {
        if (value.Length == 0)
            throw new InvalidDataException("Native provider Content-Length is invalid.");

        var length = 0;
        foreach (var digit in value)
        {
            if (digit is < '0' or > '9')
                throw new InvalidDataException("Native provider Content-Length is invalid.");
            var number = digit - '0';
            if (length > (MaxBodyBytes - number) / 10)
                throw new InvalidDataException("Native provider request body exceeded 1 MiB.");
            length = length * 10 + number;
        }
        return length;
    }

    private static int ParseChunkLength(string line)
    {
        var separator = line.IndexOf(';');
        var value = separator >= 0 ? line[..separator].TrimEnd(' ', '\t') : line;
        if (value.Length == 0)
            throw new InvalidDataException("Native provider chunk size is invalid.");

        var length = 0;
        foreach (var digit in value)
        {
            var number = digit switch
            {
                >= '0' and <= '9' => digit - '0',
                >= 'a' and <= 'f' => digit - 'a' + 10,
                >= 'A' and <= 'F' => digit - 'A' + 10,
                _ => -1
            };
            if (number < 0)
                throw new InvalidDataException("Native provider chunk size is invalid.");
            if (length > (MaxBodyBytes - number) / 16)
                throw new InvalidDataException("Native provider request body exceeded 1 MiB.");
            length = length * 16 + number;
        }
        if (separator >= 0)
            ValidateChunkExtensions(line, separator);
        return length;
    }

    private static void ValidateChunkExtensions(string line, int index)
    {
        while (index < line.Length)
        {
            SkipWhitespace(line, ref index);
            if (index == line.Length || line[index++] != ';')
                throw new InvalidDataException("Native provider chunk extension is invalid.");
            SkipWhitespace(line, ref index);
            ReadToken(line, ref index);
            SkipWhitespace(line, ref index);
            if (index < line.Length && line[index] == '=')
            {
                index++;
                SkipWhitespace(line, ref index);
                if (index < line.Length && line[index] == '"')
                    ReadQuotedString(line, ref index);
                else
                    ReadToken(line, ref index);
                SkipWhitespace(line, ref index);
            }
        }
    }

    private static void ReadToken(string line, ref int index)
    {
        var start = index;
        while (index < line.Length && IsTokenCharacter(line[index]))
            index++;
        if (index == start)
            throw new InvalidDataException("Native provider chunk extension token is invalid.");
    }

    private static void ReadQuotedString(string line, ref int index)
    {
        index++;
        while (index < line.Length)
        {
            var value = line[index++];
            if (value == '"')
                return;
            if (value == '\\')
            {
                if (index == line.Length)
                    break;
                value = line[index++];
            }
            if ((value < ' ' && value != '\t') || value == '\u007f')
                throw new InvalidDataException("Native provider quoted chunk extension is invalid.");
        }
        throw new InvalidDataException("Native provider quoted chunk extension is incomplete.");
    }

    private static void SkipWhitespace(string line, ref int index)
    {
        while (index < line.Length && line[index] is ' ' or '\t')
            index++;
    }

    private static (string Name, string Value) ParseField(string line)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0 || !IsToken(line[..separator]))
            throw new InvalidDataException("Native provider header field is invalid.");
        var value = line[(separator + 1)..];
        if (value.Any(character => (character < ' ' && character != '\t') || character == '\u007f'))
            throw new InvalidDataException("Native provider header field value is invalid.");
        return (line[..separator], value.Trim(' ', '\t'));
    }

    private static bool IsToken(string value) => value.Length != 0 && value.All(IsTokenCharacter);

    private static bool IsTokenCharacter(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static bool IsWhitespaceOrControl(char value) => value <= ' ' || value == '\u007f';

    private sealed class ReadBudget(int remaining)
    {
        internal void Consume(int count)
        {
            remaining -= count;
            if (remaining < 0)
                throw new InvalidDataException("Native provider HTTP framing exceeded its byte limit.");
        }
    }

    private sealed class LineReader(Stream stream, CancellationToken cancellationToken)
    {
        private readonly byte[] _singleByte = new byte[1];
        private readonly byte[] _line = new byte[MaxLineBytes];

        internal async Task<string?> ReadLineAsync(ReadBudget budget, bool allowCleanEof = false)
        {
            var length = 0;
            while (true)
            {
                var value = await ReadByteAsync();
                if (value < 0)
                {
                    if (allowCleanEof && length == 0)
                        return null;
                    throw new EndOfStreamException("Native provider HTTP framing is incomplete.");
                }
                budget.Consume(1);
                if (value == '\r')
                {
                    await ExpectLfAsync(budget);
                    return Encoding.Latin1.GetString(_line, 0, length);
                }
                if (value == '\n')
                    throw new InvalidDataException("Native provider HTTP framing requires CRLF.");
                if (length == _line.Length)
                    throw new InvalidDataException("Native provider HTTP line exceeded 8 KiB.");
                _line[length++] = (byte)value;
            }
        }

        internal async Task ReadCrlfAsync(ReadBudget budget)
        {
            var value = await ReadByteAsync();
            if (value < 0)
                throw new EndOfStreamException("Native provider chunk data is incomplete.");
            budget.Consume(1);
            if (value != '\r')
                throw new InvalidDataException("Native provider chunk data requires CRLF.");
            await ExpectLfAsync(budget);
        }

        private async Task ExpectLfAsync(ReadBudget budget)
        {
            var value = await ReadByteAsync();
            if (value < 0)
                throw new EndOfStreamException("Native provider HTTP framing is incomplete.");
            budget.Consume(1);
            if (value != '\n')
                throw new InvalidDataException("Native provider HTTP framing requires CRLF.");
        }

        private async Task<int> ReadByteAsync() =>
            await stream.ReadAsync(_singleByte.AsMemory(), cancellationToken) == 0 ? -1 : _singleByte[0];
    }
}

internal sealed record NativeProviderHttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body);
