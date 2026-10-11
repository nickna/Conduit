import { Paper, Stack, Group, Badge, Text, Alert, ActionIcon, Collapse, Code, Button } from '@mantine/core';
import { IconClock, IconSearch, IconLock, IconNetwork, IconAlertTriangle, IconAlertCircle, IconChevronDown, IconChevronUp, IconRefresh } from '@tabler/icons-react';
import type { ChatMessage, ChatErrorType } from '../types';

function getErrorTypeConfig(type: ChatErrorType) {
  switch (type) {
    case 'rate_limit':
      return { icon: IconClock, color: 'orange', label: 'Rate Limit' };
    case 'model_not_found':
      return { icon: IconSearch, color: 'blue', label: 'Model Not Found' };
    case 'auth_error':
      return { icon: IconLock, color: 'red', label: 'Authentication Error' };
    case 'network_error':
      return { icon: IconNetwork, color: 'gray', label: 'Network Error' };
    case 'server_error':
    default:
      return { icon: IconAlertTriangle, color: 'red', label: 'Server Error' };
  }
}

interface MessageErrorCardProps {
  message: ChatMessage;
  isExpanded: boolean;
  onToggleDetails: () => void;
  onRetry?: () => void;
  isLoading?: boolean;
}

export function MessageErrorCard({ message, isExpanded, onToggleDetails, onRetry, isLoading }: MessageErrorCardProps) {
  const error = message.error;
  if (!error) return null;

  const errorConfig = getErrorTypeConfig(error.type);
  const Icon = errorConfig.icon;
  const content = message.content;

  return (
    <Paper
      p="md"
      radius="md"
      withBorder
      className={`chat-message-error chat-message-error-${error.type.replace('_', '-')}`}
      style={{
        alignSelf: 'flex-start',
        maxWidth: '80%',
      }}
    >
      <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap">
          <Group gap="sm">
            <Icon size={20} color={`var(--mantine-color-${errorConfig.color}-6)`} />
            <Badge color={errorConfig.color} variant="light">
              {errorConfig.label}
            </Badge>
          </Group>
          {error.retryAfter && (
            <Badge size="sm" variant="light" color="gray">
              Retry after {error.retryAfter}s
            </Badge>
          )}
        </Group>

        <Text size="sm">
          {content?.replace('Error: ', '')}
        </Text>

        {error.suggestions && error.suggestions.length > 0 && (
          <Alert icon={<IconAlertCircle size={16} />} color={errorConfig.color} variant="light">
            <Stack gap="xs">
              <Text size="sm" fw={500}>Suggestions:</Text>
              {error.suggestions.map((suggestion) => (
                <Text key={suggestion} size="xs">{'\u2022'} {suggestion}</Text>
              ))}
            </Stack>
          </Alert>
        )}

        {(error.technical ?? error.code ?? error.statusCode) && (
          <>
            <Group gap="xs">
              <ActionIcon variant="subtle" size="sm" onClick={onToggleDetails}>
                {isExpanded ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
              </ActionIcon>
              <Text size="xs" c="dimmed">Technical Details</Text>
            </Group>
            <Collapse expanded={isExpanded}>
              <Paper p="sm" radius="sm" withBorder className="error-details-box">
                <Stack gap="xs">
                  {error.statusCode && (
                    <Text size="xs">
                      <Text span fw={500}>HTTP Status:</Text> {error.statusCode}
                    </Text>
                  )}
                  {error.code && (
                    <Text size="xs">
                      <Text span fw={500}>Error Code:</Text> {error.code}
                    </Text>
                  )}
                  {error.technical && (
                    <Code block style={{ fontSize: '0.75rem' }}>
                      {error.technical}
                    </Code>
                  )}
                </Stack>
              </Paper>
            </Collapse>
          </>
        )}

        {error.recoverable && onRetry && (
          <Group justify="flex-end">
            <Button
              size="xs"
              variant="light"
              color={errorConfig.color}
              leftSection={<IconRefresh size={14} />}
              onClick={onRetry}
              disabled={isLoading}
            >
              Retry
            </Button>
          </Group>
        )}
      </Stack>
    </Paper>
  );
}
