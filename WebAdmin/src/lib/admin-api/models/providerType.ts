import type { components } from '@/generated/admin-api';

/** Provider values as serialized by the Admin API. Regeneration extends this union automatically. */
export type ProviderType = components['schemas']['ProviderType'];

/**
 * Named constants used by provider-specific behavior.
 *
 * This object is intentionally not the provider catalog and is not enumerated to build provider
 * choices. New providers flow from the backend schema without adding a constant here; add one only
 * when the frontend gains behavior specific to that provider.
 */
export const ProviderType = {
  Unknown: 'unknown',
  OpenAI: 'openAI',
  Groq: 'groq',
  Replicate: 'replicate',
  Fireworks: 'fireworks',
  OpenAICompatible: 'openAICompatible',
  MiniMax: 'miniMax',
  Ultravox: 'ultravox',
  ElevenLabs: 'elevenLabs',
  Cerebras: 'cerebras',
  SambaNova: 'sambaNova',
  DeepInfra: 'deepInfra',
  Cloudflare: 'cloudflare',
  OpenRouter: 'openRouter',
  Meta: 'meta',
  Azure: 'azure',
  Bedrock: 'bedrock',
  Vertex: 'vertex',
} as const satisfies Record<string, ProviderType>;
