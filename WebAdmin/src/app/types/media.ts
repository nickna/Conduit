/**
 * Shared type definitions for media generation features (images, videos, etc.)
 */

/**
 * Unified status enum for media generation tasks
 * Simplified from the original GenerationStatus to provide consistency
 */
export enum MediaGenerationStatus {
  /** No generation in progress or initial state */
  Idle = 'idle',
  /** Task is queued and waiting to start */
  Pending = 'pending',
  /** Task is actively generating media */
  Generating = 'generating',
  /** Task completed successfully */
  Completed = 'completed',
  /** Task failed with an error */
  Failed = 'failed',
  /** Task was cancelled by user */
  Cancelled = 'cancelled'
}

/**
 * Helper to check if a status indicates the task is still active
 */
export const isActiveStatus = (status: MediaGenerationStatus): boolean => {
  return status === MediaGenerationStatus.Pending || status === MediaGenerationStatus.Generating;
};

/**
 * Helper to check if a status is terminal (no more changes expected)
 */
/**
 * Map Gateway task status values to the UI's unified generation status.
 */
export const mapGenerationStatus = (status: string): MediaGenerationStatus => {
  switch (status) {
    case 'idle':
      return MediaGenerationStatus.Idle;
    case 'pending':
      return MediaGenerationStatus.Pending;
    case 'running':
    case 'generating':
      return MediaGenerationStatus.Generating;
    case 'completed':
      return MediaGenerationStatus.Completed;
    case 'failed':
    case 'error':
    case 'timedout':
      return MediaGenerationStatus.Failed;
    case 'cancelled':
      return MediaGenerationStatus.Cancelled;
    default:
      return MediaGenerationStatus.Idle;
  }
};

/**
 * Common metadata interface for generated media
 */
export interface MediaMetadata {
  duration?: number;
  resolution?: string;
  width?: number;
  height?: number;
  fps?: number;
  file_size_bytes?: number;
  sizeBytes?: number;
  format?: string;
  codec?: string;
  audio_codec?: string;
  bitrate?: number;
  mime_type?: string;
  seed?: number;
}

/**
 * Common usage tracking for API calls
 */
export interface MediaUsage {
  prompt_tokens: number;
  total_tokens: number;
  duration_seconds?: number;
  processing_time_seconds?: number;
}

/**
 * Common data structure for generated media
 */
export interface MediaData {
  url?: string;
  b64_json?: string;
  revised_prompt?: string;
}

/**
 * Retry history entry for failed tasks
 */
export interface RetryHistoryEntry {
  attemptNumber: number;
  timestamp: string;
  error: string;
}

/**
 * Common response format options
 */
export type ResponseFormat = 'url' | 'b64_json';

/**
 * Common quality settings
 */
export type Quality = 'standard' | 'hd';

/**
 * Common style settings
 */
export type Style = 'vivid' | 'natural';
