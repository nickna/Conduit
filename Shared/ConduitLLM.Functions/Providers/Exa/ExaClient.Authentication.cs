namespace ConduitLLM.Functions.Providers.Exa;

using ConduitLLM.Functions.Providers.Exa.Models;
using ConduitLLM.Functions.Serialization;

public partial class ExaClient
{
    /// <inheritdoc />
    public Task<Interfaces.FunctionAuthenticationResult> VerifyAuthenticationAsync(
        string? apiKey = null,
        CancellationToken cancellationToken = default) =>
        VerifyViaProbeAsync(
            "/search",
            new ExaSearchRequest
            {
                Query = "test",
                NumResults = 1,
                Type = "keyword"
            },
            FunctionProviderJsonContext.Default.ExaSearchRequest,
            apiKey,
            cancellationToken);
}
