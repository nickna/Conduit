import { useForm, type UseFormReturnType } from '@mantine/form';
import { useState, useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { notify } from '@/lib/notifications';
import {
  type ProviderDto,
  type ProviderConfigurationDefinition,
  type ProviderConfigurationSchema,
  type ProviderSettingField,
  type ProviderType
} from '@/lib/admin-api';
import { withAdminClient } from '@/lib/client/adminClient';
import { getProviderTypeFromDto } from '@/lib/utils/providerTypeUtils';
import { validators } from '@/lib/utils/form-validators';

export interface ProviderFormData {
  providerType: string;
  providerName: string;
  apiKey: string;
  apiEndpoint?: string;
  /** Structured, provider-scoped settings (for example a Cloudflare account ID), keyed by setting key. */
  settings: Record<string, string>;
  isEnabled: boolean;
  trustProviderReportedCosts: boolean;
  providerCostMarkupMultiplier: number;
}

export interface ProviderFormOption {
  value: string;
  label: string;
}

export interface ProviderFormLogicResult {
  form: UseFormReturnType<ProviderFormData>;
  isSubmitting: boolean;
  setIsSubmitting: (value: boolean) => void;
  isTesting: boolean;
  setIsTesting: (value: boolean) => void;
  testResult: { success: boolean; message: string } | null;
  setTestResult: (value: { success: boolean; message: string } | null) => void;
  availableProviders: ProviderFormOption[];
  setAvailableProviders: (value: ProviderFormOption[]) => void;
  isLoadingProviders: boolean;
  setIsLoadingProviders: (value: boolean) => void;
  existingProvider: ProviderDto | null;
  setExistingProvider: (value: ProviderDto | null) => void;
  isLoadingProvider: boolean;
  setIsLoadingProvider: (value: boolean) => void;
  initialFormValues: ProviderFormData;
  setInitialFormValues: (value: ProviderFormData) => void;
  providerDisplayName: string;
  isLoading: boolean;
  /** Backend-declared settings fields for the currently selected provider type. */
  settingFields: ProviderSettingField[];
  /** Backend-declared metadata for the currently selected provider type. */
  providerConfiguration?: ProviderConfigurationDefinition;
}

export function useProviderFormLogic(
  mode: 'add' | 'edit', 
  providerId?: number
): ProviderFormLogicResult {
  const router = useRouter();
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [isTesting, setIsTesting] = useState(false);
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null);
  const [availableProviders, setAvailableProviders] = useState<ProviderFormOption[]>([]);
  const [isLoadingProviders, setIsLoadingProviders] = useState(mode === 'add');
  const [existingProvider, setExistingProvider] = useState<ProviderDto | null>(null);
  const [isLoadingProvider, setIsLoadingProvider] = useState(mode === 'edit');
  const [configurationSchema, setConfigurationSchema] = useState<ProviderConfigurationSchema>({});
  const [isLoadingConfigurationSchema, setIsLoadingConfigurationSchema] = useState(true);
  const [initialFormValues, setInitialFormValues] = useState<ProviderFormData>(() => ({
    providerType: '',
    providerName: '',
    apiKey: '',
    apiEndpoint: '',
    settings: {},
    isEnabled: true,
    trustProviderReportedCosts: false,
    providerCostMarkupMultiplier: 1,
  }));

  const form = useForm<ProviderFormData>({
    initialValues: initialFormValues,
    validate: {
      providerType: (value) => (mode === 'add' && !value ? 'Provider type is required' : null),
      providerName: (value) => {
        if (mode === 'edit' && !value) {
          return 'Provider name is required';
        }
        return null;
      },
      apiEndpoint: (value) => {
        if (value && !validators.url(value)) {
          return 'Please enter a valid URL';
        }
        return null;
      },
    },
  });

  // Load the complete backend-owned provider catalog. It drives provider choices, labels, endpoint
  // requirements, help content, and structured fields in both add and edit modes.
  useEffect(() => {
    const loadConfigurationSchema = async () => {
      if (mode === 'add') {
        setIsLoadingProviders(true);
      }

      try {
        const schema = await withAdminClient(client => client.providers.getConfigurationSchema());
        setConfigurationSchema(schema);

        if (mode === 'add') {
          const providers = Object.values(schema)
            .filter((entry): entry is ProviderConfigurationDefinition => entry !== undefined)
            .map(entry => ({
              value: entry.providerType,
              label: entry.displayName,
            }));

          setAvailableProviders(providers);

          if (providers.length === 0) {
            notify.warning('No configurable provider types were returned.', 'No Providers Available');
            router.push('/llm-providers');
          }
        }
      } catch (error) {
        console.error('Error fetching provider configuration schema:', error);
        notify.error('Failed to load provider configuration');
      } finally {
        setIsLoadingConfigurationSchema(false);
        if (mode === 'add') {
          setIsLoadingProviders(false);
        }
      }
    };

    void loadConfigurationSchema();
  }, [mode, router]);

  // Fetch existing provider for edit mode and reinitialize form
  useEffect(() => {
    if (mode === 'edit' && providerId) {
      const loadProvider = async () => {
        setIsLoadingProvider(true);
        try {
          const provider = await withAdminClient(client => 
            client.providers.getById(providerId)
          );
          setExistingProvider(provider);
          
          const apiProvider = provider;
          
          // Create new form values
          const newFormValues: ProviderFormData = {
            providerType: provider.providerType?.toString() ?? '',
            providerName: typeof apiProvider.providerName === 'string' ? apiProvider.providerName : '',
            apiKey: '', // Don't show existing key for security
            apiEndpoint: apiProvider.baseUrl ?? '',
            settings: { ...(provider.settings ?? {}) },
            isEnabled: provider.isEnabled === true,
            trustProviderReportedCosts: provider.trustProviderReportedCosts === true,
            providerCostMarkupMultiplier: provider.providerCostMarkupMultiplier ?? 1,
          };
          
          // Hydrate the form with the loaded provider. useForm captures initialValues on its first
          // render, so updating the state alone would never reach the inputs; initialize() sets both
          // the values and the dirty baseline, and is a no-op if it somehow runs twice.
          setInitialFormValues(newFormValues);
          form.initialize(newFormValues);
        } catch (error) {
          console.error('Error fetching provider:', error);
          notify.error('Failed to load provider');
          router.push('/llm-providers');
        } finally {
          setIsLoadingProvider(false);
        }
      };
      
      void loadProvider();
    }
    // 'form' is intentionally omitted: useForm returns a new object every render, so including it
    // would refetch the provider on each render. The initialize() call inside is self-guarding.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, providerId, router]);

  let providerDisplayName = 'Unknown Provider';
  if (mode === 'edit' && existingProvider) {
    try {
      const providerType = getProviderTypeFromDto(existingProvider);
      providerDisplayName = configurationSchema[providerType]?.displayName
        ?? existingProvider.providerName
        ?? 'Unknown Provider';
    } catch {
      // Fallback to provider name if available
      const apiProvider = existingProvider;
      providerDisplayName = typeof apiProvider.providerName === 'string' ? apiProvider.providerName : 'Unknown Provider';
    }
  }

  const providerConfiguration =
    configurationSchema[form.values.providerType as ProviderType];
  const isLoading =
    isLoadingProviders || isLoadingProvider || isLoadingConfigurationSchema;
  // Secret-valued settings are deliberately excluded: they belong to a key credential, where they
  // are stored encrypted, not to the provider's plaintext settings bag. The key editor renders them.
  const settingFields = (providerConfiguration?.settings ?? [])
    .filter(field => !field.secret);

  return {
    form,
    isSubmitting,
    setIsSubmitting,
    isTesting,
    setIsTesting,
    testResult,
    setTestResult,
    availableProviders,
    setAvailableProviders,
    isLoadingProviders,
    setIsLoadingProviders,
    existingProvider,
    setExistingProvider,
    isLoadingProvider,
    setIsLoadingProvider,
    initialFormValues,
    setInitialFormValues,
    providerDisplayName,
    isLoading,
    settingFields,
    providerConfiguration,
  };
}
