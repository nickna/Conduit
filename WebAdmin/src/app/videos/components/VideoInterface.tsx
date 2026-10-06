'use client';

import { Stack, Paper, LoadingOverlay, Text } from '@mantine/core';
import { useVideoStore } from '../hooks/useVideoStore';
import { ErrorDisplay } from '@/components/common/ErrorDisplay';
import { DynamicParameters } from '@/components/parameters/DynamicParameters';
import { useMediaInterface } from '@/app/hooks/useMediaInterface';
import { ModelCapability } from '@/lib/gateway-api';
import EnhancedVideoPromptInput from './EnhancedVideoPromptInput';
import VideoGallery from './VideoGallery';
import VideoQueue from './VideoQueue';

export default function VideoInterface() {
  const {
    error,
    settings,
    updateSettings,
    setError,
    currentTask,
  } = useVideoStore();

  // Use shared media interface hook for model discovery and parameter management
  const {
    discoveryData,
    modelsLoading,
    modelsError,
    selectedDiscoveryModel,
    parameterState,
  } = useMediaInterface({
    capability: ModelCapability.VideoGeneration,
    currentModel: settings.model,
    onModelChange: (model) => updateSettings({ model }),
    onError: setError,
    parameterPersistPrefix: 'video',
  });

  if (modelsLoading) {
    return (
      <Stack gap="xl">
        <Paper p="md" withBorder>
          <LoadingOverlay visible={true} overlayProps={{ radius: 'sm', blur: 2 }} />
          <Text c="dimmed">Loading video generation models...</Text>
        </Paper>
      </Stack>
    );
  }

  if (modelsError || !discoveryData?.data || discoveryData.data.length === 0) {
    let errorInstance: Error;
    
    if (modelsError) {
      errorInstance = modelsError instanceof Error ? modelsError : new Error(String(modelsError));
    } else {
      errorInstance = new Error('No video generation models available. Please configure providers and add video generation models.');
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
              onClick: () => window.location.href = '/llm-providers',
              color: 'blue',
              variant: 'filled',
            },
            {
              label: 'Add Model Mappings', 
              onClick: () => window.location.href = '/model-mappings',
              color: 'blue',
              variant: 'light',
            }
          ]}
        />
        {!modelsError && (
          <Paper p="md" withBorder>
            <Text size="sm" mb="sm">To use video generation, you need to:</Text>
            <ol style={{ marginLeft: '1rem', marginTop: '0.5rem' }}>
              <li>Configure providers (MiniMax, etc.) in <strong>LLM Providers</strong></li>
              <li>Add video generation models in <strong>Model Mappings</strong></li>
              <li>Enable the <strong>&ldquo;Supports Video Generation&rdquo;</strong> checkbox for those models</li>
            </ol>
            <Text size="sm" mt="sm">
              Example model: <code>minimax-video</code>
            </Text>
          </Paper>
        )}
      </Stack>
    );
  }

  return (
    <Stack gap="xl">
      {/* Header */}
      <div className="video-header">
        <h1>🎬 Video Generation</h1>
      </div>

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
              onClick: () => window.location.href = '/llm-providers',
              color: 'blue',
              variant: 'light',
            }
          ]}
        />
      )}

      {/* Model Selector - Always Visible */}
      <Paper p="md" withBorder>
        <Stack gap="md">
          <Text fw={600}>Model Selection</Text>
          <select
            value={settings.model}
            onChange={(e) => updateSettings({ model: e.target.value })}
            className="form-select"
            style={{ 
              padding: '8px 12px',
              borderRadius: '4px',
              border: '1px solid #ced4da',
              fontSize: '14px',
              width: '100%'
            }}
          >
            <option value="">Select a model...</option>
            {discoveryData?.data?.map((model) => (
              <option key={model.id} value={model.id}>
                {model.display_name} ({model.provider})
              </option>
            ))}
          </select>
        </Stack>
      </Paper>


      {/* Dynamic Parameters from Model */}
      {selectedDiscoveryModel?.parameters && 
       selectedDiscoveryModel.parameters !== '{}' && (
        <DynamicParameters
          parameters={selectedDiscoveryModel.parameters}
          values={parameterState.values}
          onChange={parameterState.updateValues}
          context="video"
          title="Video Generation Parameters"
          collapsible={true}
          defaultExpanded={true}
        />
      )}

      {/* Generation Queue */}
      {currentTask && (
        <VideoQueue />
      )}

      {/* Prompt Input */}
      <EnhancedVideoPromptInput 
        dynamicParameters={parameterState.getSubmitValues()}
      />

      {/* Video Gallery */}
      <VideoGallery />
    </Stack>
  );
}
