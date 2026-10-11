import React from 'react';
import { render, screen, fireEvent, waitFor } from '@/app/test-utils';
import '@testing-library/jest-dom';
import VideoQueue from '../VideoQueue';
import { useVideoStore } from '../../hooks/useVideoStore';
import { useEnhancedVideoGeneration } from '../../hooks/useEnhancedVideoGeneration';
import { MediaGenerationStatus } from '@/app/types/media';
import type { VideoTask } from '../../types';

// Mock notifications
jest.mock('@mantine/notifications', () => ({
  notifications: {
    show: jest.fn()
  }
}));

// Mock dependencies
jest.mock('../../hooks/useVideoStore');
jest.mock('../../hooks/useEnhancedVideoGeneration');
jest.mock('@/app/hooks/createMediaStore', () => ({
  createMediaStore: jest.fn(() => () => ({
    taskHistory: [],
    removeTask: jest.fn(),
    clearHistory: jest.fn(),
    addTask: jest.fn(),
    updateTask: jest.fn(),
    currentTask: null,
    setCurrentTask: jest.fn()
  })),
  MAX_HISTORY_SIZE: 100
}));
jest.mock('@/app/config/mediaGeneration', () => ({
  UI_CONFIG: {
    ICON_SIZES: {
      MEDIUM: 20
    }
  },
  VIDEO_POLLING_CONFIG: {
    INTERVAL_MS: 1000,
    TIMEOUT_MS: 300000,
    MAX_INTERVAL_MS: 5000
  },
  MEDIA_RETRY_CONFIG: {
    MAX_COUNT: 3,
    MIN_DELAY_MS: 1000,
    MAX_DELAY_MS: 10000
  }
}));
jest.mock('@/components/common/TimeDisplay', () => ({
  TimeDisplay: ({ date }: { date: string }) => <span>{new Date(date).toLocaleString()}</span>
}));

// Mock Mantine components
jest.mock('@mantine/core', () => {
  interface PaperProps {
    children: React.ReactNode;
    shadow?: string;
  }

  interface StackProps {
    children: React.ReactNode;
    gap?: string | number;
  }

  interface GroupProps {
    children: React.ReactNode;
    gap?: string | number;
    onClick?: () => void;
    style?: React.CSSProperties;
  }

  interface TextProps {
    children: React.ReactNode;
    fw?: string | number;
    size?: string;
    c?: string;
    lineClamp?: number;
    component?: string;
  }

  interface ButtonProps {
    children: React.ReactNode;
    onClick?: () => void;
    variant?: string;
    color?: string;
    size?: string;
    leftSection?: React.ReactNode;
    disabled?: boolean;
  }

  interface BadgeProps {
    children: React.ReactNode;
    variant?: string;
    size?: string;
  }

  interface ProgressProps {
    value?: number;
    size?: string;
    animated?: boolean;
    color?: string;
  }

  interface CollapseProps {
    children: React.ReactNode;
    expanded?: boolean;
  }

  interface ActionIconProps {
    children: React.ReactNode;
    variant?: string;
    size?: string;
  }

  interface AlertProps {
    children: React.ReactNode;
    icon?: React.ReactNode;
    color?: string;
    variant?: string;
  }

  interface ListProps {
    children: React.ReactNode;
    size?: string;
    mt?: string | number;
    spacing?: string | number;
  }

  interface ListItemProps {
    children: React.ReactNode;
  }

  interface BoxProps {
    children: React.ReactNode;
  }

  const Paper = ({ children, shadow }: PaperProps) => (
    <div
      data-testid="paper"
      data-shadow={shadow}
    >
      {children}
    </div>
  );

  const Stack = ({ children, gap }: StackProps) => (
    <div
      data-testid="stack"
      data-gap={gap}
    >
      {children}
    </div>
  );

  const Group = ({ children, gap, onClick, style }: GroupProps) => (
    <div
      data-testid="group"
      data-gap={gap}
      onClick={onClick}
      style={style}
    >
      {children}
    </div>
  );

  const Text = ({ children, fw, size, c, lineClamp }: TextProps) => {
    return (
      <span
        data-testid="text"
        data-fw={fw}
        data-size={size}
        data-c={c}
        data-lineclamp={lineClamp}
      >
        {children}
      </span>
    );
  };

  const Button = ({ children, onClick, variant, color, size, leftSection, disabled }: ButtonProps) => (
    <button
      data-testid="button"
      data-variant={variant}
      data-color={color}
      data-size={size}
      onClick={onClick}
      disabled={disabled}
    >
      {leftSection}
      {children}
    </button>
  );

  const Badge = ({ children, variant, size }: BadgeProps) => (
    <span
      data-testid="badge"
      data-variant={variant}
      data-size={size}
    >
      {children}
    </span>
  );

  const Progress = ({ value, size, animated, color }: ProgressProps) => (
    <div
      data-testid="progress"
      data-value={value}
      data-size={size}
      data-animated={animated}
      data-color={color}
    />
  );

  const Collapse = ({ children, expanded }: CollapseProps) =>
    expanded ? <div data-testid="collapse">{children}</div> : null;

  const ActionIcon = ({ children, variant, size }: ActionIconProps) => (
    <button
      data-testid="action-icon"
      data-variant={variant}
      data-size={size}
    >
      {children}
    </button>
  );

  const Alert = ({ children, icon, color, variant }: AlertProps) => (
    <div
      data-testid="alert"
      data-color={color}
      data-variant={variant}
    >
      {icon}
      {children}
    </div>
  );

  const List = Object.assign(
    ({ children, size, mt, spacing }: ListProps) => (
      <ul
        data-testid="list"
        data-size={size}
        data-mt={mt}
        data-spacing={spacing}
      >
        {children}
      </ul>
    ),
    {
      Item: ({ children }: ListItemProps) => <li data-testid="list-item">{children}</li>
    }
  );

  const Box = ({ children }: BoxProps) => <div data-testid="box">{children}</div>;

  const MantineProvider = ({ children }: { children: React.ReactNode }) => (
    <div data-testid="mantine-provider">{children}</div>
  );

  return {
    getDefaultZIndex: () => 100,
    MantineProvider,
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
  };
});

