/**
 * Abstract base API client providing common HTTP functionality
 *
 * API-specific clients extend this class and implement:
 * - getAuthHeaders(): Returns authentication headers
 * - getDefaultRetryStrategy(): Returns default retry strategy
 *
 * Template methods that can be overridden:
 * - handleErrorResponse(): API-specific error parsing
 * - shouldRetry(): API-specific retry logic
 * - getRetryDelay(): API-specific delay calculation
 */

import type { BaseApiClientConfig } from "./base-client-config";
import type {
  Logger,
  CacheProvider,
  RequestConfigInfo,
  ResponseInfo,
} from "./types";
import {
  calculateRetryDelay,
  getMaxRetries,
  type RetryStrategy,
} from "./retry-strategy";
import { ResponseParser } from "../http/parser";
import { HttpMethod, type ExtendedRequestInit } from "../http/types";
import { HTTP_HEADERS, CONTENT_TYPES } from "../http/constants";
import { ConduitError } from "../errors";

/**
 * Request options for individual requests
 */
export interface BaseRequestOptions {
  /** Additional headers for this request */
  headers?: Record<string, string>;
  /** AbortSignal for request cancellation */
  signal?: AbortSignal;
  /** Request timeout in milliseconds (overrides client default) */
  timeout?: number;
  /** Expected response type */
  responseType?: "json" | "text" | "blob" | "arraybuffer";
}

/**
 * Abstract base API client providing common HTTP functionality
 *
 * Both repository-local Gateway and Admin clients extend this class.
 */
export abstract class BaseApiClient {
  /** Base URL for all requests (without trailing slash) */
  protected readonly baseUrl: string;
  /** Default timeout in milliseconds */
  protected readonly timeout: number;
  /** Default headers included with all requests */
  protected readonly defaultHeaders: Record<string, string>;
  /** Retry strategy configuration */
  protected readonly retryStrategy: RetryStrategy;
  /** Enable debug logging */
  protected readonly debug: boolean;

  // Lifecycle callbacks
  protected readonly onError?: (error: Error) => void;
  protected readonly onRequest?: (
    config: RequestConfigInfo,
  ) => void | Promise<void>;
  protected readonly onResponse?: (
    response: ResponseInfo,
  ) => void | Promise<void>;

  // Optional providers used by composed API clients.
  protected readonly logger?: Logger;
  protected readonly cache?: CacheProvider;

  constructor(config: BaseApiClientConfig) {
    this.baseUrl = config.baseUrl.replace(/\/$/, "");
    this.timeout = config.timeout ?? 60000;
    this.defaultHeaders = config.defaultHeaders ?? {};
    this.retryStrategy = config.retryStrategy ?? this.getDefaultRetryStrategy();
    this.debug = config.debug ?? false;

    this.onError = config.onError;
    this.onRequest = config.onRequest;
    this.onResponse = config.onResponse;
    this.logger = config.logger;
    this.cache = config.cache;
  }

  // ============================================================================
  // Abstract Methods - Must be implemented by API-specific clients
  // ============================================================================

  /**
   * Returns authentication headers for this client
   *
   * Gateway clients return: { Authorization: 'Bearer ...' }
   * The Admin client returns: { 'X-Master-Key': '...' }
   */
  protected abstract getAuthHeaders(): Record<string, string>;

  /**
   * Returns the default retry strategy for this client
   *
   * Gateway requests use exponential backoff with jitter.
   * The Admin client uses a fixed delay
   */
  protected abstract getDefaultRetryStrategy(): RetryStrategy;

  // ============================================================================
  // Template Methods - Can be overridden by API-specific clients
  // ============================================================================

  /**
   * Transform error response into appropriate error type
   * Subclasses can override for API-specific error handling
   *
   * @param response - The failed Response object
   * @returns An Error to throw
   */
  protected async handleErrorResponse(response: Response): Promise<Error> {
    let errorData: unknown;
    try {
      const contentType = response.headers.get("content-type");
      if (contentType?.includes("application/json")) {
        errorData = await response.json();
      }
    } catch {
      errorData = {};
    }

    // Default implementation - subclasses can override for richer error handling
    return new ConduitError(
      `HTTP ${response.status}: ${response.statusText}`,
      response.status,
      `HTTP_${response.status}`,
      { data: errorData },
    );
  }

