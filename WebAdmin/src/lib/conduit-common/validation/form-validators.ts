/**
 * Composable form validation functions
 *
 * Each validator returns `null` on success or an error message string on failure.
 * These are framework-agnostic and work with any form library.
 */

import { isValidIPv4, isValidCIDR } from "./type-guards";

export const validators = {
  required: (fieldName: string) => (value: string | undefined) =>
    !value?.trim() ? `${fieldName} is required` : null,

  minLength: (fieldName: string, min: number) => (value: string | undefined) =>
    (value?.length ?? 0) < min
      ? `${fieldName} must be at least ${min} characters`
      : null,

  maxLength: (fieldName: string, max: number) => (value: string | undefined) =>
    (value?.length ?? 0) > max
      ? `${fieldName} must be no more than ${max} characters`
      : null,

  positiveNumber: (fieldName: string) => (value: number | undefined) =>
    value !== undefined && value < 0 ? `${fieldName} must be positive` : null,

  url: (value: string | undefined) => {
    if (!value?.trim()) return null;
    try {
      new URL(value);
      return null;
    } catch {
      return "Must be a valid URL";
    }
  },

  email: (value: string | undefined) => {
    if (!value?.trim()) return null;
    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    return emailRegex.test(value) ? null : "Must be a valid email address";
  },

  jsonObject: (fieldName: string) => (value: string | undefined) => {
    if (!value?.trim()) return null;
    try {
      const parsed = JSON.parse(value) as unknown;
      return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
        ? null
        : `${fieldName} must be a JSON object`;
    } catch {
      return `${fieldName} must be valid JSON`;
    }
  },

  minValue: (fieldName: string, min: number) => (value: number | undefined) =>
    value !== undefined && value < min
      ? `${fieldName} must be at least ${min}`
      : null,

  ipAddresses: (value: string[] | undefined) => {
    if (!value || value.length === 0) return null;

    for (const ip of value) {
      if (!isValidIPv4(ip) && !isValidCIDR(ip)) {
        return `Invalid IP address or CIDR: ${ip}`;
      }
    }
    return null;
  },

  arrayMinLength:
    (fieldName: string, min: number) => (value: unknown[] | undefined) =>
      !value || value.length < min
        ? `At least ${min} ${fieldName} must be selected`
        : null,
};

/**
 * Pre-configured validation combinations for common Conduit domain objects.
 */
export const commonValidations = {
  name: {
    validate: validators.required("Name"),
  },

  nameWithLength: (min = 3, max = 100) => ({
    validate: {
      required: validators.required("Name"),
      minLength: validators.minLength("Name", min),
      maxLength: validators.maxLength("Name", max),
    },
  }),

  description: {
    validate: validators.maxLength("Description", 500),
  },

  apiKey: {
    validate: validators.required("API Key"),
  },

  budget: {
    validate: validators.positiveNumber("Budget"),
  },

  rateLimit: {
    validate: validators.minValue("Rate limit", 1),
  },

  virtualKeyName: {
    validate: {
      required: validators.required("Key name"),
      minLength: validators.minLength("Key name", 3),
      maxLength: validators.maxLength("Key name", 100),
    },
  },

  allowedModels: {
    validate: validators.arrayMinLength("model", 1),
  },

  allowedEndpoints: {
    validate: validators.arrayMinLength("endpoint", 1),
  },

  ipAddresses: {
    validate: validators.ipAddresses,
  },
};
