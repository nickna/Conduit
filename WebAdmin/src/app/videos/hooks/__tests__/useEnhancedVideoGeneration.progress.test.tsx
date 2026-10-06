import { renderHook, act } from '@testing-library/react';
import { useEnhancedVideoGeneration } from '../useEnhancedVideoGeneration';
import { setupMocks } from './videoTest.helpers';
import type { VideoTask } from '../../types';
import * as browserClientModule from '@/lib/client/browserGatewayClient';
import type { VideoProgressCallbacks } from '@/lib/gateway-api';
import { MediaGenerationStatus } from '@/app/types/media';

// Mock the browser client module
jest.mock('@/lib/client/browserGatewayClient');

// Mock the useVideoStore hook directly in this test file
jest.mock('../useVideoStore');

interface MockVideoClient {
  videos: {
    generateWithProgress: jest.Mock;
    cancelTask: jest.Mock;
  };
}

describe('useEnhancedVideoGeneration - Progress Tracking', () => {
  let storeMocks: ReturnType<typeof setupMocks>;
  let mockClient: MockVideoClient;
  let mockGenerateWithProgress: jest.Mock;

  beforeEach(() => {
    jest.clearAllMocks();
    storeMocks = setupMocks();

    // Setup mock client
    mockGenerateWithProgress = jest.fn();
    mockClient = {
      videos: {
        generateWithProgress: mockGenerateWithProgress,
        cancelTask: jest.fn().mockResolvedValue(undefined),
      },
    };

    // Mock the getBrowserGatewayClient function
    (browserClientModule.getBrowserGatewayClient as jest.Mock).mockResolvedValue(mockClient);

    // Setup default success response with progress simulation
    mockGenerateWithProgress.mockImplementation((request: unknown, callbacks?: VideoProgressCallbacks) => {
      // Simulate progress callbacks
      setTimeout(() => {
        if (callbacks?.onStarted) {
          callbacks.onStarted('mock_task_id', 30);
        }
      }, 0);

      setTimeout(() => {
        if (callbacks?.onProgress) {
          callbacks.onProgress({
            percentage: 50,
            status: 'running',  // Changed to 'running' which maps to Generating
            message: 'Processing video...',
          });
        }
      }, 10);

      setTimeout(() => {
        if (callbacks?.onCompleted) {
          callbacks.onCompleted({
            created: Date.now(),
            data: [{
              url: 'https://example.com/video.mp4',
            }],
            model: (request as { model: string }).model,
          });
        }
      }, 20);
      
      // Return promise structure
      return Promise.resolve({
        taskId: 'mock_task_id',
        result: new Promise((resolve) => {
          setTimeout(() => {
            resolve({
              created: Date.now(),
              data: [{
                url: 'https://example.com/video.mp4',
              }],
              model: (request as { model: string }).model,
            });
          }, 30);
        }),
      });
    });
  });

  afterEach(() => {
    // Clean up without restoring all mocks
    jest.clearAllMocks();
  });

  describe('Enhanced video generation with progress tracking', () => {
    it('should delegate progress tracking to the SDK', async () => {
      const hook = renderHook(() => useEnhancedVideoGeneration());

      await act(async () => {
        await hook.result.current.generateVideo({
          prompt: 'Test video with progress',
          settings: {
            model: 'minimax-video',
          },
        });
      });

      // Wait for async callbacks
      await act(async () => {
        await new Promise(resolve => setTimeout(resolve, 50));
      });

      expect(storeMocks.mockAddTask).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: 'Test video with progress',
          status: MediaGenerationStatus.Pending,
          progress: 0,
          id: 'mock_task_id',
        }) as VideoTask
      );

      // Verify the SDK was called correctly
      expect(mockGenerateWithProgress).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: 'Test video with progress',
          model: 'minimax-video',
        }),
        expect.any(Object)
      );

      // Verify progress updates were handled
      expect(storeMocks.mockUpdateTask).toHaveBeenCalledWith(
        'mock_task_id',
        expect.objectContaining({
          progress: 50,
          status: MediaGenerationStatus.Generating,
        })
      );
    });

    it('should handle SDK connection failure gracefully', async () => {
      // Mock SDK to throw an error
      mockGenerateWithProgress.mockRejectedValue(new Error('Connection failed'));

      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      await act(async () => {
        try {
          await hook.result.current.generateVideo({
            prompt: 'SDK failure test',
            settings: {
              model: 'minimax-video',
            },
          });
        } catch {
          // Expected error
        }
      });

      expect(hook.result.current.isGenerating).toBe(false);
      expect(storeMocks.mockSetError).toHaveBeenLastCalledWith(expect.any(Error));
    });

    it('should handle progress callbacks correctly', async () => {
      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      // Track callback invocations
      let startedCalled = false;
      let progressCalled = false;
      let completedCalled = false;

      mockGenerateWithProgress.mockImplementation((request: unknown, callbacks?: VideoProgressCallbacks) => {
        if (callbacks?.onStarted) {
          callbacks.onStarted('test-task-123', 60);
          startedCalled = true;
        }
        if (callbacks?.onProgress) {
          callbacks.onProgress({
            percentage: 75,
            status: 'processing',
            message: 'Rendering video...',
          });
          progressCalled = true;
        }
        if (callbacks?.onCompleted) {
          callbacks.onCompleted({
            created: Date.now(),
            data: [{
              url: 'https://example.com/final-video.mp4',
            }],
            model: 'test-model',
          });
          completedCalled = true;
        }
        
        return Promise.resolve({
          taskId: 'test-task-123',
          result: Promise.resolve({
            created: Date.now(),
            data: [{
              url: 'https://example.com/final-video.mp4',
            }],
            model: 'test-model',
          }),
        });
      });

      await act(async () => {
        await hook.result.current.generateVideo({
          prompt: 'Progress callback test',
          settings: {
            model: 'minimax-video',
          },
        });
      });

      expect(startedCalled).toBe(true);
      expect(progressCalled).toBe(true);
      expect(completedCalled).toBe(true);

      // Verify task was added with correct initial state
      expect(storeMocks.mockAddTask).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 'test-task-123',
          status: MediaGenerationStatus.Pending,
          estimatedTimeToCompletion: 60,
        })
      );

      // Verify progress was updated
      expect(storeMocks.mockUpdateTask).toHaveBeenCalledWith(
        'test-task-123',
        expect.objectContaining({
          progress: 75,
          message: 'Rendering video...',
        })
      );

      // Verify completion was handled
      expect(storeMocks.mockUpdateTask).toHaveBeenCalledWith(
        'test-task-123',
        expect.objectContaining({
          status: MediaGenerationStatus.Completed,
          progress: 100,
        })
      );
    });
  });
});