  /**
   * Determine if an error should be retried
   * Subclasses can override for API-specific retry logic
   *
   * @param error - The error that occurred
   * @param attempt - Current attempt number (1-based)
   * @returns Whether to retry the request
   */
  protected shouldRetry(error: unknown, attempt: number): boolean {
    const maxRetries = getMaxRetries(this.retryStrategy);
    if (attempt > maxRetries) return false;

    // Check custom retry condition if provided
    if (this.retryStrategy.retryCondition) {
      return this.retryStrategy.retryCondition(error);
    }

    // Default retry logic
    if (error instanceof ConduitError) {
      // Retry rate limits and server errors
      return error.statusCode === 429 || error.statusCode >= 500;
    }

    if (error instanceof Error) {
      // Network errors are retryable
      return (
        error.name === "AbortError" ||
        error.message.includes("network") ||
        error.message.includes("fetch")
      );
    }

    return false;
  }

  /**
   * Calculate delay for a retry attempt
   * Subclasses can override for special cases (e.g., retry-after headers)
   *
   * @param error - The error that triggered the retry
   * @param attempt - Current attempt number (1-based)
   * @returns Delay in milliseconds before next retry
   */
  protected getRetryDelay(_error: unknown, attempt: number): number {
    return calculateRetryDelay(this.retryStrategy, attempt);
  }

  // ============================================================================
  // HTTP Methods
  // ============================================================================

  /**
   * Main request method with retry logic
   */
  protected async request<TResponse = unknown, TRequest = unknown>(
    url: string,
    options: BaseRequestOptions & { method?: HttpMethod; body?: TRequest } = {},
  ): Promise<TResponse> {
    const fullUrl = this.buildUrl(url);
    const controller = new AbortController();

    const timeoutMs = options.timeout ?? this.timeout;
    const timeoutId = setTimeout(() => controller.abort(), timeoutMs);

    try {
      const requestInfo: RequestConfigInfo = {
        method: options.method ?? HttpMethod.GET,
        url: fullUrl,
        headers: this.buildHeaders(options.headers),
        data: options.body,
      };

      // Call onRequest hook if provided
      if (this.onRequest) {
        await this.onRequest(requestInfo);
      }

      this.log(
        "debug",
        `API Request: ${requestInfo.method} ${requestInfo.url}`,
      );

      const response = await this.executeWithRetry<TResponse>(fullUrl, {
        method: requestInfo.method,
        headers: requestInfo.headers,
        body: options.body ? JSON.stringify(options.body) : undefined,
        signal: options.signal ?? controller.signal,
        responseType: options.responseType,
        timeout: timeoutMs,
      });

      return response;
    } finally {
      clearTimeout(timeoutId);
    }
  }

  /**
   * Type-safe GET request
   */
  protected async get<TResponse = unknown>(
    url: string,
    options?: BaseRequestOptions,
  ): Promise<TResponse> {
    return this.request<TResponse>(url, { ...options, method: HttpMethod.GET });
  }

  /**
   * Type-safe POST request
   */
  protected async post<TResponse = unknown, TRequest = unknown>(
    url: string,
    data?: TRequest,
    options?: BaseRequestOptions,
  ): Promise<TResponse> {
    return this.request<TResponse, TRequest>(url, {
      ...options,
      method: HttpMethod.POST,
      body: data,
    });
  }

  /**
   * Type-safe PUT request
   */
  protected async put<TResponse = unknown, TRequest = unknown>(
    url: string,
    data?: TRequest,
    options?: BaseRequestOptions,
  ): Promise<TResponse> {
    return this.request<TResponse, TRequest>(url, {
      ...options,
      method: HttpMethod.PUT,
      body: data,
    });
  }

  /**
   * Type-safe PATCH request
   */
  protected async patch<TResponse = unknown, TRequest = unknown>(
    url: string,
    data?: TRequest,
    options?: BaseRequestOptions,
  ): Promise<TResponse> {
    return this.request<TResponse, TRequest>(url, {
      ...options,
      method: HttpMethod.PATCH,
      body: data,
    });
  }

  /**
   * Type-safe DELETE request
   */
  protected async delete<TResponse = unknown>(
    url: string,
    options?: BaseRequestOptions,
  ): Promise<TResponse> {
    return this.request<TResponse>(url, {
      ...options,
      method: HttpMethod.DELETE,
    });
  }

  // ============================================================================
  // Internal Methods
  // ============================================================================

