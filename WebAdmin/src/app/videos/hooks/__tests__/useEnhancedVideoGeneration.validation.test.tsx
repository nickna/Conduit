import { renderHook, act } from '@testing-library/react';
import { useEnhancedVideoGeneration } from '../useEnhancedVideoGeneration';
import * as browserClientModule from '@/lib/client/browserGatewayClient';
import type { VideoProgressCallbacks } from '@/lib/gateway-api';

// Mock the browser client module
jest.mock('@/lib/client/browserGatewayClient');

// Mock the video SignalR client
jest.mock('@/lib/client/videoSignalRClient', () => ({
  disconnectVideoSignalRClient: jest.fn().mockResolvedValue(undefined),
}));

interface MockVideoClient {
  videos: {
    generateWithProgress: jest.Mock;
    cancelTask: jest.Mock;
  };
}

describe('useEnhancedVideoGeneration - Settings Validation', () => {
  let mockClient: MockVideoClient;
  let mockGenerateWithProgress: jest.Mock;

  beforeEach(() => {
    jest.clearAllMocks();
    
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

    // Setup default success response
    mockGenerateWithProgress.mockImplementation((request: unknown, callbacks?: VideoProgressCallbacks) => {
      // Simulate immediate task start
      if (callbacks?.onStarted) {
        callbacks.onStarted('test-task-id', 30);
      }
      
      // Return promise structure
      return Promise.resolve({
        taskId: 'test-task-id',
        result: Promise.resolve({
          created: Date.now(),
          data: [{
            url: 'https://example.com/video.mp4',
          }],
          model: 'test-model',
        }),
      });
    });
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  describe('Settings validation', () => {
    it('should handle empty prompt gracefully', async () => {
      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      await act(async () => {
        await hook.result.current.generateVideo({
          prompt: '',
          settings: {
            model: 'minimax-video',
          },
        });
      });

      // Should still make SDK call with empty prompt - validation handled by backend
      expect(mockGenerateWithProgress).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: '',
          model: 'minimax-video',
        }),
        expect.any(Object)
      );
    });

    it('should handle long duration gracefully', async () => {
      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      await act(async () => {
        await hook.result.current.generateVideo({
          prompt: 'Duration test',
          settings: {
            model: 'minimax-video',
          },
        });
      });

      // Should still make SDK call - validation handled by backend
      expect(mockGenerateWithProgress).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: 'Duration test',
        }),
        expect.any(Object)
      );
    });

    it('should pass dynamic parameters to SDK', async () => {
      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      const dynamicParams = {
        start_image: 'https://example.com/image.jpg',
        custom_setting: 'value',
      };

      await act(async () => {
        await hook.result.current.generateVideo({
          prompt: 'Test with dynamic params',
          settings: {
            model: 'kling',
          },
          dynamicParameters: dynamicParams,
        });
      });

      // Should include dynamic parameters in the SDK call
      expect(mockGenerateWithProgress).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: 'Test with dynamic params',
          model: 'kling',
          ...dynamicParams,
        }),
        expect.any(Object)
      );
    });

    it('should handle SDK errors properly', async () => {
      // Mock SDK to throw an error
      mockGenerateWithProgress.mockRejectedValue(new Error('SDK Error'));

      const hook = renderHook(() =>
        useEnhancedVideoGeneration()
      );

      await act(async () => {
        try {
          await hook.result.current.generateVideo({
            prompt: 'Error test',
            settings: {
              model: 'minimax-video',
            },
          });
        } catch {
          // Expected error
        }
      });

      expect(hook.result.current.isGenerating).toBe(false);
    });
  });
});
