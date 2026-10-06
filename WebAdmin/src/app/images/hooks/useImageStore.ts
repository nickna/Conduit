import { create } from 'zustand';
import { createMediaStore, type MediaStore } from '@/app/hooks/createMediaStore';
import { 
  ImageTask, 
  ImageGenerationSettings,
  ImageGenerationResponse,
  GeneratedImage
} from '../types';
import { MediaGenerationStatus } from '@/app/types/media';
import { STORAGE_CONFIG, IMAGE_CONFIG } from '@/app/config/mediaGeneration';
import { 
  createToastErrorHandler, 
  shouldShowBalanceWarning
} from '@/lib/gateway-api';
// Needs raw notifications API: .show is passed as callback to SDK's createToastErrorHandler
import { notifications } from '@mantine/notifications';
import {
  ConduitError,
  InsufficientBalanceError,
  ValidationError,
} from '@/lib/conduit-common';

const LOCAL_STORAGE_KEY = 'conduit-image-generation';

// Create the base store configuration
const imageStoreConfig = createMediaStore<ImageTask, ImageGenerationSettings>({
  name: LOCAL_STORAGE_KEY,
  initialSettings: {
    model: '',
  },
  persistHistory: true,
  partializeState: (state) => ({
    settings: state.settings,
    taskHistory: state.taskHistory.filter(
      (task) => task.status === MediaGenerationStatus.Completed || task.status === MediaGenerationStatus.Failed
    ).slice(0, STORAGE_CONFIG.IMAGE_HISTORY_LIMIT), // Keep only last N completed/failed images in storage
  }),
});

// Extend with image-specific state and actions
interface ImageStoreExtensions {
  // Additional state
  prompt: string;
  status: MediaGenerationStatus;
  currentResults: GeneratedImage[];
  settingsVisible: boolean;
  
  // Additional actions
  setPrompt: (prompt: string) => void;
  generateImages: (dynamicParameters?: Record<string, unknown>) => Promise<void>;
  clearResults: () => void;
  toggleSettings: () => void;
  getLatestResults: () => GeneratedImage[];
}

// Complete store type
export type ImageStore = MediaStore<ImageTask, ImageGenerationSettings> & ImageStoreExtensions;

// Create the actual store with extensions
export const useImageStore = create<ImageStore>()((set, get, api) => ({
  // Base store functionality
  ...imageStoreConfig(set, get, api),
  
  // Additional state
  prompt: '',
  status: MediaGenerationStatus.Idle,
  currentResults: [],
  settingsVisible: false,
  
  // Additional actions
  setPrompt: (prompt: string) => set({ prompt }),
  
  generateImages: async (dynamicParameters?: Record<string, unknown>) => {
    const state = get();
    const { prompt, settings } = state;
    
    if (!prompt.trim()) {
      set({
        error: new ValidationError('Please enter a prompt for image generation'),
        status: MediaGenerationStatus.Failed,
      });
      return;
    }

    if (!settings.model) {
      set({
        error: new ValidationError('Please select a model for image generation'),
        status: MediaGenerationStatus.Failed,
      });
      return;
    }

    // Create a new task
    const taskId = `img-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
    const newTask: ImageTask = {
      id: taskId,
      prompt,
      status: MediaGenerationStatus.Generating,
      progress: 0,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      settings,
      retryCount: 0,
      retryHistory: [],
    };

    // Add task to history and set as current
    state.addTask(newTask);
    set({ status: MediaGenerationStatus.Generating, currentResults: [], error: null });

    // Create error handler
    const handleError = createToastErrorHandler(notifications.show);

    try {
      // Get SDK client and use it directly
      const { getBrowserGatewayClient } = await import('@/lib/client/browserGatewayClient');
      const client = await getBrowserGatewayClient();
      
      // Use SDK to generate image with hardcoded defaults
      const result = await client.images.generate({
        prompt,
        model: settings.model,
        n: IMAGE_CONFIG.DEFAULTS.N,
        response_format: 'url',  // Always use URL for CDN storage
        // Include dynamic parameters if provided (overrides defaults)
        ...dynamicParameters,
      });

      // Update task with results
      state.updateTask(taskId, {
        status: MediaGenerationStatus.Completed,
        progress: 100,
        result: result as ImageGenerationResponse,
      });

      set({ 
        status: MediaGenerationStatus.Completed, 
        currentResults: result.data,
        error: null 
      });
    } catch (error) {
      // Use enhanced error handler with toast notifications
      const errorMessage = handleError(error, 'generate images');
      
      // Update task with error
      state.updateTask(taskId, {
        status: MediaGenerationStatus.Failed,
        error: errorMessage,
      });
      
      const displayError = error instanceof Error
        ? error
        : new ConduitError(errorMessage, 500, 'IMAGE_GENERATION_ERROR');
      set({
        status: MediaGenerationStatus.Failed, 
        error: displayError,
        currentResults: []
      });
      
      // Special handling for balance errors
      if (shouldShowBalanceWarning(error)) {
        set({ 
          error: new InsufficientBalanceError(
            'Please add credits to your account to generate images.',
          ),
        });
      }
    }
  },

  clearResults: () => set({ 
    currentResults: [], 
    status: MediaGenerationStatus.Idle, 
    error: null,
    currentTask: null 
  }),

  toggleSettings: () => set((state) => ({ settingsVisible: !state.settingsVisible })),

  getLatestResults: () => {
    const state = get();
    // Return current results if available, otherwise get from latest completed task
    if (state.currentResults.length > 0) {
      return state.currentResults;
    }
    
    const latestCompleted = state.taskHistory
      .filter(task => task.status === MediaGenerationStatus.Completed && task.result)
      .sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime())[0];
    
    return latestCompleted?.result?.data ?? [];
  },
}));
