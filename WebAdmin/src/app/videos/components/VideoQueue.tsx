'use client';

import { useState } from 'react';
import { UI_CONFIG } from '@/app/config/mediaGeneration';
import { 
  Paper, 
  Stack, 
  Group, 
  Text, 
  Button, 
  Badge, 
  Progress, 
  Collapse,
  ActionIcon,
  Alert,
  List,
  Box
} from '@mantine/core';
import { 
  IconVideo, 
  IconX, 
  IconRefresh,
  IconAlertCircle,
  IconChevronDown,
  IconChevronUp,
  IconClock,
  IconHourglass,
  IconCircleCheck,
  IconCircleX,
  IconBan
} from '@tabler/icons-react';
import { useVideoStore } from '../hooks/useVideoStore';
import { useEnhancedVideoGeneration } from '../hooks/useEnhancedVideoGeneration';
import { canRetry, type VideoTask } from '../types';
import { MediaGenerationStatus, isActiveStatus } from '@/app/types/media';
import { TimeDisplay } from '@/components/common/TimeDisplay';

// Retry button component
function RetryButton({ task, onRetry }: { task: VideoTask; onRetry: (task: VideoTask) => Promise<void> }) {
  const [isRetrying, setIsRetrying] = useState(false);
  
  if (!canRetry(task)) return null;
  
  const handleRetry = () => {
    setIsRetrying(true);
    void onRetry(task).finally(() => {
      setIsRetrying(false);
    });
  };
  
  return (
    <Button
      onClick={handleRetry}
      disabled={isRetrying}
      variant="light"
      size="sm"
      leftSection={<IconRefresh size={16} />}
    >
      {isRetrying ? 'Retrying...' : `Retry (${3 - task.retryCount} left)`}
    </Button>
  );
}

export default function VideoQueue() {
  const { currentTask } = useVideoStore();
  const { cancelGeneration, retryGeneration } = useEnhancedVideoGeneration();
  const [retryHistoryOpen, setRetryHistoryOpen] = useState(false);

  if (!currentTask) {
    return null;
  }

  const isActive = isActiveStatus(currentTask.status);

  return (
    <Paper shadow="sm" p="md" radius="md" withBorder>
      <Stack gap="md">
        <Group gap="sm">
          <IconVideo size={20} />
          <Text fw={600}>Video Generation Queue</Text>
        </Group>
        
        <Stack gap="sm">
          <Group gap="xs">
            {getStatusIcon(currentTask.status)}
            <Text size="sm" fw={500}>
              {getStatusText(currentTask.status)}
              {currentTask.message && ` - ${currentTask.message}`}
            </Text>
          </Group>
          
          <Text size="sm" c="dimmed" lineClamp={2}>
            {currentTask.prompt}
          </Text>
          
          {currentTask.estimatedTimeToCompletion && (
            <Group gap="xs">
              <IconClock size={14} />
              <Text size="xs" c="dimmed">
                ETA: {formatTime(currentTask.estimatedTimeToCompletion)}
              </Text>
            </Group>
          )}
          
          {currentTask.retryCount > 0 && (
            <Badge variant="light" size="sm">
              Attempt {currentTask.retryCount + 1}/{3 + 1}
            </Badge>
          )}
          
          {currentTask.progress > 0 && (
            <Progress 
              value={currentTask.progress} 
              size="sm" 
              animated={isActive}
              color={isActive ? 'blue' : 'gray'}
            />
          )}
          
          <Group gap="xs">
            {isActive && (
              <Button
                onClick={() => void cancelGeneration(currentTask.id)}
                variant="light"
                color="red"
                size="sm"
                leftSection={<IconX size={UI_CONFIG.ICON_SIZES.MEDIUM} />}
              >
                Cancel
              </Button>
            )}
            
            {currentTask.status === MediaGenerationStatus.Failed && (
              <RetryButton task={currentTask} onRetry={(task: VideoTask) => retryGeneration(task)} />
            )}
          </Group>
          
          {(currentTask.status === MediaGenerationStatus.Failed || currentTask.status === MediaGenerationStatus.Cancelled) && currentTask.error && (
            <Alert 
              icon={<IconAlertCircle size={16} />} 
              color={currentTask.status === MediaGenerationStatus.Cancelled ? 'orange' : 'red'}
              variant="light"
            >
              {currentTask.error}
            </Alert>
          )}
          
          {currentTask.retryHistory.length > 0 && (
            <Box>
              <Group 
                gap="xs" 
                onClick={() => setRetryHistoryOpen(!retryHistoryOpen)}
                style={{ cursor: 'pointer' }}
              >
                <ActionIcon variant="subtle" size="xs">
                  {retryHistoryOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
                </ActionIcon>
                <Text size="sm" c="dimmed">
                  Retry History ({currentTask.retryHistory.length})
                </Text>
              </Group>
              
              <Collapse expanded={retryHistoryOpen}>
                <List size="sm" mt="xs" spacing="xs">
                  {currentTask.retryHistory.map((retry) => (
                    <List.Item key={`${retry.attemptNumber}-${retry.timestamp}`}>
                      <Text size="xs">
                        <Text component="span" fw={500}>Attempt {retry.attemptNumber}:</Text> {retry.error}
                        <Text component="span" c="dimmed"> (<TimeDisplay date={retry.timestamp} />)</Text>
                      </Text>
                    </List.Item>
                  ))}
                </List>
              </Collapse>
            </Box>
          )}
        </Stack>
      </Stack>
    </Paper>
  );
}

function getStatusIcon(status: MediaGenerationStatus): React.ReactNode {
  const iconProps = { size: 16 };
  switch (status) {
    case MediaGenerationStatus.Pending:
      return <IconClock {...iconProps} />;
    case MediaGenerationStatus.Generating:
      return <IconHourglass {...iconProps} />;
    case MediaGenerationStatus.Completed:
      return <IconCircleCheck {...iconProps} color="green" />;
    case MediaGenerationStatus.Failed:
      return <IconCircleX {...iconProps} color="red" />;
    case MediaGenerationStatus.Cancelled:
      return <IconBan {...iconProps} color="orange" />;
    case MediaGenerationStatus.Idle:
    default:
      return <IconAlertCircle {...iconProps} />;
  }
}

function getStatusText(status: MediaGenerationStatus): string {
  switch (status) {
    case MediaGenerationStatus.Pending:
      return 'Queued';
    case MediaGenerationStatus.Generating:
      return 'Generating';
    case MediaGenerationStatus.Completed:
      return 'Completed';
    case MediaGenerationStatus.Failed:
      return 'Failed';
    case MediaGenerationStatus.Cancelled:
      return 'Cancelled';
    case MediaGenerationStatus.Idle:
    default:
      return status;
  }
}

function formatTime(seconds: number | undefined): string {
  // Handle undefined, null, or NaN values
  if (seconds === undefined || seconds === null || isNaN(seconds)) {
    return 'calculating...';
  }
  
  // Ensure we have a valid positive number
  const validSeconds = Math.max(0, seconds);
  
  if (validSeconds < 60) {
    return `${Math.round(validSeconds)}s`;
  } else {
    const minutes = Math.floor(validSeconds / 60);
    const remainingSeconds = Math.round(validSeconds % 60);
    return `${minutes}m ${remainingSeconds}s`;
  }
}
