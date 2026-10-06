import { FetchBaseApiClient } from './client/FetchBaseApiClient';
import { FetchVirtualKeyService } from './services/FetchVirtualKeyService';
import { FetchVirtualKeyGroupService } from './services/FetchVirtualKeyGroupService';
import { FetchProvidersService } from './services/FetchProvidersService';
import { FetchSystemService } from './services/FetchSystemService';
import { FetchModelMappingsService } from './services/FetchModelMappingsService';
import { FetchSettingsService } from './services/FetchSettingsService';
import { FetchAnalyticsService } from './services/FetchAnalyticsService';
import { FetchConfigurationService } from './services/FetchConfigurationService';
import { FetchIpFilterService } from './services/FetchIpFilterService';
import { FetchModelCostService } from './services/FetchModelCostService';
import { FetchMediaService } from './services/FetchMediaService';
import { FetchModelService } from './services/FetchModelService';
import { FetchModelSeriesService } from './services/FetchModelSeriesService';
import { FetchModelAuthorService } from './services/FetchModelAuthorService';
import { FetchProviderErrorsService } from './services/FetchProviderErrorsService';
import { ProviderToolsService } from './services/ProviderToolsService';
import {
  FetchFunctionConfigurationsService,
  FetchFunctionCredentialsService,
  FetchFunctionCostsService,
  FetchFunctionExecutionsService
} from './services/FetchFunctionsService';
import { FetchProviderSyncService } from './services/FetchProviderSyncService';
import type { ApiClientConfig } from './client/types';
import {
  isConduitError,
  isAuthError,
  isRateLimitError,
  isValidationError,
  isNotFoundError,
  isServerError
} from '@/lib/conduit-common';

/**
 * Type-safe Conduit Admin Client using native fetch
 *
 * Provides full type safety for all admin operations without HTTP complexity
 *
 * @example
 * ```typescript
 * const client = new FetchConduitAdminClient({
 *   baseUrl: 'https://admin.conduit.ai',
 *   masterKey: 'your-master-key'
 * });
 *
 * // All operations are fully typed
 * const keys = await client.virtualKeys.list();
 * const metrics = await client.dashboard.getMetrics();
 * ```
 */
export class FetchConduitAdminClient extends FetchBaseApiClient {
  public readonly virtualKeys: FetchVirtualKeyService;
  public readonly virtualKeyGroups: FetchVirtualKeyGroupService;
  public readonly providers: FetchProvidersService;
  public readonly system: FetchSystemService;
  public readonly modelMappings: FetchModelMappingsService;
  public readonly settings: FetchSettingsService;
  public readonly analytics: FetchAnalyticsService;
  public readonly configuration: FetchConfigurationService;
  public readonly ipFilters: FetchIpFilterService;
  public readonly modelCosts: FetchModelCostService;
  public readonly media: FetchMediaService;
  public readonly models: FetchModelService;
  public readonly modelSeries: FetchModelSeriesService;
  public readonly modelAuthors: FetchModelAuthorService;
  public readonly providerErrors: FetchProviderErrorsService;
  public readonly providerTools: ProviderToolsService;
  public readonly functionConfigurations: FetchFunctionConfigurationsService;
  public readonly functionCredentials: FetchFunctionCredentialsService;
  public readonly functionCosts: FetchFunctionCostsService;
  public readonly functionExecutions: FetchFunctionExecutionsService;
  public readonly providerSync: FetchProviderSyncService;

  constructor(config: ApiClientConfig) {
    super(config);

    // Initialize services
    this.virtualKeys = new FetchVirtualKeyService(this);
    this.virtualKeyGroups = new FetchVirtualKeyGroupService(this);
    this.providers = new FetchProvidersService(this);
    this.system = new FetchSystemService(this);
    this.modelMappings = new FetchModelMappingsService(this);
    this.settings = new FetchSettingsService(this);
    this.analytics = new FetchAnalyticsService(this);
    this.configuration = new FetchConfigurationService(this);
    this.ipFilters = new FetchIpFilterService(this);
    this.modelCosts = new FetchModelCostService(this);
    this.media = new FetchMediaService(this);
    this.models = new FetchModelService(this);
    this.modelSeries = new FetchModelSeriesService(this);
    this.modelAuthors = new FetchModelAuthorService(this);
    this.providerErrors = new FetchProviderErrorsService(this);
    this.providerTools = new ProviderToolsService(this);
    this.functionConfigurations = new FetchFunctionConfigurationsService(this);
    this.functionCredentials = new FetchFunctionCredentialsService(this);
    this.functionCosts = new FetchFunctionCostsService(this);
    this.functionExecutions = new FetchFunctionExecutionsService(this);
    this.providerSync = new FetchProviderSyncService(this);
  }

  /**
   * Type guard for checking if an error is a ConduitError
   * Re-exported from the local shared utilities for convenience
   */
  isConduitError = isConduitError;

  /**
   * Type guard for checking if an error is an authentication error
   * Re-exported from the local shared utilities for convenience
   */
  isAuthError = isAuthError;

  /**
   * Type guard for checking if an error is a rate limit error
   * Re-exported from the local shared utilities for convenience
   */
  isRateLimitError = isRateLimitError;

  /**
   * Type guard for checking if an error is a validation error
   * Re-exported from the local shared utilities for convenience
   */
  isValidationError = isValidationError;

  /**
   * Type guard for checking if an error is a not found error
   * Re-exported from the local shared utilities for convenience
   */
  isNotFoundError = isNotFoundError;

  /**
   * Type guard for checking if an error is a server error
   * Re-exported from the local shared utilities for convenience
   */
  isServerError = isServerError;
}

// Export the fetch-based client as the default
export default FetchConduitAdminClient;

// Re-export types for convenience
export type {
  ApiClientConfig,
  RequestConfig,
  RetryConfig,
  Logger,
  CacheProvider,
} from './client/types';

// Re-export generated types
export type { components, operations, paths } from '@/generated/admin-api';

// Re-export specific schema types for convenience
// NOTE: These types are available via components['schemas']['TypeName']
// They are not directly exported from the generated file
