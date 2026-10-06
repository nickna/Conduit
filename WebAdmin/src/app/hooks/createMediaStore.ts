/**
 * Base media store factory for creating consistent stores across media types
 */

import { StateCreator } from 'zustand';
import { persist, PersistOptions } from 'zustand/middleware';
import { MediaGenerationStatus, RetryHistoryEntry } from '@/app/types/media';
import { STORAGE_CONFIG } from '@/app/config/mediaGeneration';

/**
 * Base media task interface
 */
export interface MediaTask<TResult = unknown> {
  id: string;
  prompt: string;
  status: MediaGenerationStatus;
  progress: number;
  message?: string;
  estimatedTimeToCompletion?: number;
  createdAt: string;
  updatedAt: string;
  result?: TResult;
  error?: string;
  retryCount: number;
  lastRetryAt?: string;
  retryHistory: RetryHistoryEntry[];
}

/**
 * Base media settings interface
 */
export interface MediaSettings {
  model: string;
  [key: string]: unknown;
}

/**
 * Base media store state
 */
export interface BaseMediaState<TTask extends MediaTask, TSettings extends MediaSettings> {
  // UI State
  error: Error | string | null;
  
  // Settings
  settings: TSettings;
  
  // Tasks
  currentTask: TTask | null;
  taskHistory: TTask[];
  
  // Options
  maxHistorySize: number;
  persistHistory: boolean;
}

/**
 * Base media store actions
 */
export interface BaseMediaActions<TTask extends MediaTask, TSettings extends MediaSettings> {
  // Settings actions
  updateSettings: (updates: Partial<TSettings>) => void;
  
  // Error handling
  setError: (error: Error | string | null) => void;
  
  // Task management
  addTask: (task: TTask) => void;
  updateTask: (taskId: string, updates: Partial<TTask>) => void;
  removeTask: (taskId: string) => void;
  clearHistory: () => void;
  
  // Task queries
  getTaskById: (taskId: string) => TTask | undefined;
  getCompletedTasks: () => TTask[];
  getFailedTasks: () => TTask[];
  getPendingTasks: () => TTask[];
}

/**
 * Complete media store type
 */
export type MediaStore<
  TTask extends MediaTask = MediaTask,
  TSettings extends MediaSettings = MediaSettings
> = BaseMediaState<TTask, TSettings> & BaseMediaActions<TTask, TSettings>;

/**
 * Options for creating a media store
 */
export interface CreateMediaStoreOptions<TSettings extends MediaSettings, TTask extends MediaTask = MediaTask> {
  name: string;
  initialSettings: TSettings;
  maxHistorySize?: number;
  persistHistory?: boolean;
  partializeState?: (state: MediaStore<TTask, TSettings>) => Partial<MediaStore<TTask, TSettings>>;
}

/**
 * Creates a base media store with common functionality
 */
export function createMediaStore<
  TTask extends MediaTask = MediaTask,
  TSettings extends MediaSettings = MediaSettings
>(
  options: CreateMediaStoreOptions<TSettings, TTask>
): StateCreator<
  MediaStore<TTask, TSettings>,
  [],
  [['zustand/persist', MediaStore<TTask, TSettings>]]
