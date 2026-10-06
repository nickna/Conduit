import { renderHook, act, waitFor } from '@testing-library/react';
import { useImageStore } from '../useImageStore';
import { MediaGenerationStatus } from '@/app/types/media';
import type { ImageTask } from '../../types';
import {
  InsufficientBalanceError,
  ValidationError,
} from '@/lib/conduit-common';

// Mock the browser client
jest.mock('@/lib/client/browserGatewayClient', () => ({
  getBrowserGatewayClient: jest.fn().mockResolvedValue({
    images: {
      generate: jest.fn()
    }
  })
}));

// Mock notifications
jest.mock('@mantine/notifications', () => ({
  notifications: {
    show: jest.fn()
  }
}));

// Mock the createMediaStore
type MockState = {
  error: Error | string | null;
  settings: Record<string, unknown>;
  currentTask: unknown;
  taskHistory: Array<{ id: string; [key: string]: unknown }>;
  maxHistorySize: number;
  persistHistory: boolean;
};

jest.mock('@/app/hooks/createMediaStore', () => ({
  createMediaStore: jest.fn(() => (set: (fn: (state: MockState) => MockState) => void) => ({
    error: null,
    settings: {
      model: ''
    },
    currentTask: null,
    taskHistory: [],
    maxHistorySize: 20,
    persistHistory: true,

    updateSettings: (updates: Record<string, unknown>) => set((state: MockState) => ({
      ...state,
      settings: { ...state.settings, ...updates }
    })),

    setError: (error: Error | string | null) => set((state: MockState) => ({ ...state, error })),

    addTask: (task: unknown) => set((state: MockState) => ({
      ...state,
      taskHistory: [task as { id: string; [key: string]: unknown }, ...state.taskHistory].slice(0, state.maxHistorySize),
      currentTask: task
    })),

    updateTask: (taskId: string, updates: Record<string, unknown>) => set((state: MockState) => ({
      ...state,
      taskHistory: state.taskHistory.map((t) =>
        t.id === taskId ? { ...t, ...updates, updatedAt: new Date().toISOString() } : t
      ),
      currentTask: (state.currentTask as { id?: string } | null)?.id === taskId
        ? { ...(state.currentTask as Record<string, unknown>), ...updates, updatedAt: new Date().toISOString() }
        : state.currentTask
    })),

    removeTask: (taskId: string) => set((state: MockState) => ({
      ...state,
      taskHistory: state.taskHistory.filter((t) => t.id !== taskId),
      currentTask: (state.currentTask as { id?: string } | null)?.id === taskId ? null : state.currentTask
    })),

    clearHistory: () => set((state: MockState) => ({ ...state, taskHistory: [], currentTask: null })),

    getTaskById: () => undefined,
    getCompletedTasks: () => [],
    getFailedTasks: () => [],
    getPendingTasks: () => []
  }))
}));

// Mock error handler
jest.mock('@/lib/gateway-api', () => ({
  createToastErrorHandler: jest.fn(() => jest.fn((error: { message?: string } | string) => {
    if (typeof error === 'object' && error?.message) return error.message;
    if (typeof error === 'string') return error;
    return 'An error occurred';
  })),
  shouldShowBalanceWarning: jest.fn((error: { code?: string }) => {
    return error?.code === 'insufficient_balance';
  })
}));

