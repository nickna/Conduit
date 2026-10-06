/**
 * Model management types for the Admin API
 */

import type { components } from '@/generated/admin-api';

// Re-export ModelType enum from modelType.ts
export { ModelType } from './modelType';

// Model DTOs
export type ModelDto = components['schemas']['ModelDto'];
export type CreateModelDto = components['schemas']['CreateModelDto'];
export type UpdateModelDto = components['schemas']['UpdateModelDto'];
export type ModelProviderAvailabilityDto = components['schemas']['ModelProviderAvailabilityDto'];

// Model Series DTOs
export type ModelSeriesDto = components['schemas']['ModelSeriesDto'];
export type CreateModelSeriesDto = components['schemas']['CreateModelSeriesDto'];
export type UpdateModelSeriesDto = components['schemas']['UpdateModelSeriesDto'];
export type SimpleModelSeriesDto = components['schemas']['SimpleModelSeriesDto'];
export type SeriesSimpleModelDto = components['schemas']['SeriesSimpleModelDto'];

// Model Author DTOs
export type ModelAuthorDto = components['schemas']['ModelAuthorDto'];
export type CreateModelAuthorDto = components['schemas']['CreateModelAuthorDto'];
export type UpdateModelAuthorDto = components['schemas']['UpdateModelAuthorDto'];

// Simplified type aliases for convenience
export type Model = ModelDto;
export type ModelSeries = ModelSeriesDto;
export type ModelAuthor = ModelAuthorDto;
