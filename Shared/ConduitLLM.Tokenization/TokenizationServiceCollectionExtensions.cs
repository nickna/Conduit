using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Core.Extensions;

/// <summary>Registers the optional tokenizer implementation.</summary>
public static class TokenizationServiceCollectionExtensions
{
    public static IServiceCollection AddConduitTokenization(this IServiceCollection services)
    {
        services.AddScoped<ITokenCounter, TiktokenCounter>();
        return services;
    }
}
