using System.Text.Json.Serialization;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Metrics;
using ConduitLLM.Admin.Models;
using ConduitLLM.Admin.Models.ModelAuthors;
using ConduitLLM.Admin.Models.Models;
using ConduitLLM.Admin.Models.ModelSeries;
using ConduitLLM.Admin.Models.ProviderSync;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.DTOs.Costs;
using ConduitLLM.Configuration.DTOs.IpFilter;
using ConduitLLM.Configuration.DTOs.Monitoring;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.ModelCatalogs;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Serialization;

/// <summary>
/// Source-generated metadata for the concrete response contracts advertised by Admin endpoints.
/// Keeping this separate from request metadata makes OpenAPI endpoint materialization auditable.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AdminFunctionExecutionDto))]
[JsonSerializable(typeof(AdminProblemDetails))]
[JsonSerializable(typeof(AllMetricsDto))]
[JsonSerializable(typeof(AnalyticsCacheInvalidationResponse))]
[JsonSerializable(typeof(AnalyticsCacheMetricsResponse))]
[JsonSerializable(typeof(AnalyticsSummaryDto))]
[JsonSerializable(typeof(BatchSpendingFlushResponse))]
[JsonSerializable(typeof(BatchSpendingInformationResponse))]
[JsonSerializable(typeof(BatchSpendingStatusResponse))]
[JsonSerializable(typeof(BulkDeleteResult))]
[JsonSerializable(typeof(BulkDriftActionResponse))]
[JsonSerializable(typeof(BulkImportResult))]
[JsonSerializable(typeof(BulkModelMappingCreateResponse))]
[JsonSerializable(typeof(BulkModelMappingPreviewResponse))]
[JsonSerializable(typeof(BulkUpdateResult))]
[JsonSerializable(typeof(BundledModelCatalogImportResult))]
[JsonSerializable(typeof(CacheInvalidationPublishedResponse))]
[JsonSerializable(typeof(CacheServiceUnavailableResponse))]
[JsonSerializable(typeof(CacheStats))]
[JsonSerializable(typeof(ClearKeyErrorsResponseDto))]
[JsonSerializable(typeof(global::ConduitLLM.Configuration.DTOs.PagedResult<ProviderDto>))]
[JsonSerializable(typeof(CostDashboardDto))]
[JsonSerializable(typeof(CostTrendDto))]
[JsonSerializable(typeof(CreateVirtualKeyResponseDto))]
[JsonSerializable(typeof(Dictionary<int, int>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(DisableKeyResponseDto))]
[JsonSerializable(typeof(DiscoveryModelsResponse))]
[JsonSerializable(typeof(DriftActionResultDto))]
[JsonSerializable(typeof(DriftItemDto))]
[JsonSerializable(typeof(EphemeralMasterKeyResponse))]
[JsonSerializable(typeof(ErrorStatisticsDto))]
[JsonSerializable(typeof(FunctionConfigurationDto))]
[JsonSerializable(typeof(FunctionCostCacheClearResultDto))]
[JsonSerializable(typeof(FunctionCostDto))]
[JsonSerializable(typeof(FunctionCredentialDto))]
[JsonSerializable(typeof(FunctionCredentialTestResultDto))]
[JsonSerializable(typeof(FunctionExecutionCleanupResultDto))]
[JsonSerializable(typeof(FunctionExecutionDto))]
[JsonSerializable(typeof(GlobalSettingDto))]
[JsonSerializable(typeof(GlobalSettingsReloadAcceptedResponse))]
[JsonSerializable(typeof(HealthHistoryResponse))]
[JsonSerializable(typeof(HealthStatusDto))]
[JsonSerializable(typeof(IEnumerable<GlobalSettingDto>))]
[JsonSerializable(typeof(IEnumerable<IpFilterDto>))]
[JsonSerializable(typeof(IEnumerable<ModelAuthorDto>))]
[JsonSerializable(typeof(IEnumerable<ModelCostDto>))]
[JsonSerializable(typeof(IEnumerable<ModelCostOverviewDto>))]
[JsonSerializable(typeof(IEnumerable<ModelDto>))]
[JsonSerializable(typeof(IEnumerable<ModelIdentifierDto>))]
[JsonSerializable(typeof(IEnumerable<ModelProviderAvailabilityDto>))]
[JsonSerializable(typeof(IEnumerable<ModelProviderMappingDto>))]
[JsonSerializable(typeof(IEnumerable<ModelSeriesDto>))]
[JsonSerializable(typeof(IEnumerable<ModelWithProviderIdDto>))]
[JsonSerializable(typeof(IEnumerable<NotificationDto>))]
[JsonSerializable(typeof(IEnumerable<OperatorInfo>))]
[JsonSerializable(typeof(IEnumerable<PricingAuditEventDto>))]
[JsonSerializable(typeof(IEnumerable<PricingTypeInfo>))]
[JsonSerializable(typeof(IEnumerable<ProviderDto>))]
[JsonSerializable(typeof(IEnumerable<ProviderKeyCredentialDto>))]
[JsonSerializable(typeof(IEnumerable<ProviderToolDto>))]
[JsonSerializable(typeof(IEnumerable<SeriesSimpleModelDto>))]
[JsonSerializable(typeof(IEnumerable<SimpleModelSeriesDto>))]
[JsonSerializable(typeof(IEnumerable<string>))]
[JsonSerializable(typeof(IEnumerable<ToolProviderDto>))]
[JsonSerializable(typeof(IncidentsResponse))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(IpCheckResult))]
[JsonSerializable(typeof(IpFilterDto))]
[JsonSerializable(typeof(IpFilterSettingsDto))]
[JsonSerializable(typeof(IReadOnlyList<GlobalSettingDefinitionDto>))]
[JsonSerializable(typeof(IReadOnlyList<PromptCachingCapability>))]
[JsonSerializable(typeof(IReadOnlyList<ProviderSettingsSchemaDto>))]
[JsonSerializable(typeof(KeyErrorDetailsDto))]
[JsonSerializable(typeof(List<AdminFunctionExecutionDto>))]
[JsonSerializable(typeof(List<DriftItemDto>))]
[JsonSerializable(typeof(List<FunctionConfigurationDto>))]
[JsonSerializable(typeof(List<FunctionCostDto>))]
[JsonSerializable(typeof(List<FunctionCredentialDto>))]
[JsonSerializable(typeof(List<MediaCleanupApprovalDto>))]
[JsonSerializable(typeof(List<MediaRecordResponse>))]
[JsonSerializable(typeof(List<MediaRetentionPolicyDto>))]
[JsonSerializable(typeof(List<ProviderErrorDto>))]
[JsonSerializable(typeof(List<ProviderErrorSummaryDto>))]
[JsonSerializable(typeof(List<ProviderSyncRunDto>))]
[JsonSerializable(typeof(List<VirtualKeyDto>))]
[JsonSerializable(typeof(LogRequestDto))]
[JsonSerializable(typeof(MediaCleanupApprovalActionDto))]
[JsonSerializable(typeof(MediaCleanupEnabledChangedDto))]
[JsonSerializable(typeof(MediaCleanupEnabledDto))]
[JsonSerializable(typeof(MediaCleanupPreviewDto))]
[JsonSerializable(typeof(MediaCleanupResponseDto))]
[JsonSerializable(typeof(MediaCleanupStatusDto))]
[JsonSerializable(typeof(MediaDeletionResponseDto))]
[JsonSerializable(typeof(MediaRestoreResponseDto))]
[JsonSerializable(typeof(MediaRetentionPolicyDetailDto))]
[JsonSerializable(typeof(MediaRetentionPolicyDto))]
[JsonSerializable(typeof(MediaStorageStats))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(ModelAuthorDto))]
[JsonSerializable(typeof(ModelCostBreakdownDto))]
[JsonSerializable(typeof(ModelCostDto))]
[JsonSerializable(typeof(ModelDto))]
[JsonSerializable(typeof(ModelIdentifierDto))]
[JsonSerializable(typeof(ModelProviderMappingDto))]
[JsonSerializable(typeof(ModelSeriesDto))]
[JsonSerializable(typeof(NotificationDto))]
[JsonSerializable(typeof(OverallMediaStorageStats))]
[JsonSerializable(typeof(PagedResult<IndeterminateTaskDto>))]
[JsonSerializable(typeof(PagedResult<LogRequestDto>))]
[JsonSerializable(typeof(PagedResult<ModelCostDto>))]
[JsonSerializable(typeof(PagedResult<ModelDto>))]
[JsonSerializable(typeof(PagedResult<PricingAuditEventDto>))]
[JsonSerializable(typeof(PagedResult<VirtualKeyGroupDto>))]
[JsonSerializable(typeof(PagedResult<VirtualKeyGroupTransactionDto>))]
[JsonSerializable(typeof(PricingAuditSummary))]
[JsonSerializable(typeof(PricingRulesConfig))]
[JsonSerializable(typeof(PricingRulesValidationResult))]
[JsonSerializable(typeof(PricingSimulationResponse))]
[JsonSerializable(typeof(PricingValidationResponse))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(PromptCachingAnalyticsDto))]
[JsonSerializable(typeof(PromptCachingConfigDto))]
[JsonSerializable(typeof(ProviderDto))]
[JsonSerializable(typeof(ProviderKeyCredentialDto))]
[JsonSerializable(typeof(ProviderSyncRunDto))]
[JsonSerializable(typeof(ProviderToolDto))]
[JsonSerializable(typeof(ProviderToolImportResultDto))]
[JsonSerializable(typeof(RefundResultDto))]
[JsonSerializable(typeof(RoutePolicyDto))]
[JsonSerializable(typeof(RoutingConfigurationDto))]
[JsonSerializable(typeof(RoutingDefaultsDto))]
[JsonSerializable(typeof(ServiceHealthResponse))]
[JsonSerializable(typeof(SimpleRetentionResponse))]
[JsonSerializable(typeof(StandardApiKeyTestResponse))]
[JsonSerializable(typeof(SystemInfoDto))]
[JsonSerializable(typeof(TaskCleanupResponseDto))]
[JsonSerializable(typeof(TaskResolutionAcceptedDto))]
[JsonSerializable(typeof(UsageStatisticsDto))]
[JsonSerializable(typeof(VirtualKeyCostBreakdownDto))]
[JsonSerializable(typeof(VirtualKeyDto))]
[JsonSerializable(typeof(VirtualKeyGroupDto))]
[JsonSerializable(typeof(VirtualKeyRateLimitUsageDto))]
[JsonSerializable(typeof(VirtualKeyUsageDto))]
[JsonSerializable(typeof(VirtualKeyValidationInfoDto))]
[JsonSerializable(typeof(VirtualKeyValidationResult))]
public partial class AdminHttpResponseJsonContext : JsonSerializerContext;