  /**
   * Execute request with retry logic
   */
  private async executeWithRetry<TResponse>(
    url: string,
    init: ExtendedRequestInit,
    attempt: number = 1,
  ): Promise<TResponse> {
    try {
      const response = await fetch(url, ResponseParser.cleanRequestInit(init));

      this.log(
        "debug",
        `API Response: ${response.status} ${response.statusText}`,
      );

      // Build response info for callback
      const headers: Record<string, string> = {};
      response.headers.forEach((value, key) => {
        headers[key] = value;
      });

      // Call onResponse hook if provided
      if (this.onResponse) {
        const responseInfo: ResponseInfo = {
          status: response.status,
          statusText: response.statusText,
          headers,
          data: undefined,
          config: {
            url,
            method: (init.method as string) ?? HttpMethod.GET,
            headers: (init.headers as Record<string, string>) ?? {},
          },
        };
        await this.onResponse(responseInfo);
      }

      if (!response.ok) {
        const error = await this.handleErrorResponse(response);
        throw error;
      }

      // Handle empty responses
      const contentLength = response.headers.get("content-length");
      if (contentLength === "0" || response.status === 204) {
        return undefined as TResponse;
      }

      return await ResponseParser.parse<TResponse>(response, init.responseType);
    } catch (error) {
      if (this.shouldRetry(error, attempt)) {
        const delay = this.getRetryDelay(error, attempt);
        this.log(
          "debug",
          `Retrying request (attempt ${attempt + 1}) after ${delay}ms`,
        );

        await this.sleep(delay);
        return this.executeWithRetry<TResponse>(url, init, attempt + 1);
      }

      // Call error handler and rethrow
      if (this.onError && error instanceof Error) {
        this.onError(error);
      }
      throw error;
    }
  }

  /**
   * Build full URL from path
   */
  protected buildUrl(path: string): string {
    // If path is already a full URL, return it
    if (path.startsWith("http://") || path.startsWith("https://")) {
      return path;
    }

    // Ensure path starts with /
    const cleanPath = path.startsWith("/") ? path : `/${path}`;
    return `${this.baseUrl}${cleanPath}`;
  }

  /**
   * Build headers including auth, defaults, and additional headers
   */
  protected buildHeaders(
    additionalHeaders?: Record<string, string>,
  ): Record<string, string> {
    return {
      [HTTP_HEADERS.CONTENT_TYPE]: CONTENT_TYPES.JSON,
      ...this.getAuthHeaders(),
      ...this.defaultHeaders,
      ...additionalHeaders,
    };
  }

  /**
   * Log a message using the configured logger or console in debug mode
   */
  protected log(
    level: "debug" | "info" | "warn" | "error",
    message: string,
    ...args: unknown[]
  ): void {
    if (this.logger?.[level]) {
      this.logger[level](message, ...args);
    } else if (this.debug && level === "debug") {
      console.warn(`[API] ${message}`, ...args);
    }
  }

  /**
   * Sleep for a specified duration
   */
  protected sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // ============================================================================
  // Caching Utilities (Optional - only active if cache provider is configured)
  // ============================================================================

  /**
   * Get a value from cache
   * Returns null if cache is not configured or key is not found
   */
  protected async getFromCache<T>(key: string): Promise<T | null> {
    if (!this.cache) return null;

    try {
      const cached = await this.cache.get<T>(key);
      if (cached) {
        this.log("debug", `Cache hit for key: ${key}`);
        return cached;
      }
    } catch (error) {
      this.log("error", "Cache get error:", error);
    }

    return null;
  }

  /**
   * Set a value in cache
   * No-op if cache is not configured
   */
  protected async setCache(
    key: string,
    value: unknown,
    ttl?: number,
  ): Promise<void> {
    if (!this.cache) return;

    try {
      await this.cache.set(key, value, ttl);
      this.log("debug", `Cache set for key: ${key}`);
    } catch (error) {
      this.log("error", "Cache set error:", error);
    }
  }

  /**
   * Execute a function with caching
   * Returns cached value if available, otherwise executes function and caches result
   */
  public async withCache<T>(
    cacheKey: string,
    fn: () => Promise<T>,
    ttl?: number,
  ): Promise<T> {
    const cached = await this.getFromCache<T>(cacheKey);
    if (cached !== null) {
      return cached;
    }

    const result = await fn();
    await this.setCache(cacheKey, result, ttl);

    return result;
  }

  /**
   * Generate a cache key from resource and identifiers
   */
  public getCacheKey(
    resource: string,
    ...identifiers: (string | number | Record<string, unknown> | undefined)[]
  ): string {
    const parts = identifiers
      .filter((id) => id !== undefined)
      .map((id) => (typeof id === "object" ? JSON.stringify(id) : String(id)));
    return `${resource}:${parts.join(":")}`;
  }
}
