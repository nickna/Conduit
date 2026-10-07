#if CONDUIT_NATIVE_AOT
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Selects fixed-shape persistence implementations for extracted native Gateway slices.
/// </summary>
public static class NativeRuntimePersistenceExtensions
{
    /// <summary>
    /// Replaces EF reference repositories only after the normal registration graph has
    /// been assembled. Unextracted contracts intentionally remain on EF and observable
    /// through the native feature matrix.
    /// </summary>
    public static IServiceCollection UseNativeRuntimePersistence(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IGlobalSettingRepository, NpgsqlGlobalSettingRepository>());
        services.Replace(ServiceDescriptor.Singleton<IIpFilterRepository, NpgsqlIpFilterRepository>());
        services.Replace(ServiceDescriptor.Singleton<IProviderRepository, NpgsqlProviderRepository>());
        services.Replace(ServiceDescriptor.Singleton<IProviderKeyCredentialRepository, NpgsqlProviderKeyCredentialRepository>());
        services.Replace(ServiceDescriptor.Singleton<IVirtualKeyRuntimeStore, NpgsqlVirtualKeyRuntimeStore>());
        services.Replace(ServiceDescriptor.Singleton<IGatewayMetricsStore, NpgsqlGatewayMetricsStore>());
        services.Replace(ServiceDescriptor.Singleton<IAsyncTaskRuntimeStore, NpgsqlAsyncTaskRuntimeStore>());
        services.Replace(ServiceDescriptor.Singleton<IMediaRuntimeStore, NpgsqlMediaRuntimeStore>());
        services.Replace(ServiceDescriptor.Singleton<IModelProviderMappingRuntimeStore, NpgsqlModelProviderMappingRuntimeStore>());
        services.Replace(ServiceDescriptor.Singleton<IModelProviderMappingRepository, StoreBackedModelProviderMappingRepository>());
        services.Replace(ServiceDescriptor.Singleton<IRequestLogRuntimeStore, NpgsqlRequestLogRuntimeStore>());
        services.Replace(ServiceDescriptor.Singleton<IRequestLogRuntimeWriter, StoreBackedRequestLogRuntimeWriter>());
        return services;
    }
}
#endif
