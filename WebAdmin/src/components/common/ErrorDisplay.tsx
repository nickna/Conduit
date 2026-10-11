'use client';

import {
  Alert,
  Button,
  Stack,
  Text,
  Group,
  Code,
  Collapse,
  Box,
  Paper,
  Center,
  Container,
} from '@mantine/core';
import {
  IconAlertTriangle,
  IconAlertCircle,
  IconRefresh,
  IconLogin,
  IconHome,
  IconChevronDown,
  IconChevronUp,
} from '@tabler/icons-react';
import { useState } from 'react';
import { ErrorClassifier, type ErrorClassification } from '@/lib/utils/ui-error-classifier';

export interface ErrorAction {
  label: string;
  onClick: () => void;
  color?: string;
  variant?: 'filled' | 'light' | 'outline' | 'subtle';
  leftSection?: React.ReactNode;
}

export interface ErrorDisplayProps {
  error: Error | string;
  variant?: 'inline' | 'card' | 'fullscreen';
  size?: 'sm' | 'md' | 'lg';
  showDetails?: boolean;
  showStackTrace?: boolean;
  onRetry?: () => void;
  onReload?: () => void;
  onNavigateHome?: () => void;
  onLogin?: () => void;
  actions?: ErrorAction[];
  title?: string;
  className?: string;
  testId?: string;
}

