using System.Text.Json.Nodes;

using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Helpers;

using AwesomeAssertions;

namespace ConduitLLM.Tests.Providers.Helpers;

public sealed class PromptCacheMarkerInjectorTests
{
    [Fact]
    public void ResolveTargets_HandlesRolesNegativeIndexesAndDuplicates()
    {
        Message[] messages =
        [
            new() { Role = "system", Content = "one" },
            new() { Role = "user", Content = "two" },
            new() { Role = "system", Content = "three" }
        ];
        CacheInjectionPoint[] points =
        [
            new() { Role = "system", Index = -1 },
            new() { Role = "system", Index = 1 },
            new() { Role = "user" }
        ];

        PromptCacheMarkerInjector.ResolveTargets(messages, points).Should().Equal(2, 1);
    }

    [Fact]
    public void TryAddMarker_RejectsEmptyTextBlock()
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "   " });

        var added = PromptCacheMarkerInjector.TryAddMarker(
            content,
            "prompt_cache_breakpoint",
            new Dictionary<string, object?> { ["mode"] = "explicit" },
            out var updated);

        added.Should().BeFalse();
        updated.Should().BeSameAs(content);
    }

    [Fact]
    public void TryAddMarker_AddsAndCountsProviderSpecificMarker()
    {
        var added = PromptCacheMarkerInjector.TryAddMarker(
            "stable prefix",
            "cache_control",
            new Dictionary<string, object?> { ["type"] = "ephemeral" },
            out var updated);

        added.Should().BeTrue();
        PromptCacheMarkerInjector.CountMarkers(updated, "cache_control").Should().Be(1);
        PromptCacheMarkerInjector.CountMarkers(updated, "prompt_cache_breakpoint").Should().Be(0);
    }
}