describe('useImageStore', () => {
  beforeEach(() => {
    // Clear store state between tests
    jest.clearAllMocks();
    localStorage.clear();
    // Reset the store state completely
    const { getState } = useImageStore;
    if (getState && typeof getState === 'function') {
      useImageStore.setState({
        prompt: '',
        status: MediaGenerationStatus.Idle,
        currentResults: [],
        settingsVisible: false,
        error: null,
        settings: {
          model: ''
        },
        currentTask: null,
        taskHistory: []
      });
    }
  });

  describe('Initial State', () => {
    it('should have correct initial state', () => {
      const { result } = renderHook(() => useImageStore());

      expect(result.current.prompt).toBe('');
      expect(result.current.status).toBe(MediaGenerationStatus.Idle);
      expect(result.current.currentResults).toEqual([]);
      expect(result.current.settingsVisible).toBe(false);
      expect(result.current.error).toBeNull();
      expect(result.current.settings).toEqual({
        model: ''
      });
    });
  });

  describe('Prompt Management', () => {
    it('should update prompt', () => {
      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.setPrompt('A beautiful landscape');
      });

      expect(result.current.prompt).toBe('A beautiful landscape');
    });

    it('should clear prompt', () => {
      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.setPrompt('Test prompt');
      });

      act(() => {
        result.current.setPrompt('');
      });

      expect(result.current.prompt).toBe('');
    });
  });

  describe('Image Generation', () => {
    it('should not generate with empty prompt', async () => {
      const { result } = renderHook(() => useImageStore());

      await act(async () => {
        await result.current.generateImages();
      });

      expect(result.current.error).toBeInstanceOf(ValidationError);
      expect(result.current.error).toMatchObject({
        message: 'Please enter a prompt for image generation',
      });
      expect(result.current.status).toBe(MediaGenerationStatus.Failed);
    });

    it('should not generate without model selected', async () => {
      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.setPrompt('Test prompt');
      });

      await act(async () => {
        await result.current.generateImages();
      });

      expect(result.current.error).toBeInstanceOf(ValidationError);
      expect(result.current.error).toMatchObject({
        message: 'Please select a model for image generation',
      });
      expect(result.current.status).toBe(MediaGenerationStatus.Failed);
    });

    it('should generate images successfully', async () => {
      const mockClient = {
        images: {
          generate: jest.fn().mockResolvedValue({
            data: [
              { url: 'https://example.com/image1.jpg' },
              { url: 'https://example.com/image2.jpg' }
            ]
          })
        }
      };

      // eslint-disable-next-line @typescript-eslint/no-unnecessary-type-assertion
      const browserClientModule = jest.requireMock('@/lib/client/browserGatewayClient') as {
        getBrowserGatewayClient: jest.MockedFunction<() => Promise<typeof mockClient>>;
      };
      const { getBrowserGatewayClient } = browserClientModule;
      getBrowserGatewayClient.mockResolvedValue(mockClient);

      const { result } = renderHook(() => useImageStore());

      // Set up valid state
      act(() => {
        result.current.setPrompt('Generate a sunset');
        result.current.updateSettings({ model: 'dall-e-3' });
      });

      await act(async () => {
        await result.current.generateImages();
      });

      await waitFor(() => {
        expect(result.current.status).toBe(MediaGenerationStatus.Completed);
        expect(result.current.currentResults).toHaveLength(2);
        expect(result.current.currentResults[0].url).toBe('https://example.com/image1.jpg');
      });

      expect(mockClient.images.generate).toHaveBeenCalledWith({
        prompt: 'Generate a sunset',
        model: 'dall-e-3',
        n: 1,
        response_format: 'url'
      });
    });

    it('should handle generation errors', async () => {
      const mockError = new Error('API Error');
      const mockClient = {
        images: {
          generate: jest.fn().mockRejectedValue(mockError)
        }
      };

      // eslint-disable-next-line @typescript-eslint/no-unnecessary-type-assertion
      const browserClientModule = jest.requireMock('@/lib/client/browserGatewayClient') as {
        getBrowserGatewayClient: jest.MockedFunction<() => Promise<typeof mockClient>>;
      };
      const { getBrowserGatewayClient } = browserClientModule;
      getBrowserGatewayClient.mockResolvedValue(mockClient);

      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.setPrompt('Generate image');
        result.current.updateSettings({ model: 'dall-e-3' });
      });

      await act(async () => {
        await result.current.generateImages();
      });

      await waitFor(() => {
        expect(result.current.status).toBe(MediaGenerationStatus.Failed);
        expect(result.current.error).toBeInstanceOf(Error);
        expect(result.current.error).toMatchObject({ message: 'API Error' });
        expect(result.current.currentResults).toEqual([]);
      });
    });

    it('should handle balance warning errors', async () => {
      const mockError = { code: 'insufficient_balance', message: 'Insufficient balance' };
      const mockClient = {
        images: {
          generate: jest.fn().mockRejectedValue(mockError)
        }
      };

      // eslint-disable-next-line @typescript-eslint/no-unnecessary-type-assertion
      const browserClientModule = jest.requireMock('@/lib/client/browserGatewayClient') as {
        getBrowserGatewayClient: jest.MockedFunction<() => Promise<typeof mockClient>>;
      };
      const { getBrowserGatewayClient } = browserClientModule;
      getBrowserGatewayClient.mockResolvedValue(mockClient);

      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.setPrompt('Generate image');
        result.current.updateSettings({ model: 'dall-e-3' });
      });

      await act(async () => {
        await result.current.generateImages();
      });

      await waitFor(() => {
        expect(result.current.error).toBeInstanceOf(InsufficientBalanceError);
        expect(result.current.error).toMatchObject({
          message: 'Please add credits to your account to generate images.',
        });
      });
    });

    it('should support dynamic parameters', async () => {
      const mockClient = {
        images: {
          generate: jest.fn().mockResolvedValue({
            data: [{ url: 'https://example.com/image.jpg' }]
          })
        }
      };

      // eslint-disable-next-line @typescript-eslint/no-unnecessary-type-assertion
      const browserClientModule = jest.requireMock('@/lib/client/browserGatewayClient') as {
        getBrowserGatewayClient: jest.MockedFunction<() => Promise<typeof mockClient>>;
      };
      const { getBrowserGatewayClient } = browserClientModule;
      getBrowserGatewayClient.mockResolvedValue(mockClient);

      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.setPrompt('Test');
        result.current.updateSettings({ model: 'dall-e-3' });
      });

      await act(async () => {
        await result.current.generateImages({ size: '1024x1024' });
      });

      expect(mockClient.images.generate).toHaveBeenCalledWith(
        expect.objectContaining({
          size: '1024x1024'
        })
      );
    });
  });

  describe('Results Management', () => {
    it('should clear results', () => {
      const { result } = renderHook(() => useImageStore());

      // Set some results first
      act(() => {
        (result.current as unknown as { currentResults: Array<{ url: string }> }).currentResults = [
          { url: 'https://example.com/image.jpg' }
        ];
      });

      act(() => {
        result.current.clearResults();
      });

      expect(result.current.currentResults).toEqual([]);
      expect(result.current.status).toBe(MediaGenerationStatus.Idle);
      expect(result.current.error).toBeNull();
      expect(result.current.currentTask).toBeNull();
    });

    it('should get latest results from history', () => {
      const { result } = renderHook(() => useImageStore());

      const mockTask: ImageTask = {
        id: 'task-1',
        prompt: 'Test',
        status: MediaGenerationStatus.Completed,
        progress: 100,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        settings: { model: 'dall-e-3' },
        retryCount: 0,
        retryHistory: [],
        result: {
          created: Date.now(),
          data: [{ url: 'https://example.com/result.jpg' }]
        }
      };

      act(() => {
        result.current.addTask(mockTask);
      });

      const latestResults = result.current.getLatestResults();
      expect(latestResults).toHaveLength(1);
      expect(latestResults[0].url).toBe('https://example.com/result.jpg');
    });
  });

  describe('Settings Management', () => {
    it('should toggle settings visibility', () => {
      const { result } = renderHook(() => useImageStore());

      expect(result.current.settingsVisible).toBe(false);

      act(() => {
        result.current.toggleSettings();
      });

      expect(result.current.settingsVisible).toBe(true);

      act(() => {
        result.current.toggleSettings();
      });

      expect(result.current.settingsVisible).toBe(false);
    });

    it('should update settings', () => {
      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.updateSettings({ model: 'dall-e-2' });
      });

      expect(result.current.settings.model).toBe('dall-e-2');
    });
  });

  describe('Task History', () => {
    it('should add tasks to history', () => {
      const { result } = renderHook(() => useImageStore());

      act(() => {
        result.current.setPrompt('Test');
        result.current.updateSettings({ model: 'dall-e-3' });
      });

      expect(result.current.taskHistory).toHaveLength(0);

      // Simulate task creation (normally done in generateImages)
      const task: ImageTask = {
        id: 'test-1',
        prompt: 'Test',
        status: MediaGenerationStatus.Generating,
        progress: 0,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        settings: result.current.settings,
        retryCount: 0,
        retryHistory: []
      };

      act(() => {
        result.current.addTask(task);
      });

      expect(result.current.taskHistory).toHaveLength(1);
      expect(result.current.currentTask).toEqual(task);
    });

    it('should clear history', () => {
      const { result } = renderHook(() => useImageStore());

      // Add some tasks
      const task: ImageTask = {
        id: 'test-1',
        prompt: 'Test',
        status: MediaGenerationStatus.Completed,
        progress: 100,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        settings: { model: 'dall-e-3' },
        retryCount: 0,
        retryHistory: []
      };

      act(() => {
        result.current.addTask(task);
      });

      act(() => {
        result.current.clearHistory();
      });

      expect(result.current.taskHistory).toHaveLength(0);
      expect(result.current.currentTask).toBeNull();
    });
  });
});
