'use client';

import { useEffect, useState, useCallback } from 'react';
import {
  Container,
  Paper,
  Stack,
  Center,
  Loader,
  Alert,
  Group,
  Badge,
  ActionIcon,
  Tooltip,
  Modal
} from '@mantine/core';
import {
  IconAlertCircle,
  IconSettings,
  IconAdjustments,
  IconLayoutSidebarLeftCollapse,
  IconLayoutSidebarLeftExpand
} from '@tabler/icons-react';
import { ModelSelector } from './ModelSelector';
import { ChatInput } from './ChatInput';
import { ChatMessages } from './ChatMessages';
import { ChatSettings } from './ChatSettings';
import { TokenCounter } from './TokenCounter';
import { ErrorDisplay } from '@/components/common/ErrorDisplay';
import { 
  ChatMessage,
} from '../types';
import { usePerformanceSettings } from '../hooks/usePerformanceSettings';
import { useChatStore } from '../hooks/useChatStore';
import { useDiscoveryModels } from '../hooks/useDiscoveryModels';
import { ModelCapability } from '@/lib/gateway-api';
import { useChatStreamingLogic } from './ChatStreamingLogic';
import { DynamicParameters } from '@/components/parameters/DynamicParameters';
import { useParameterState } from '@/components/parameters/hooks/useParameterState';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useAdminClient } from '@/lib/client/adminClient';
import type { FunctionConfigurationDto } from '@/lib/admin-api';
import { useChatLayout } from '../hooks/useChatLayout';

