import type { MediaTask, MediaSettings } from '@/app/hooks/createMediaStore';
import type { MediaData } from '@/app/types/media';

export type ImageData = MediaData;

export interface ImageGenerationResponse {
  created: number;
  data: ImageData[];
}

export type ImageGenerationSettings = MediaSettings;

export interface GeneratedImage extends ImageData {
  id?: string;
  width?: number;
  height?: number;
  sizeBytes?: number;
  format?: string;
}

export interface ImageTask extends MediaTask<ImageGenerationResponse> {
  settings: ImageGenerationSettings;
}
