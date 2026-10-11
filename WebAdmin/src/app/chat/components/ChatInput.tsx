'use client';

import { 
  Textarea, 
  Button, 
  Group, 
  ActionIcon,
  Badge,
  Tooltip,
  Stack,
  Text,
  Paper,
  Modal,
  JsonInput,
  Collapse,
  Divider
} from '@mantine/core';
import {
  IconSend,
  IconPlayerStop,
  IconTool,
  IconX,
  IconTrash,
  IconHistory
} from '@tabler/icons-react';
import { useState, useRef, KeyboardEvent, useEffect } from 'react';
import { ModelWithCapabilities, FunctionDefinition, ChatAttachment } from '../types';
import { useDisclosure } from '@mantine/hooks';
import { AttachmentUpload } from './AttachmentUpload';

interface ChatInputProps {
  onSendMessage: (message: string, attachments?: ChatAttachment[]) => void;
  isStreaming: boolean;
  onStopStreaming: () => void;
  disabled?: boolean;
  model?: ModelWithCapabilities;
  onInputChange?: (text: string) => void;
  onImagesChange?: (count: number) => void;
  onClearChat?: () => void;
  sendHistoryEnabled?: boolean;
  onToggleSendHistory?: () => void;
}

export function ChatInput({
  onSendMessage,
  isStreaming,
  onStopStreaming,
  disabled,
  model,
  onInputChange,
  onImagesChange,
  onClearChat,
  sendHistoryEnabled = true,
  onToggleSendHistory
}: ChatInputProps) {
  const [message, setMessage] = useState('');
  const [attachments, setAttachments] = useState<ChatAttachment[]>([]);
  const [functions, setFunctions] = useState<FunctionDefinition[]>([]);
  const [functionsEnabled, setFunctionsEnabled] = useState(false);
  const [functionModalOpened, { open: openFunctionModal, close: closeFunctionModal }] = useDisclosure(false);
  const [newFunctionJson, setNewFunctionJson] = useState('');

  // Notify parent component of input changes
  useEffect(() => {
    onInputChange?.(message);
  }, [message, onInputChange]);

  // Notify parent component of attachment changes.
  useEffect(() => {
    onImagesChange?.(attachments.length);
  }, [attachments.length, onImagesChange]);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const handleSend = () => {
    if (message.trim() || attachments.length > 0) {
      onSendMessage(message.trim(), attachments);
      setMessage('');
      setAttachments([]);
    }
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const addFunction = () => {
    try {
      const parsed = JSON.parse(newFunctionJson) as FunctionDefinition;
      if (parsed.name) {
        setFunctions(prev => [...prev, parsed]);
        setNewFunctionJson('');
        closeFunctionModal();
      }
    } catch (e) {
      console.error('Invalid function JSON:', e);
    }
  };

  const removeFunction = (index: number) => {
    setFunctions(prev => prev.filter((fn, i) => i !== index));
  };

  const supportsFunctions = model?.supportsFunctionCalling ?? model?.supportsToolUsage;
  const supportsAttachments = [
    model?.supportsVision,
    model?.supportsPdfInput,
    model?.supportsAudioInput,
    model?.supportsVideoInput,
  ].some((supported) => supported === true);

  return (
    <Stack gap="xs">
      {supportsFunctions && (
        <Collapse expanded={functionsEnabled}>
          <Paper p="xs" withBorder radius="sm">
            <Group justify="space-between" mb="xs">
              <Text size="sm" fw={600}>Functions</Text>
              <Button size="xs" onClick={openFunctionModal}>
                Add Function
              </Button>
            </Group>
            
            {functions.length === 0 ? (
              <Text size="xs" c="dimmed">No functions defined</Text>
            ) : (
              <Group gap="xs">
                {functions.map((func) => (
                  <Badge
                    key={func.name}
                    variant="light"
                    rightSection={
                      <ActionIcon 
                        size="xs" 
                        variant="subtle" 
                        onClick={() => removeFunction(functions.indexOf(func))}
                      >
                        <IconX size={12} />
                      </ActionIcon>
                    }
                  >
                    {func.name}
                  </Badge>
                ))}
              </Group>
            )}
          </Paper>
        </Collapse>
      )}
      
      {supportsAttachments && (
        <>
          <AttachmentUpload
            attachments={attachments}
            onAttachmentsChange={setAttachments}
            disabled={disabled ?? isStreaming}
            supportsImage={model?.supportsVision === true}
            supportsPdf={model?.supportsPdfInput === true || model?.supportsFileInput === true}
            supportsAudio={model?.supportsAudioInput === true}
            supportsVideo={model?.supportsVideoInput === true}
          />
          {attachments.length > 0 && <Divider />}
        </>
      )}
      
      <Group gap="xs" align="flex-end">
        <Textarea
          ref={textareaRef}
          style={{ flex: 1 }}
          placeholder={isStreaming ? "Generating response..." : "Type your message..."}
          value={message}
          onChange={(e) => setMessage(e.currentTarget.value)}
          onKeyDown={handleKeyDown}
          minRows={1}
          maxRows={10}
          autosize
          disabled={disabled ?? isStreaming}
        />
        
        <Group gap="xs">
          {onClearChat && (
            <Tooltip label="Clear chat history">
              <ActionIcon
                size="lg"
                variant="default"
                color="red"
                onClick={onClearChat}
                disabled={disabled ?? isStreaming}
              >
                <IconTrash size={20} />
              </ActionIcon>
            </Tooltip>
          )}

          {onToggleSendHistory && (
            <Tooltip label={sendHistoryEnabled ? "Send with history (enabled)" : "Send single message only (disabled)"}>
              <ActionIcon
                size="lg"
                variant={sendHistoryEnabled ? 'filled' : 'default'}
                color={sendHistoryEnabled ? 'blue' : 'gray'}
                onClick={onToggleSendHistory}
                disabled={disabled ?? isStreaming}
              >
                <IconHistory size={20} />
              </ActionIcon>
            </Tooltip>
          )}

          {supportsFunctions && (
            <Tooltip label="Toggle function calling">
              <ActionIcon
                variant={functionsEnabled ? 'filled' : 'default'}
                size="lg"
                onClick={() => setFunctionsEnabled(!functionsEnabled)}
              >
                <IconTool size={20} />
              </ActionIcon>
            </Tooltip>
          )}

          {isStreaming ? (
            <Button
              size="md"
              color="red"
              onClick={onStopStreaming}
              leftSection={<IconPlayerStop size={20} />}
            >
              Stop
            </Button>
          ) : (
            <Button
              size="md"
              onClick={handleSend}
              disabled={disabled ?? (!message.trim() && attachments.length === 0)}
              leftSection={<IconSend size={20} />}
            >
              Send
            </Button>
          )}
        </Group>
      </Group>
      
      <Modal
        opened={functionModalOpened}
        onClose={closeFunctionModal}
        title="Add Function"
        size="lg"
      >
        <Stack>
          <Text size="sm" c="dimmed">
            Define a function that the AI can call. The function should follow the OpenAI function format.
          </Text>
          
          <JsonInput
            label="Function Definition"
            placeholder={JSON.stringify({
              name: "get_weather",
              description: "Get the current weather",
              parameters: {
                type: "object",
                properties: {
                  location: {
                    type: "string",
                    description: "The city and state"
                  }
                },
                required: ["location"]
              }
            }, null, 2)}
            value={newFunctionJson}
            onChange={setNewFunctionJson}
            minRows={10}
            formatOnBlur
            autosize
          />
          
          <Group justify="flex-end">
            <Button variant="default" onClick={closeFunctionModal}>
              Cancel
            </Button>
            <Button onClick={addFunction}>
              Add Function
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
