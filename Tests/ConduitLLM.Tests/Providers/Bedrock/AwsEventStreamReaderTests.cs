using System.Buffers.Binary;
using System.Text;

using ConduitLLM.Providers.Streaming;

using AwesomeAssertions;

using Xunit;

namespace ConduitLLM.Tests.Providers.Bedrock;

/// <summary>
/// Tests for the AWS event-stream (<c>application/vnd.amazon.eventstream</c>) frame decoder used by
/// Bedrock's ConverseStream.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "Providers")]
public class AwsEventStreamReaderTests
{
    [Fact]
    public void Crc32_Matches_The_Published_Check_Value()
    {
        // CRC-32/IEEE check value: crc32("123456789") == 0xCBF43926.
        AwsEventStreamReader.Crc32.Compute("123456789"u8.ToArray())
            .Should().Be(0xCBF43926u);
    }

    [Fact]
    public async Task ReadMessagesAsync_Decodes_A_Frame_With_Headers_And_Payload()
    {
        var frame = BuildFrame(
            new Dictionary<string, string>
            {
                [":message-type"] = "event",
                [":event-type"] = "contentBlockDelta"
            },
            """{"contentBlockIndex":0,"delta":{"text":"Hello"}}""");

        var messages = await ReadAllAsync(new MemoryStream(frame));

        var message = messages.Should().ContainSingle().Subject;
        message.MessageType.Should().Be("event");
        message.EventType.Should().Be("contentBlockDelta");
        message.PayloadText.Should().Contain("Hello");
    }

    [Fact]
    public async Task ReadMessagesAsync_Decodes_Consecutive_Frames_In_Order()
    {
        var first = BuildFrame(new() { [":event-type"] = "messageStart" }, """{"role":"assistant"}""");
        var second = BuildFrame(new() { [":event-type"] = "messageStop" }, """{"stopReason":"end_turn"}""");
        var stream = new MemoryStream(first.Concat(second).ToArray());

        var messages = await ReadAllAsync(stream);

        messages.Select(m => m.EventType).Should().Equal("messageStart", "messageStop");
    }

    [Fact]
    public async Task ReadMessagesAsync_Rejects_A_Corrupted_Message_Crc()
    {
        var frame = BuildFrame(new() { [":event-type"] = "messageStart" }, "{}");
        frame[^1] ^= 0xFF;

        var act = () => ReadAllAsync(new MemoryStream(frame));

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*CRC*");
    }

    [Fact]
    public async Task ReadMessagesAsync_Rejects_A_Corrupted_Prelude()
    {
        var frame = BuildFrame(new() { [":event-type"] = "messageStart" }, "{}");
        frame[9] ^= 0xFF; // inside the prelude CRC

        var act = () => ReadAllAsync(new MemoryStream(frame));

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadMessagesAsync_Rejects_A_Truncated_Frame()
    {
        var frame = BuildFrame(new() { [":event-type"] = "messageStart" }, """{"role":"assistant"}""");

        var act = () => ReadAllAsync(new MemoryStream(frame[..^6]));

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*mid-frame*");
    }

    [Fact]
    public async Task ReadMessagesAsync_Returns_Nothing_For_An_Empty_Stream()
    {
        var messages = await ReadAllAsync(new MemoryStream());

        messages.Should().BeEmpty();
    }

    private static async Task<List<AwsEventStreamMessage>> ReadAllAsync(Stream stream)
    {
        var messages = new List<AwsEventStreamMessage>();
        await foreach (var message in AwsEventStreamReader.ReadMessagesAsync(stream))
        {
            messages.Add(message);
        }
        return messages;
    }

    /// <summary>
    /// Builds a spec-conformant event-stream frame: 12-byte prelude (total length, headers length,
    /// prelude CRC), string-typed headers, payload, and the trailing message CRC.
    /// </summary>
    internal static byte[] BuildFrame(Dictionary<string, string> headers, string payload)
    {
        using var headerStream = new MemoryStream();
        Span<byte> length = stackalloc byte[2];
        foreach (var (name, value) in headers)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            headerStream.WriteByte((byte)nameBytes.Length);
            headerStream.Write(nameBytes);
            headerStream.WriteByte(7); // value type: string
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)valueBytes.Length);
            headerStream.Write(length);
            headerStream.Write(valueBytes);
        }

        var headerBytes = headerStream.ToArray();
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var totalLength = 12 + headerBytes.Length + payloadBytes.Length + 4;

        var frame = new byte[totalLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), (uint)headerBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(8, 4), AwsEventStreamReader.Crc32.Compute(frame.AsSpan(0, 8)));
        headerBytes.CopyTo(frame.AsSpan(12));
        payloadBytes.CopyTo(frame.AsSpan(12 + headerBytes.Length));
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(totalLength - 4, 4),
            AwsEventStreamReader.Crc32.Compute(frame.AsSpan(0, totalLength - 4)));

        return frame;
    }
}