export function ErrorDisplay({
  error,
  variant = 'inline',
  size = 'md',
  showDetails = false,
  showStackTrace = false,
  onRetry,
  onReload,
  onNavigateHome,
  onLogin,
  actions = [],
  title,
  className,
  testId,
}: ErrorDisplayProps) {
  const [detailsOpen, setDetailsOpen] = useState(false);
  
  // Classify the error
  const classification: ErrorClassification = ErrorClassifier.getClassification(error);
  const errorInstance = error instanceof Error ? error : new Error(String(error));
  
  // Determine display properties based on error classification
  const getDisplayProps = () => {
    const baseProps = {
      icon: <IconAlertTriangle size={16} />,
      color: 'red' as const,
    };
    
    switch (classification.severity) {
      case 'low':
        return { ...baseProps, color: 'yellow' as const };
      case 'medium':
        return { ...baseProps, color: 'orange' as const };
      case 'high':
        return { ...baseProps, color: 'red' as const };
      case 'critical':
        return { 
          ...baseProps, 
          color: 'red' as const,
          icon: <IconAlertCircle size={16} />,
        };
      default:
        return baseProps;
    }
  };

  // Generate automatic actions based on error classification
  const getAutomaticActions = (): ErrorAction[] => {
    const automaticActions: ErrorAction[] = [];

    switch (classification.recoveryAction) {
      case 'retry':
        if (onRetry) {
          automaticActions.push({
            label: 'Try Again',
            onClick: onRetry,
            color: 'blue',
            variant: 'light',
            leftSection: <IconRefresh size={16} />,
          });
        }
        break;
      case 'login':
        if (onLogin) {
          automaticActions.push({
            label: 'Log In',
            onClick: onLogin,
            color: 'blue',
            variant: 'filled',
            leftSection: <IconLogin size={16} />,
          });
        }
        break;
      case 'reload':
        if (onReload) {
          automaticActions.push({
            label: 'Reload Page',
            onClick: onReload,
            color: 'blue',
            variant: 'light',
            leftSection: <IconRefresh size={16} />,
          });
        }
        break;
      case 'navigate':
        if (onNavigateHome) {
          automaticActions.push({
            label: 'Go Home',
            onClick: onNavigateHome,
            color: 'blue',
            variant: 'light',
            leftSection: <IconHome size={16} />,
          });
        }
        break;
    }

    return automaticActions;
  };

  const displayProps = getDisplayProps();
  const automaticActions = getAutomaticActions();
  const allActions = [...automaticActions, ...actions];
  const hasActions = allActions.length > 0;
  const hasDetails = showDetails && (errorInstance.stack ?? errorInstance.name !== 'Error');
  
  const errorTitle = title ?? `${classification.type.charAt(0).toUpperCase() + classification.type.slice(1)} Error`;

  // Error content
  const errorContent = (
    <Stack gap="md">
      <Alert
        {...displayProps}
        title={errorTitle}
        variant="light"
        className={className}
        data-testid={testId}
      >
        <Stack gap="sm">
          <Text size={size === 'sm' ? 'sm' : 'md'}>
            {classification.displayMessage}
          </Text>

          {hasDetails && (
            <Button
              variant="subtle"
              size="xs"
              onClick={() => setDetailsOpen(!detailsOpen)}
              leftSection={detailsOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
              style={{ alignSelf: 'flex-start' }}
            >
              {detailsOpen ? 'Hide' : 'Show'} Details
            </Button>
          )}

          <Collapse expanded={detailsOpen}>
            <Stack gap="xs" mt="sm">
              {errorInstance.name !== 'Error' && (
                <Group>
                  <Text size="xs" c="dimmed" fw={500}>Type:</Text>
                  <Code style={{ fontSize: '0.75rem' }}>{errorInstance.name}</Code>
                </Group>
              )}
              
              {errorInstance.message && errorInstance.message !== classification.displayMessage && (
                <Group align="flex-start">
                  <Text size="xs" c="dimmed" fw={500}>Original:</Text>
                  <Text size="xs" c="dimmed" style={{ flex: 1 }}>
                    {errorInstance.message}
                  </Text>
                </Group>
              )}

              {showStackTrace && errorInstance.stack && (
                <Box>
                  <Text size="xs" c="dimmed" fw={500} mb={4}>Stack Trace:</Text>
                  <Code 
                    block 
                    style={{ 
                      maxHeight: '200px', 
                      overflow: 'auto',
                      fontSize: '0.6875rem',
                    }}
                  >
                    {errorInstance.stack}
                  </Code>
                </Box>
              )}
            </Stack>
          </Collapse>

          {hasActions && (
            <Group gap="sm" mt="sm">
              {allActions.map((action) => (
                <Button
                  key={`error-action-${action.label}-${action.color ?? 'blue'}-${action.variant ?? 'light'}`}
                  size={size}
                  variant={action.variant ?? 'light'}
                  color={action.color ?? 'blue'}
                  leftSection={action.leftSection}
                  onClick={action.onClick}
                >
                  {action.label}
                </Button>
              ))}
            </Group>
          )}
        </Stack>
      </Alert>
    </Stack>
  );

  // Render based on variant
  switch (variant) {
    case 'card':
      return (
        <Paper withBorder radius="md" p="md" className={className}>
          {errorContent}
        </Paper>
      );

    case 'fullscreen':
      return (
        <Container size="sm" py="xl">
          <Center style={{ minHeight: 'calc(100vh - 200px)' }}>
            <Box style={{ width: '100%', maxWidth: 500 }}>
              <Paper withBorder radius="lg" p="xl" shadow="md">
                {errorContent}
              </Paper>
            </Box>
          </Center>
        </Container>
      );

    case 'inline':
    default:
      return errorContent;
  }
}

// Convenience components for common error scenarios
export function NetworkErrorDisplay(props: Omit<ErrorDisplayProps, 'error'>) {
  const networkError = new Error('Network connection failed');
  networkError.name = 'NetworkError';
  
  return <ErrorDisplay {...props} error={networkError} />;
}

export function AuthErrorDisplay(props: Omit<ErrorDisplayProps, 'error'>) {
  const authError = new Error('Authentication required');
  authError.name = 'AuthenticationError';
  
  return <ErrorDisplay {...props} error={authError} />;
}

export function NotFoundErrorDisplay(props: Omit<ErrorDisplayProps, 'error'>) {
  const notFoundError = new Error('Resource not found');
  notFoundError.name = 'NotFoundError';
  
  return <ErrorDisplay {...props} error={notFoundError} />;
}

export function ServerErrorDisplay(props: Omit<ErrorDisplayProps, 'error'>) {
  const serverError = new Error('Internal server error occurred');
  serverError.name = 'ServerError';
  
  return <ErrorDisplay {...props} error={serverError} />;
}