> {
  const {
    name,
    initialSettings,
    maxHistorySize = STORAGE_CONFIG.MAX_HISTORY_SIZE,
    persistHistory = true,
    partializeState
  } = options;

  const baseStore: StateCreator<MediaStore<TTask, TSettings>> = (set, get) => ({
    // Initial state
    error: null,
    settings: initialSettings,
    currentTask: null,
    taskHistory: [],
    maxHistorySize,
    persistHistory,

    // Settings actions
    updateSettings: (updates: Partial<TSettings>) =>
      set((state) => ({
        settings: { ...state.settings, ...updates },
      })),

    // Error handling
    setError: (error: Error | string | null) => set({ error }),

    // Task management
    addTask: (task: TTask) =>
      set((state) => {
        // Check if task already exists in history
        const existingTaskIndex = state.taskHistory.findIndex(t => t.id === task.id);
        let newHistory;
        
        if (existingTaskIndex !== -1) {
          // Update existing task instead of adding duplicate
          newHistory = [...state.taskHistory];
          newHistory[existingTaskIndex] = task;
        } else {
          // Add new task to the beginning
          newHistory = [task, ...state.taskHistory].slice(0, state.maxHistorySize);
        }
        
        return {
          currentTask: task,
          taskHistory: newHistory,
        };
      }),

    updateTask: (taskId: string, updates: Partial<TTask>) =>
      set((state) => {
        // Check if task exists in history
        const taskExists = state.taskHistory.some(task => task.id === taskId);
        
        let updatedHistory;
        if (taskExists) {
          // Update existing task
          updatedHistory = state.taskHistory.map((task) =>
            task.id === taskId 
              ? { ...task, ...updates, updatedAt: new Date().toISOString() } as TTask
              : task
          );
        } else {
          // Add as new task if it doesn't exist (shouldn't happen normally)
          const newTask = {
            id: taskId,
            prompt: '',
            status: MediaGenerationStatus.Pending,
            progress: 0,
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            retryCount: 0,
            retryHistory: [],
            ...updates,
          } as unknown as TTask;
          updatedHistory = [newTask, ...state.taskHistory].slice(0, state.maxHistorySize);
        }
        
        let updatedCurrent = state.currentTask?.id === taskId
          ? { ...state.currentTask, ...updates, updatedAt: new Date().toISOString() } as TTask
          : state.currentTask;
        
        // Clear currentTask if it's completed, failed, or cancelled
        if (updatedCurrent && ['completed', 'failed', 'cancelled', 'error'].includes(updatedCurrent.status)) {
          updatedCurrent = null;
        }

        return {
          currentTask: updatedCurrent,
          taskHistory: updatedHistory,
        };
      }),

    removeTask: (taskId: string) =>
      set((state) => ({
        currentTask: state.currentTask?.id === taskId ? null : state.currentTask,
        taskHistory: state.taskHistory.filter((task) => task.id !== taskId),
      })),

    clearHistory: () => set({ taskHistory: [], currentTask: null }),

    // Task queries
    getTaskById: (taskId: string) => {
      const state = get();
      return state.taskHistory.find(task => task.id === taskId);
    },

    getCompletedTasks: () => {
      const state = get();
      return state.taskHistory.filter(task => task.status === MediaGenerationStatus.Completed);
    },

    getFailedTasks: () => {
      const state = get();
      return state.taskHistory.filter(task => task.status === MediaGenerationStatus.Failed);
    },

    getPendingTasks: () => {
      const state = get();
      return state.taskHistory.filter(task => 
        task.status === MediaGenerationStatus.Pending || task.status === MediaGenerationStatus.Generating
      );
    },
  });

  // If persistence is enabled, wrap with persist middleware
  if (persistHistory) {
    const persistOptions: PersistOptions<MediaStore<TTask, TSettings>, Partial<MediaStore<TTask, TSettings>>> = {
      name,
      partialize: partializeState ?? ((state: MediaStore<TTask, TSettings>) => ({
        settings: state.settings,
        taskHistory: state.taskHistory.filter(
          (task) => task.status === MediaGenerationStatus.Completed || task.status === MediaGenerationStatus.Failed
        ).slice(0, state.maxHistorySize),
      })),
    };

    return persist(baseStore, persistOptions) as StateCreator<
      MediaStore<TTask, TSettings>,
      [],
      [['zustand/persist', MediaStore<TTask, TSettings>]]
    >;
  }

  return baseStore as StateCreator<
    MediaStore<TTask, TSettings>,
    [],
    [['zustand/persist', MediaStore<TTask, TSettings>]]
  >;
}
