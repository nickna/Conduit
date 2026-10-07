'use client';

import {
  Stack,
  Title,
  Text,
  Group,
  Button,
  Alert,
  LoadingOverlay,
  Paper,
} from '@mantine/core';
import { IconSettings } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { useImageStore } from '../hooks/useImageStore';
import { ErrorDisplay } from '@/components/common/ErrorDisplay';
import { DynamicParameters } from '@/components/parameters/DynamicParameters';
import { useMediaInterface } from '@/app/hooks/useMediaInterface';
import { ModelCapability } from '@/lib/gateway-api';
import ImageSettings from './ImageSettings';
import ImagePromptInput from './ImagePromptInput';
import ImageGallery from './ImageGallery';
import { MediaGenerationStatus } from '@/app/types/media';

export default function ImageInterface() {
  const router = useRouter();
  const {
    status,
    error,
    settingsVisible,
    settings,
    updateSettings,
    toggleSettings,
    setError,
  } = useImageStore();

  // Use shared media interface hook for model discovery and parameter management
  const {
    discoveryData,
    modelsLoading,
    modelsError,
    selectedDiscoveryModel,
    parameterState,
  } = useMediaInterface({
    capability: ModelCapability.ImageGeneration,
    currentModel: settings.model,
    onModelChange: (model) => updateSettings({ model }),
    onError: setError,
    parameterPersistPrefix: 'image',
  });

  if (modelsLoading) {
    return (
      <Stack gap="xl">
        <Paper p="md" withBorder>
          <LoadingOverlay visible={true} overlayProps={{ radius: 'sm', blur: 2 }} />
          <Text c="dimmed">Loading image generation models...</Text>
        </Paper>
      </Stack>
    );
  }

  if (modelsError || !discoveryData?.data || discoveryData.data.length === 0) {
    const errorInstance = modelsError 
      ? new Error(`Error loading models: ${modelsError.message}`)
      : new Error('No image generation models available. Please configure providers and add image generation models.');
    
    if (modelsError) {
      errorInstance.name = 'ModelLoadError';
    } else {
      errorInstance.name = 'ConfigurationError';
    }

    return (
      <Stack gap="xl">
        <ErrorDisplay 
          error={errorInstance}
          variant="card"
          showDetails={!!modelsError}
          actions={[
            {
              label: 'Configure Providers',
              onClick: () => router.push('/llm-providers'),
              color: 'blue',
              variant: 'filled',
            },
            {
              label: 'Add Model Mappings', 
              onClick: () => router.push('/model-mappings'),
              color: 'blue',
              variant: 'light',
            }
          ]}
        />
        {!modelsError && (
          <Alert color="blue" variant="light">
            <Text size="sm">To use image generation, you need to:</Text>
            <ol style={{ marginLeft: '1rem', marginTop: '0.5rem' }}>
              <li>Configure providers (OpenAI, MiniMax, etc.) in <strong>LLM Providers</strong></li>
              <li>Add image generation models in <strong>Model Mappings</strong></li>
              <li>Enable the <strong>&quot;Supports Image Generation&quot;</strong> checkbox for those models</li>
            </ol>
            <Text size="sm" mt="sm">
              Example models: <code>dall-e-2</code>, <code>dall-e-3</code>, <code>minimax-image</code>
            </Text>
          </Alert>
        )}
      </Stack>
    );
  }

  return (
    <Stack gap="xl">
      {/* Header */}
      <Group justify="space-between">
        <div>
          <Title order={1}>Image Generation</Title>
          <Text c="dimmed">Create AI-generated images from text prompts</Text>
        </div>
        <Button 
          variant="light"
          leftSection={<IconSettings size={16} />}
          onClick={toggleSettings}
        >
          Settings
        </Button>
      </Group>

      {/* Error Display */}
      {error && (
        <ErrorDisplay 
          error={error}
          variant="inline"
          showDetails={true}
          onRetry={() => setError(null)}
          actions={[
            {
              label: 'Configure Providers',
              onClick: () => router.push('/llm-providers'),
              color: 'blue',
              variant: 'light',
            }
          ]}
        />
      )}

      {/* Status Display */}
      {status !== MediaGenerationStatus.Idle && (
        <Alert
          color={(() => {
            if (status === MediaGenerationStatus.Generating) return 'blue';
            if (status === MediaGenerationStatus.Completed) return 'green';
            return 'red';
          })()}
          title={(() => {
            if (status === MediaGenerationStatus.Generating) return 'Generating images...';
            if (status === MediaGenerationStatus.Completed) return 'Images generated successfully!';
            return 'Generation failed';
          })()}
        />
      )}

      {/* Settings Panel */}
      {settingsVisible && (
        <Paper p="md" withBorder>
          <ImageSettings models={discoveryData?.data || []} />
        </Paper>
      )}

      {/* Dynamic Parameters from Model */}
      {selectedDiscoveryModel?.parameters && selectedDiscoveryModel.parameters !== '{}' && (
        <DynamicParameters
          parameters={selectedDiscoveryModel.parameters}
          values={parameterState.values}
          onChange={parameterState.updateValues}
          context="image"
          title="Image Generation Parameters"
          collapsible={true}
          defaultExpanded={false}
        />
      )}

      {/* Prompt Input */}
      <Paper p="md" withBorder>
        <ImagePromptInput dynamicParameters={parameterState.getSubmitValues()} />
      </Paper>

      {/* Image Gallery */}
      <ImageGallery />
    </Stack>
  );
}
