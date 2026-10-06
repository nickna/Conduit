using System.Text.Json;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Helpers;
using AwesomeAssertions;
using Xunit;

namespace ConduitLLM.Tests.Providers.Helpers;

public class ContentHelperTests
{
    [Fact]
    public void ShouldPreserveAsArray_WithCacheControl_ReturnsTrue()
    {
        // Arrange — content array with cache_control on a text block
        var json = """
        [
            { "type": "text", "text": "System prompt", "cache_control": { "type": "ephemeral" } },
            { "type": "text", "text": "Hello" }
        ]
        """;
        var content = JsonSerializer.Deserialize<JsonElement>(json);

        // Act
        var result = ContentHelper.ShouldPreserveAsArray(content);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveAsArray_PlainText_ReturnsFalse()
    {
        // Arrange — plain string content
        var result = ContentHelper.ShouldPreserveAsArray("Hello world");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldPreserveAsArray_NullContent_ReturnsFalse()
    {
        ContentHelper.ShouldPreserveAsArray(null).Should().BeFalse();
    }

    [Fact]
    public void ShouldPreserveAsArray_TextOnlyArray_ReturnsTrue()
    {
        // Arrange — content array with only type/text, no cache_control
        var json = """
        [
            { "type": "text", "text": "Hello" },
            { "type": "text", "text": "World" }
        ]
        """;
        var content = JsonSerializer.Deserialize<JsonElement>(json);

        // Act
        var result = ContentHelper.ShouldPreserveAsArray(content);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveAsArray_ImageOnly_ReturnsTrue()
    {
        // Arrange — content array with image but no cache_control
        var json = """
        [
            { "type": "text", "text": "Describe this" },
            { "type": "image_url", "image_url": { "url": "https://example.com/img.png" } }
        ]
        """;
        var content = JsonSerializer.Deserialize<JsonElement>(json);

        // Act
        var result = ContentHelper.ShouldPreserveAsArray(content);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveAsArray_ImageWithCacheControl_ReturnsTrue()
    {
        // Arrange — content array with both image and cache_control
        var json = """
        [
            { "type": "text", "text": "System prompt", "cache_control": { "type": "ephemeral" } },
            { "type": "image_url", "image_url": { "url": "https://example.com/img.png" } }
        ]
        """;
        var content = JsonSerializer.Deserialize<JsonElement>(json);

        // Act
        var result = ContentHelper.ShouldPreserveAsArray(content);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveAsArray_JsonString_ReturnsFalse()
    {
        // Arrange — JsonElement of kind String
        var content = JsonSerializer.Deserialize<JsonElement>("\"Hello\"");

        // Act
        var result = ContentHelper.ShouldPreserveAsArray(content);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsTextOnly_WithCacheControl_StillReturnsTrue()
    {
        // Verify that IsTextOnly still returns true for text+cache_control (no images)
        // This is important because ShouldPreserveAsArray takes priority in the mapping
        var json = """
        [
            { "type": "text", "text": "Hello", "cache_control": { "type": "ephemeral" } }
        ]
        """;
        var content = JsonSerializer.Deserialize<JsonElement>(json);

        ContentHelper.IsTextOnly(content).Should().BeTrue();
        ContentHelper.ShouldPreserveAsArray(content).Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveAsArray_ListOfDictsWithCacheControl_ReturnsTrue()
    {
        // Arrange — content produced by PromptCacheInjectionService (string → List<Dictionary>)
        var content = new List<object>
        {
            new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = "System prompt",
                ["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" }
            }
        };

        // Act
        var result = ContentHelper.ShouldPreserveAsArray(content);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveAsArray_ListOfDictsWithoutCacheControl_ReturnsTrue()
    {
        // Arrange — List<Dictionary> without cache_control
        var content = new List<object>
        {
            new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = "Hello"
            }
        };

        // Act
        var result = ContentHelper.ShouldPreserveAsArray(content);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveAsArray_ValueTypeArray_ReturnsTrue()
    {
        ContentHelper.ShouldPreserveAsArray(new[] { 1, 2, 3 }).Should().BeTrue();
    }

    [Fact]
    public void UnknownObjectContentUsesItsStringRepresentation()
    {
        var content = new OpaqueContent();

        ContentHelper.ExtractMultimodalContent(content).Should().Equal("opaque-content");
        ContentHelper.GetContentAsString(content).Should().Be("opaque-content");
        ContentHelper.IsTextOnly(content).Should().BeTrue();
        ContentHelper.ShouldPreserveAsArray(content).Should().BeFalse();
    }

    [Fact]
    public void IsTextOnly_WithVideoUrl_ReturnsFalse()
    {
        var content = JsonSerializer.Deserialize<JsonElement>(
            """[{"type":"video_url","video_url":{"url":"https://example.com/video.mp4"}}]""");

        ContentHelper.IsTextOnly(content).Should().BeFalse();
    }

    [Fact]
    public void ExtractVideoUrls_PreservesTypedProcessingOptions()
    {
        var content = new List<object>
        {
            new VideoUrlContentPart
            {
                VideoUrl = new VideoUrl
                {
                    Url = "https://example.com/video.mp4",
                    Detail = "high",
                    MaxFrames = 24,
                    SampleRate = 2.5
                }
            }
        };

        var result = ContentHelper.ExtractVideoUrls(content);

        result.Should().ContainSingle();
        result[0].Url.Should().Be("https://example.com/video.mp4");
        result[0].Detail.Should().Be("high");
        result[0].MaxFrames.Should().Be(24);
        result[0].SampleRate.Should().Be(2.5);
    }

    [Fact]
    public void ExtractVideoUrls_ParsesJsonContent()
    {
        var content = JsonSerializer.Deserialize<JsonElement>(
            """
            [
              {
                "type": "video_url",
                "video_url": {
                  "url": "https://example.com/video.mp4",
                  "start_time": 1.5,
                  "end_time": 8
                }
              }
            ]
            """);

        var result = ContentHelper.ExtractVideoUrls(content);

        result.Should().ContainSingle();
        result[0].StartTime.Should().Be(1.5);
        result[0].EndTime.Should().Be(8);
    }

    private sealed class OpaqueContent
    {
        public override string ToString() => "opaque-content";
    }

}
