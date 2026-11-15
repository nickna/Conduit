using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;

using Microsoft.AspNetCore.Http;

namespace ConduitLLM.Core.Middleware;

/// <summary>
/// Resolves the error response mapping once for an exception and translator within a request.
/// This lets inner accounting observe the status that the outer error handler will write.
/// </summary>
public static class RequestExceptionMapping
{
    private static readonly object CacheKey = new();

    /// <summary>
    /// Uses the registered customer-mode translator for wrapped provider failures, or the shared
    /// exception mapper otherwise. Only successful resolutions are cached; a failed translator
    /// leaves the outer error handler's existing exception behavior unchanged.
    /// </summary>
    public static ExceptionToResponseMapper.ExceptionMappingResult Resolve(
        HttpContext context,
        Exception exception,
        IProviderErrorTranslator? providerErrorTranslator = null)
    {
        if (context.Items.TryGetValue(CacheKey, out var value) &&
            value is CachedMapping cached &&
            ReferenceEquals(cached.Exception, exception) &&
            ReferenceEquals(cached.Translator, providerErrorTranslator))
        {
            return cached.Mapping;
        }

        var providerException = FindProviderException(exception, providerErrorTranslator);
        var mapping = providerException is not null
            ? providerErrorTranslator!.MapProviderError(providerException)
            : ExceptionToResponseMapper.Map(exception);

        context.Items[CacheKey] = new CachedMapping(exception, providerErrorTranslator, mapping);
        return mapping;
    }

    /// <summary>
    /// Resolves only the HTTP status when provisional customer-facing translation fails.
    /// The translator contract preserves this status; its message and detail are still resolved
    /// by the outer error handler. No fallback envelope is cached.
    /// </summary>
    public static int GetStatusCodeWithoutTranslation(
        Exception exception,
        IProviderErrorTranslator? providerErrorTranslator = null)
    {
        var providerException = FindProviderException(exception, providerErrorTranslator);
        return providerException is not null
            ? ExceptionToResponseMapper.MapProviderCommunicationStatus(
                providerException.StatusCode, providerException.Message).StatusCode
            : ExceptionToResponseMapper.Map(exception).StatusCode;
    }

    private static LLMCommunicationException? FindProviderException(
        Exception exception, IProviderErrorTranslator? providerErrorTranslator) =>
        providerErrorTranslator is not null ? LLMCommunicationException.FindWithStatus(exception) : null;

    private sealed record CachedMapping(
        Exception Exception,
        IProviderErrorTranslator? Translator,
        ExceptionToResponseMapper.ExceptionMappingResult Mapping);
}
