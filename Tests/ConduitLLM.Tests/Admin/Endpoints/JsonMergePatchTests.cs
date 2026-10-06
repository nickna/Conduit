using System.Text.Json;
using System.Text;

using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Serialization;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.Admin.Endpoints;

public sealed class JsonMergePatchTests
{
    private static JsonSerializerOptions Options()
        => AdminJsonOptions.Create();

    private static T Parse<T>(string json) where T : class
    {
        using var document = JsonDocument.Parse(json);
        return JsonMergePatchState.Parse<T>(document.RootElement, Options());
    }

    [Fact]
    public void Parser_PreservesAbsenceClearsNullAndMergesNestedObjects()
    {
        var request = Parse<UpdateProviderRequest>(
            """
            {
              "baseUrl": null,
              "settings": {
                "removed": null,
                "added": "new"
              }
            }
            """);

        request.IsDefined(nameof(request.ProviderName)).Should().BeFalse();
        request.IsDefined(nameof(request.BaseUrl)).Should().BeTrue();

        request.TryGetPatchedProperty(
                nameof(request.BaseUrl),
                "https://old.example",
                out string? baseUrl)
            .Should().BeTrue();
        baseUrl.Should().BeNull();

        request.TryGetPatchedProperty(
                nameof(request.Settings),
                new Dictionary<string, string>
                {
                    ["kept"] = "original",
                    ["removed"] = "old"
                },
                out Dictionary<string, string>? settings)
            .Should().BeTrue();
        settings.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["kept"] = "original",
            ["added"] = "new"
        });
    }

    [Fact]
    public void Parser_RejectsUnknownProperties()
    {
        var act = () => Parse<UpdateProviderRequest>(
            """{"notAProviderProperty":true}""");

        act.Should().Throw<JsonException>()
            .WithMessage("*notAProviderProperty*not writable*");
    }

    [Fact]
    public void Parser_RejectsNullForNonNullableRequestProperties()
    {
        var act = () => Parse<UpdateProviderRequest>(
            """{"isEnabled":null}""");

        act.Should().Throw<JsonException>()
            .WithMessage("*isEnabled*");
    }

    [Fact]
    public async Task Binder_UsesMergePatchParsingAndRequiresItsMediaType()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(Microsoft.Extensions.Options.Options.Create(
                    new Microsoft.AspNetCore.Http.Json.JsonOptions()))
                .BuildServiceProvider()
        };
        context.Request.ContentType = "application/merge-patch+json; charset=utf-8";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"settings":{"added":"value"}}"""));

        var patch = await JsonMergePatch<UpdateProviderRequest>.BindAsync(context, null!);

        patch.Value.TryGetPatchedProperty(
                nameof(UpdateProviderRequest.Settings),
                new Dictionary<string, string> { ["kept"] = "value" },
                out Dictionary<string, string>? settings)
            .Should().BeTrue();
        settings.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["kept"] = "value",
            ["added"] = "value"
        });

        context.Request.ContentType = "application/json";
        var act = async () => await JsonMergePatch<UpdateProviderRequest>.BindAsync(context, null!);
        (await act.Should().ThrowAsync<BadHttpRequestException>())
            .Which.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
    }
}