export function ChatInterface() {
  const router = useRouter();
  const { data: discoveryData, isLoading: modelsLoading } = useDiscoveryModels(ModelCapability.Chat); // Filter for chat-capable models only
  const [selectedModel, setSelectedModel] = useState<string | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<Error | null>(null);
  const [streamingContent, setStreamingContent] = useState('');
  const [streamingChannel, setStreamingChannel] = useState<string | null>(null);
  const [tokensPerSecond, setTokensPerSecond] = useState<number | null>(null);
  const [reasoningExpanded, setReasoningExpanded] = useState(true); // Default to expanded
  const [currentInputText, setCurrentInputText] = useState('');
  const [currentInputImages, setCurrentInputImages] = useState(0);
  const [sendHistoryEnabled, setSendHistoryEnabled] = useState(true); // Default to sending history
  const [selectedFunctionIds, setSelectedFunctionIds] = useState<number[]>([]);
  const [availableFunctions, setAvailableFunctions] = useState<FunctionConfigurationDto[]>([]);

  // Layout state management
  const chatLayout = useChatLayout();

  // Modal state
  const [settingsModalOpen, setSettingsModalOpen] = useState(false);
  const [parametersModalOpen, setParametersModalOpen] = useState(false);

  const performanceSettings = usePerformanceSettings();
  const { executeWithAdmin } = useAdminClient();
  const { 
    getActiveSession, 
    createSession,
    activeSessionId 
  } = useChatStore();

  // Load available functions on mount
  useEffect(() => {
    const loadFunctions = async () => {
      try {
        const functions = await executeWithAdmin(client =>
          client.functionConfigurations.list()
        );
        // Filter to only enabled functions
        setAvailableFunctions(functions.filter(f => f.isEnabled));
      } catch (err) {
        console.warn('Failed to load functions:', err);
        // Don't show error to user - function calling is optional
      }
    };
    void loadFunctions();
  }, [executeWithAdmin]);

  // Set initial model when data loads
  useEffect(() => {
    if (discoveryData?.data && discoveryData.data.length > 0 && !selectedModel) {
      setSelectedModel(discoveryData.data[0].id);
    }
  }, [discoveryData, selectedModel]);

  // Ensure we have an active session
  useEffect(() => {
    if (selectedModel && !activeSessionId) {
      createSession(selectedModel);
    }
  }, [selectedModel, activeSessionId, createSession]);

  const currentDiscoveryModel = discoveryData?.data?.find(m => m.id === selectedModel);
  
  // Use max_tokens from discovery API
  const maxContextTokens = currentDiscoveryModel?.max_tokens ?? 128000;
  
  // Use parameters from discovery model
  const modelParameters = currentDiscoveryModel?.parameters ?? '{}';
  const parameterState = useParameterState({
    parameters: modelParameters,
    persistKey: `chat-params-${selectedModel ?? 'default'}`,
  });

  // Only pass function IDs if the model supports function calling
  const modelSupportsFunctionCalling = currentDiscoveryModel?.capabilities?.function_calling === true;
  const effectiveFunctionIds = modelSupportsFunctionCalling && selectedFunctionIds.length > 0
    ? selectedFunctionIds
    : undefined;

  // Use streaming logic hook
  const { sendMessage, abortControllerRef } = useChatStreamingLogic({
    selectedModel,
    messages,
    setMessages,
    isLoading,
    setIsLoading,
    setStreamingContent,
    setStreamingChannel,
    setTokensPerSecond,
    setError,
    getActiveSession,
    performanceSettings,
    dynamicParameters: parameterState.getSubmitValues(),
    sendHistoryEnabled,
    functionConfigurationIds: effectiveFunctionIds,
    availableFunctions,
  });

  // Handle retry for error messages
  const handleRetryMessage = useCallback((errorMessageId: string) => {
    // Find the error message index
    const errorIndex = messages.findIndex(m => m.id === errorMessageId);
    if (errorIndex <= 0) return;

    // Find the preceding user message
    for (let i = errorIndex - 1; i >= 0; i--) {
      if (messages[i].role === 'user') {
        const userMessage = messages[i];
        // Remove the error message before retrying
        setMessages(prev => prev.filter(m => m.id !== errorMessageId));
        // Resend the user message
        void sendMessage(userMessage.content, userMessage.attachments ?? userMessage.images);
        return;
      }
    }
  }, [messages, setMessages, sendMessage]);

  // Cleanup on unmount - abort any pending requests
  useEffect(() => {
    return () => {
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
        abortControllerRef.current = null;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []); // Empty dependency array - only run on unmount

  if (modelsLoading) {
    return (
      <Center h="100vh">
        <Loader size="lg" />
      </Center>
    );
  }

  if (error) {
    return (
      <Container size="sm" mt="xl">
        <ErrorDisplay 
          error={error}
          variant="card"
          showDetails={true}
          onRetry={() => {
            setError(null);
            setIsLoading(false);
          }}
          actions={[
            {
              label: 'Configure Providers',
              onClick: () => router.push('/llm-providers'),
              color: 'blue',
              variant: 'light',
            }
          ]}
        />
      </Container>
    );
  }

  if (!discoveryData?.data || discoveryData.data.length === 0) {
    return (
      <Container size="sm" mt="xl">
        <Alert icon={<IconAlertCircle size={16} />} color="yellow" title="No models available">
          No models are currently configured. Please add model mappings first.<br />
          <Link href="/model-mappings">Add model mappings</Link>
        </Alert>
      </Container>
    );
  }

  return (
    <Container size="lg" py="md">
      <Stack gap="md">
        {/* Header */}
        <Paper p="md" withBorder>
          <Group justify="space-between">
            <Group style={{ flex: 1 }}>
              <ModelSelector
                value={selectedModel}
                onChange={setSelectedModel}
                modelData={discoveryData?.data ?? []}
                style={{ flex: 1, maxWidth: 400 }}
              />
              {currentDiscoveryModel?.capabilities?.vision && (
                <Badge variant="light" color="blue">
                  Image Input
                </Badge>
              )}
              {currentDiscoveryModel?.capabilities?.video_input && (
                <Badge variant="light" color="grape">
                  Video Input
                </Badge>
              )}
              {!chatLayout.compactMode && currentDiscoveryModel && (
                <TokenCounter
                  messages={messages}
                  maxTokens={maxContextTokens}
                  compact={true}
                  currentInputText={currentInputText}
                  currentInputImages={currentInputImages}
                />
              )}
            </Group>
            <Group gap="xs">
              <Tooltip label={chatLayout.compactMode ? 'Expand Controls' : 'Compact Mode'}>
                <ActionIcon
                  size="lg"
                  variant="light"
                  onClick={chatLayout.toggleCompactMode}
                  aria-label="Toggle compact mode"
                  color={chatLayout.compactMode ? 'blue' : undefined}
                >
                  {chatLayout.compactMode ? (
                    <IconLayoutSidebarLeftExpand size={20} />
                  ) : (
                    <IconLayoutSidebarLeftCollapse size={20} />
                  )}
                </ActionIcon>
              </Tooltip>
              {!chatLayout.compactMode && (
                <>
                  <Tooltip label="Chat Settings">
                    <ActionIcon
                      size="lg"
                      variant="light"
                      onClick={() => setSettingsModalOpen(true)}
                      aria-label="Open chat settings"
                    >
                      <IconSettings size={20} />
                    </ActionIcon>
                  </Tooltip>
                  {currentDiscoveryModel?.parameters && currentDiscoveryModel.parameters !== '{}' && (
                    <Tooltip label="Model Parameters">
                      <ActionIcon
                        size="lg"
                        variant="light"
                        onClick={() => setParametersModalOpen(true)}
                        aria-label="Open model parameters"
                      >
                        <IconAdjustments size={20} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                </>
              )}
            </Group>
          </Group>
        </Paper>

        {/* Messages Section */}
        <Paper p="md" withBorder style={{ minHeight: '500px', display: 'flex', flexDirection: 'column' }}>
          <ChatMessages
            messages={messages}
            isLoading={isLoading}
            streamingContent={isLoading ? streamingContent : undefined}
            streamingChannel={isLoading ? streamingChannel : null}
            tokensPerSecond={performanceSettings.showTokensPerSecond ? tokensPerSecond : null}
            reasoningExpanded={reasoningExpanded}
            onRetryMessage={handleRetryMessage}
          />
        </Paper>

        {/* Input Section */}
        <Paper p="md" withBorder>
          <ChatInput
            onSendMessage={(message, attachments) => {
              // Clear input state when message is sent
              setCurrentInputText('');
              setCurrentInputImages(0);
              void sendMessage(message, attachments);
            }}
            isStreaming={isLoading}
            onStopStreaming={() => {}}
            disabled={!selectedModel}
            model={currentDiscoveryModel ? {
              id: currentDiscoveryModel.id,
              providerId: '',
              displayName: currentDiscoveryModel.display_name ?? currentDiscoveryModel.id,
              supportsVision: currentDiscoveryModel.capabilities?.image_input === true
                || currentDiscoveryModel.capabilities?.vision === true,
              supportsVideoInput: currentDiscoveryModel.capabilities?.video_input === true,
              supportsAudioInput: currentDiscoveryModel.capabilities?.audio_input === true,
              supportsFileInput: currentDiscoveryModel.capabilities?.file_input === true,
              supportsPdfInput: currentDiscoveryModel.capabilities?.pdf_input === true
            } : undefined}
            onInputChange={setCurrentInputText}
            onImagesChange={setCurrentInputImages}
            sendHistoryEnabled={sendHistoryEnabled}
            onToggleSendHistory={() => setSendHistoryEnabled(!sendHistoryEnabled)}
            onClearChat={() => {
              // Abort any ongoing streaming
              if (abortControllerRef.current) {
                abortControllerRef.current.abort();
              }
              // Clear all chat state
              setMessages([]);
              setStreamingContent('');
              setStreamingChannel(null);
              setTokensPerSecond(null);
              setError(null);
              setIsLoading(false);
              setCurrentInputText('');
              setCurrentInputImages(0);
            }}
          />
        </Paper>

        {/* Chat Settings Modal */}
        <Modal
          opened={settingsModalOpen}
          onClose={() => setSettingsModalOpen(false)}
          title="Chat Settings"
          size="lg"
        >
          <Stack gap="md">
            <ChatSettings
              reasoningExpanded={reasoningExpanded}
              onReasoningExpandedChange={setReasoningExpanded}
              availableFunctions={availableFunctions}
              selectedFunctionIds={selectedFunctionIds}
              onFunctionIdsChange={setSelectedFunctionIds}
            />

            {/* Token Counter */}
            {currentDiscoveryModel && (
              <TokenCounter
                messages={messages}
                maxTokens={maxContextTokens}
                compact={false}
                currentInputText={currentInputText}
                currentInputImages={currentInputImages}
              />
            )}
          </Stack>
        </Modal>

        {/* Model Parameters Modal */}
        <Modal
          opened={parametersModalOpen}
          onClose={() => setParametersModalOpen(false)}
          title="Model Parameters"
          size="lg"
        >
          {currentDiscoveryModel?.parameters && currentDiscoveryModel.parameters !== '{}' && (
            <DynamicParameters
              parameters={currentDiscoveryModel.parameters}
              values={parameterState.values}
              onChange={parameterState.updateValues}
              context="chat"
              collapsible={false}
            />
          )}
        </Modal>
      </Stack>
    </Container>
  );
}
