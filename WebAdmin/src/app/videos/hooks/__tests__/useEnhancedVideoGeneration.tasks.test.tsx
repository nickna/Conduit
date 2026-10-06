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

describe('useEnhancedVideoGeneration - Task Management', () => {
  let storeMocks: ReturnType<typeof setupMocks>;
  let mockClient: MockVideoClient;
  let mockGenerateWithProgress: jest.Mock;
  let mockCancelTask: jest.Mock;

  beforeEach(() => {
    jest.clearAllMocks();
    storeMocks = setupMocks();

    // Setup mock client
    mockGenerateWithProgress = jest.fn();
    mockCancelTask = jest.fn().mockResolvedValue(undefined);
    mockClient = {
      videos: {
        generateWithProgress: mockGenerateWithProgress,
        cancelTask: mockCancelTask,
      },
    };

    // Mock the getBrowserGatewayClient function
    (browserClientModule.getBrowserGatewayClient as jest.Mock).mockResolvedValue(mockClient);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  describe('Task management', () => {
    it('should generate unique task IDs', async () => {
      // Mock different task IDs for each call
      mockGenerateWithProgress
        .mockImplementationOnce((request: unknown, callbacks?: VideoProgressCallbacks) => {
          if (callbacks?.onStarted) {
            callbacks.onStarted('task_unique_123', 30);
          }
          return Promise.resolve({
            taskId: 'task_unique_123',
            result: Promise.resolve({
              created: Date.now(),
              data: [{
                url: 'https://example.com/video1.mp4',
              }],
              model: 'minimax-video',
            }),
          });
        })
        .mockImplementationOnce((request: unknown, callbacks?: VideoProgressCallbacks) => {
          if (callbacks?.onStarted) {
            callbacks.onStarted('task_unique_456', 30);
          }
          return Promise.resolve({
            taskId: 'task_unique_456',
            result: Promise.resolve({
              created: Date.now(),
              data: [{
                url: 'https://example.com/video2.mp4',
              }],
              model: 'minimax-video',
            }),
          });
        });

      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      await act(async () => {
        await hook.result.current.generateVideo({
          prompt: 'First video',
          settings: {
            model: 'minimax-video',
          },
        });
      });

      await act(async () => {
        await hook.result.current.generateVideo({
          prompt: 'Second video',
          settings: {
            model: 'minimax-video',
          },
        });
      });

      const firstCall = storeMocks.mockAddTask.mock.calls[0] as [VideoTask] | undefined;
      const firstTaskCall = firstCall?.[0];
      
      const secondCall = storeMocks.mockAddTask.mock.calls[1] as [VideoTask] | undefined;
      const secondTaskCall = secondCall?.[0];
      
      expect(firstTaskCall?.id).toBe('task_unique_123');
      expect(secondTaskCall?.id).toBe('task_unique_456');
      expect(firstTaskCall?.id).not.toBe(secondTaskCall?.id);
    });

    it('should handle cancellation', async () => {
      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      await act(async () => {
        await hook.result.current.cancelGeneration('task_cancel_123');
      });

      expect(storeMocks.mockUpdateTask).toHaveBeenCalledWith(
        'task_cancel_123',
        expect.objectContaining({
          status: MediaGenerationStatus.Cancelled,
        }) as Partial<VideoTask>
      );

      expect(mockCancelTask).toHaveBeenCalledWith('task_cancel_123');
    });

    it('should handle retry functionality', async () => {
      // Setup initial failed task
      const failedTask: VideoTask = {
        id: 'task_retry_789',
        prompt: 'Retry test video',
        status: MediaGenerationStatus.Failed,
        progress: 0,
        error: 'Previous failure',
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        settings: {
          model: 'minimax-video',
        },
        retryCount: 1,
        retryHistory: [{
          attemptNumber: 1,
          timestamp: new Date().toISOString(),
          error: 'Initial failure',
        }],
      };

      mockGenerateWithProgress.mockImplementation((request: unknown, callbacks?: VideoProgressCallbacks) => {
        if (callbacks?.onStarted) {
          callbacks.onStarted('task_retry_new', 30);
        }
        return Promise.resolve({
          taskId: 'task_retry_new',
          result: Promise.resolve({
            created: Date.now(),
            data: [{
              url: 'https://example.com/retry-video.mp4',
            }],
            model: 'minimax-video',
          }),
        });
      });

      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      await act(async () => {
        await hook.result.current.retryGeneration(failedTask);
      });

      // Verify retry history was updated
      expect(storeMocks.mockUpdateTask).toHaveBeenCalledWith(
        'task_retry_789',
        expect.objectContaining({
          status: MediaGenerationStatus.Pending,
          retryCount: 2,
          retryHistory: expect.arrayContaining([
            expect.objectContaining({
              attemptNumber: 1,
            }) as object,
            expect.objectContaining({
              attemptNumber: 2,
            }) as object,
          ]) as unknown[],
        })
      );

      // Verify new generation was triggered
      expect(mockGenerateWithProgress).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: 'Retry test video',
          model: 'minimax-video',
        }),
        expect.any(Object)
      );
    });

    it('should track multiple concurrent tasks', async () => {
      let taskCounter = 0;
      mockGenerateWithProgress.mockImplementation((request: unknown, callbacks?: VideoProgressCallbacks) => {
        const taskId = `concurrent_task_${++taskCounter}`;
        if (callbacks?.onStarted) {
          callbacks.onStarted(taskId, 30);
        }
        
        // Simulate async completion
        setTimeout(() => {
          if (callbacks?.onCompleted) {
            callbacks.onCompleted({
              created: Date.now(),
              data: [{
                url: `https://example.com/video${taskCounter}.mp4`,
              }],
              model: 'minimax-video',
            });
          }
        }, 100 * taskCounter);

        return Promise.resolve({
          taskId,
          result: new Promise(resolve => {
            setTimeout(() => {
              resolve({
                created: Date.now(),
                data: [{
                  url: `https://example.com/video${taskCounter}.mp4`,
                }],
                model: 'minimax-video',
              });
            }, 100 * taskCounter);
          }),
        });
      });

      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      // Start multiple concurrent generations
      await act(async () => {
        // Don't await these - let them run concurrently
        const promises = [
          hook.result.current.generateVideo({
            prompt: 'Concurrent video 1',
            settings: {
              model: 'minimax-video',
            },
          }),
          hook.result.current.generateVideo({
            prompt: 'Concurrent video 2',
            settings: {
              model: 'minimax-video',
            },
          }),
        ];

        // Wait for all to complete
        await Promise.all(promises);
      });

      // Verify both tasks were added
      expect(storeMocks.mockAddTask).toHaveBeenCalledTimes(2);
      expect(storeMocks.mockAddTask).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 'concurrent_task_1',
          prompt: 'Concurrent video 1',
        })
      );
      expect(storeMocks.mockAddTask).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 'concurrent_task_2',
          prompt: 'Concurrent video 2',
        })
      );
    });
  });
});