// Mock Tabler icons
jest.mock('@tabler/icons-react', () => ({
  IconVideo: () => <span>IconVideo</span>,
  IconX: () => <span>IconX</span>,
  IconRefresh: () => <span>IconRefresh</span>,
  IconAlertCircle: () => <span>IconAlertCircle</span>,
  IconChevronDown: () => <span>IconChevronDown</span>,
  IconChevronUp: () => <span>IconChevronUp</span>,
  IconClock: () => <span>IconClock</span>,
  IconHourglass: () => <span>IconHourglass</span>,
  IconCircleCheck: () => <span>IconCircleCheck</span>,
  IconCircleX: () => <span>IconCircleX</span>,
  IconBan: () => <span>IconBan</span>
}));

const mockUseVideoStore = useVideoStore as jest.MockedFunction<typeof useVideoStore>;
const mockUseEnhancedVideoGeneration = useEnhancedVideoGeneration as jest.MockedFunction<typeof useEnhancedVideoGeneration>;

describe('VideoQueue', () => {
  const mockCancelGeneration = jest.fn();
  const mockRetryGeneration = jest.fn().mockResolvedValue(undefined);

  const createMockTask = (overrides?: Partial<VideoTask>): VideoTask => ({
    id: 'task-1',
    prompt: 'Generate a video of ocean waves',
    status: MediaGenerationStatus.Generating,
    progress: 50,
    message: 'Processing video',
    estimatedTimeToCompletion: 120,
    retryCount: 0,
    retryHistory: [],
    error: null,
    result: null,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides
  } as VideoTask);

  beforeEach(() => {
    jest.clearAllMocks();
    mockUseVideoStore.mockReturnValue({
      currentTask: null,
      taskHistory: [],
      removeTask: jest.fn(),
      clearHistory: jest.fn(),
      addTask: jest.fn(),
      updateTask: jest.fn(),
      setCurrentTask: jest.fn()
    });

    mockUseEnhancedVideoGeneration.mockReturnValue({
      cancelGeneration: mockCancelGeneration,
      retryGeneration: mockRetryGeneration,
      generateVideo: jest.fn(),
      isGenerating: false
    });
  });

  describe('No Current Task', () => {
    it('should return null when no current task', () => {
      const { container } = render(<VideoQueue />);
      // Check that no paper/content is rendered (ignore style tags)
      expect(container.querySelector('[data-testid="paper"]')).toBeNull();
    });
  });

  describe('Active Task Display', () => {
    it('should display current task information', () => {
      const task = createMockTask();
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByText('Video Generation Queue')).toBeInTheDocument();
      expect(screen.getByText('Generate a video of ocean waves')).toBeInTheDocument();
      expect(screen.getByText(/Generating.*Processing video/)).toBeInTheDocument();
    });

    it('should display progress bar for active task', () => {
      const task = createMockTask({ progress: 75 });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      const progress = screen.getByTestId('progress');
      expect(progress).toHaveAttribute('data-value', '75');
      expect(progress).toHaveAttribute('data-animated', 'true');
    });

    it('should display ETA when available', () => {
      const task = createMockTask({ estimatedTimeToCompletion: 60 });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByText(/ETA:/)).toBeInTheDocument();
    });

    it('should show cancel button for active tasks', () => {
      const task = createMockTask({ status: MediaGenerationStatus.Generating });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      const cancelButton = screen.getByText('Cancel');
      expect(cancelButton).toBeInTheDocument();
      
      fireEvent.click(cancelButton);
      expect(mockCancelGeneration).toHaveBeenCalledWith('task-1');
    });
  });

  describe('Failed Task Handling', () => {
    it('should display error message for failed tasks', () => {
      const task = createMockTask({
        status: MediaGenerationStatus.Failed,
        error: 'API error: Generation failed'
      });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByTestId('alert')).toBeInTheDocument();
      expect(screen.getByText('API error: Generation failed')).toBeInTheDocument();
    });

    it('should show retry button for failed tasks', () => {
      const task = createMockTask({
        status: MediaGenerationStatus.Failed,
        retryCount: 1
      });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      const retryButton = screen.getByText(/Retry \(2 left\)/);
      expect(retryButton).toBeInTheDocument();
    });

    it('should not show retry button when max retries reached', () => {
      const task = createMockTask({
        status: MediaGenerationStatus.Failed,
        retryCount: 3
      });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.queryByText(/Retry/)).not.toBeInTheDocument();
    });

    it('should handle retry button click', async () => {
      const task = createMockTask({
        status: MediaGenerationStatus.Failed,
        retryCount: 0
      });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      const retryButton = screen.getByText(/Retry \(3 left\)/);
      fireEvent.click(retryButton);
      
      await waitFor(() => {
        expect(mockRetryGeneration).toHaveBeenCalledWith(task);
      });
    });
  });

  describe('Retry History', () => {
    it('should display retry history when present', () => {
      const task = createMockTask({
        retryHistory: [
          {
            attemptNumber: 1,
            error: 'First attempt failed',
            timestamp: '2024-01-01T12:00:00Z'
          },
          {
            attemptNumber: 2,
            error: 'Second attempt failed',
            timestamp: '2024-01-01T12:05:00Z'
          }
        ]
      });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByText('Retry History (2)')).toBeInTheDocument();
    });

    it('should toggle retry history visibility', () => {
      const task = createMockTask({
        retryHistory: [
          {
            attemptNumber: 1,
            error: 'First attempt failed',
            timestamp: '2024-01-01T12:00:00Z'
          }
        ]
      });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      // Initially collapsed
      expect(screen.queryByTestId('collapse')).not.toBeInTheDocument();
      
      // Click to expand
      fireEvent.click(screen.getByText('Retry History (1)'));
      
      // Should now be visible - check for the collapse component and list item
      expect(screen.getByTestId('collapse')).toBeInTheDocument();
      expect(screen.getByTestId('list-item')).toBeInTheDocument();
      // Check for the text content separately
      expect(screen.getByText(/Attempt 1:/)).toBeInTheDocument();
      expect(screen.getByText(/First attempt failed/)).toBeInTheDocument();
    });
  });

  describe('Status Display', () => {
    it('should display correct status for pending task', () => {
      const task = createMockTask({ status: MediaGenerationStatus.Pending });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByText(/Queued/)).toBeInTheDocument();
    });

    it('should display correct status for completed task', () => {
      const task = createMockTask({ status: MediaGenerationStatus.Completed });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByText(/Completed/)).toBeInTheDocument();
    });

    it('should display correct status for cancelled task', () => {
      const task = createMockTask({
        status: MediaGenerationStatus.Cancelled,
        error: 'User cancelled generation'
      });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByText(/Cancelled/)).toBeInTheDocument();
      expect(screen.getByTestId('alert')).toHaveAttribute('data-color', 'orange');
    });
  });

  describe('Attempt Counter', () => {
    it('should display attempt counter when retry count > 0', () => {
      const task = createMockTask({ retryCount: 2 });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.getByText('Attempt 3/4')).toBeInTheDocument();
    });

    it('should not display attempt counter when retry count is 0', () => {
      const task = createMockTask({ retryCount: 0 });
      mockUseVideoStore.mockReturnValue({
        currentTask: task,
        taskHistory: [],
        removeTask: jest.fn(),
        clearHistory: jest.fn(),
        addTask: jest.fn(),
        updateTask: jest.fn(),
        setCurrentTask: jest.fn()
      });

      render(<VideoQueue />);
      
      expect(screen.queryByText(/Attempt/)).not.toBeInTheDocument();
    });
  });
});
