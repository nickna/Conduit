using ConduitLLM.Core.Utilities;
using ConduitLLM.Security;
using ConduitLLM.Security.Options;

namespace ConduitLLM.Admin.Security;

/// <summary>Uses the same credential and header precedence throughout the Admin pipeline.</summary>
internal static class MasterKeyCredentialReader
{
    internal static IReadOnlyList<string> GetKeyHeaders(ApiAuthOptions options) =>
        new[] { options.ApiKeyHeader }
            .Concat(options.AlternativeHeaders)
            .Where(header => !string.IsNullOrWhiteSpace(header))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static string? Read(HttpRequest request, IEnumerable<string> keyHeaders)
    {
        foreach (var header in keyHeaders)
        {
            var key = request.Headers[header].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(key))
                return key;
        }

        return SpanHelper.ExtractBearerToken(request.Headers[SecurityHeaderNames.Authorization].FirstOrDefault() ?? "");
    }
}
