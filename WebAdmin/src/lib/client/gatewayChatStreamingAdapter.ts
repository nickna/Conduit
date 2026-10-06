import { getBrowserGatewayClient } from './browserGatewayClient';
import {
  isChatCompletionChunk,
  isStreamingMetrics,
  isFinalMetrics,
  isStreamingErrorEvent,
  isReasoningEvent,
  isToolExecutingEvent,
  isToolResultEvent,
  buildMessageContent,
  type StreamingCallbacks,
  type StreamMessageOptions,
  type StreamingError
} from '@/lib/gateway-api';


/**
 * Adapter for the Gateway's custom SSE chat transport.
 */
export class GatewayChatStreamingAdapter {
  private abortController: AbortController | null = null;

  constructor(
    private config: {
      timeoutMs?: number;
      trackPerformanceMetrics?: boolean;
      showTokensPerSecond?: boolean;
      useServerMetrics?: boolean;
      enableLogging?: boolean;
    }
  ) {}

  /**
   * Stream a chat message using the local Gateway client.
   */
  async streamMessage(
    message: string,
    options: StreamMessageOptions,
    callbacks: StreamingCallbacks
  ): Promise<void> {
    // Track content and tool calls for callbacks (declared outside try to be accessible in catch)
    let totalContent = '';
    let totalReasoning = '';
    const toolCalls: Array<{
      id: string;
      type: 'function';
      function: {
        name: string;
        arguments: string;
      };
    }> = [];

    // Track completion state to handle race condition between finish_reason and metrics-final
    let completionTriggered = false;
    let lastFinishReason: string | null = null;

    try {
      // Get the Gateway client with an ephemeral key.
      const client = await getBrowserGatewayClient();
      
      // Create abort controller for cancellation
      this.abortController = new AbortController();
      
      // Prepare the chat request
      const attachments = options.attachments ?? options.images;
      const historyAttachments = (options.messages ?? []).flatMap(
        (historyMessage) => historyMessage.attachments ?? historyMessage.images ?? []
      );
      const parser = [...historyAttachments, ...(attachments ?? [])].find(
        (attachment) => attachment.kind === 'pdf' && attachment.parser && attachment.parser !== 'auto'
      )?.parser;
      const dynamicParameters = options.dynamicParameters ?? {};
      const chatRequest = {
        messages: [
          ...(options.systemPrompt ? [{ role: 'system' as const, content: options.systemPrompt }] : []),
          ...(options.messages ?? []).map((historyMessage) => ({
            role: historyMessage.role,
            content: buildMessageContent(
              historyMessage.content,
              historyMessage.attachments ?? historyMessage.images
            )
          })),
          {
            role: 'user' as const,
            content: buildMessageContent(message, attachments)
          }
        ],
        model: options.model,
        temperature: options.temperature,
        max_tokens: options.maxTokens,
        top_p: options.topP,
        frequency_penalty: options.frequencyPenalty,
        presence_penalty: options.presencePenalty,
        seed: options.seed,
        stop: options.stop,
        response_format: options.responseFormat ? { type: options.responseFormat } : undefined,
        stream: true as const,
        function_configuration_ids: options.functionConfigurationIds,
        // Include dynamic parameters
        ...dynamicParameters,
        ...(parser && !('plugins' in dynamicParameters)
          ? { plugins: [{ id: 'file-parser', pdf: { engine: parser } }] }
          : {})
      };

      // Start callback
      if (callbacks.onStart) {
        callbacks.onStart();
      }

      // Start the custom SSE request.
      const stream = await client.chat.create(chatRequest, {
        signal: this.abortController.signal
      });

      // Process the stream
      for await (const data of stream) {
        // Handle different event types from the stream
        if (isChatCompletionChunk(data)) {
          // Handle chunk callback
          if (callbacks.onChunk) {
            // Normalize the wire chunk to the callback type.
            const transformedChunk = {
              ...data,
              choices: data.choices?.map(choice => ({
                ...choice,
                finish_reason: choice.finish_reason ?? undefined
              })) ?? []
            };
            callbacks.onChunk(transformedChunk as Parameters<typeof callbacks.onChunk>[0]);
          }

          // Handle content updates
          const content = data.choices?.[0]?.delta?.content;
          if (content) {
            totalContent += content;

            if (callbacks.onContent) {
              callbacks.onContent(content, totalContent);
            }
          }

          // Note: We do NOT accumulate delta.reasoning here because:
          // 1. The backend sends reasoning via dedicated "event: reasoning" SSE events
          // 2. It also includes delta.reasoning in chunks for compatibility
          // 3. Accumulating from both sources causes text duplication
          // 4. We only accumulate from the reasoning event (lines 260-272)
          //
          // If a provider sends ONLY delta.reasoning without reasoning events,
          // that's a backend issue that should be fixed by emitting reasoning events.

          // Handle tool calls in streaming response
          const deltaToolCalls = data.choices?.[0]?.delta?.tool_calls as Array<{
            index?: number;
            id?: string;
            type?: 'function';
            function?: {
              name?: string;
              arguments?: string;
            };
          }> | undefined;

          if (deltaToolCalls && Array.isArray(deltaToolCalls)) {
            for (const toolCall of deltaToolCalls) {
              const index = toolCall.index ?? 0;

              // Initialize tool call if it doesn't exist
              if (!toolCalls[index]) {
                toolCalls[index] = {
                  id: toolCall.id ?? `call_${index}`,
                  type: 'function',
                  function: {
                    name: toolCall.function?.name ?? '',
                    arguments: toolCall.function?.arguments ?? ''
                  }
                };
              } else {
                // Append to existing tool call
                if (toolCall.id) {
                  toolCalls[index].id = toolCall.id;
                }
                if (toolCall.function?.name) {
                  toolCalls[index].function.name += toolCall.function.name;
                }
                if (toolCall.function?.arguments) {
                  toolCalls[index].function.arguments += toolCall.function.arguments;
                }
              }
            }
          }

          // Check for completion in chunk (some providers send finish_reason in a chunk)
          const finishReason = data.choices?.[0]?.finish_reason;
          if (finishReason) {
            // IMPORTANT: finish_reason can appear in the middle of a stream!
            // - finish_reason: "tool_calls" = tool invocation, backend will continue streaming with tool results
            // - finish_reason: "stop" = actual end of stream
            // - finish_reason: "length" = max tokens reached, actual end

            if (finishReason === 'tool_calls') {
              // Tool call completion - DO NOT end the stream!
              // The backend will execute the tool and continue streaming the results
              // Just continue processing chunks
              continue;
            } else {
              // stop/length/other = completion detected
              // Note the finish_reason but DON'T end the stream yet
              // Wait for metrics-final event which has the actual token counts and timing
              lastFinishReason = finishReason;
              // Continue processing to wait for metrics-final event
            }
          }
        } else if (isStreamingMetrics(data)) {
          // Handle streaming metrics updates — the type guard narrows to StreamingMetrics

          // Update tokens per second if available
          if (callbacks.onTokensPerSecond && this.config.showTokensPerSecond) {
            const tokensPerSecond = data.current_tokens_per_second;
            if (tokensPerSecond !== undefined) {
              callbacks.onTokensPerSecond(tokensPerSecond);
            }
          }

          // Pass metrics to callback if available
          if (callbacks.onMetrics) {
            callbacks.onMetrics(data);
          }
        } else if (isFinalMetrics(data)) {
          // Handle final metrics - this has the accurate token counts and timing
          const finalMetrics = data;

          // Build metadata from server-provided final metrics
          // The backend calculates accurate timing using Stopwatch from request start to completion
          const metadata = {
            model: finalMetrics.model ?? options.model,
            finishReason: 'stop' as const, // FinalMetrics indicate completion
            tokensUsed: finalMetrics.total_tokens ?? undefined,
            completionTokens: finalMetrics.completion_tokens ?? undefined,
            promptTokens: finalMetrics.prompt_tokens ?? undefined,
            latency: finalMetrics.total_latency_ms ?? undefined,
            timeToFirstToken: finalMetrics.time_to_first_token_ms ?? undefined,
            tokensPerSecond: finalMetrics.tokens_per_second ?? finalMetrics.completion_tokens_per_second ?? undefined,
            streaming: true,
            provider: finalMetrics.provider ?? undefined,
            toolCalls: toolCalls.length > 0 ? toolCalls : undefined
          };

          // Mark completion as triggered so we don't call it again in fallback
          completionTriggered = true;

          if (callbacks.onComplete) {
            // Use reasoning as fallback if no regular content was received
            // This handles models like gpt-oss-20b that non-deterministically output to reasoning
            const finalContent = totalContent.length > 0 ? totalContent : totalReasoning;

            callbacks.onComplete({
              content: finalContent,
              metadata
            });
          }
          break; // Stream is complete
        } else if (isStreamingErrorEvent(data)) {
          // Handle error event - sent as "event: error" when upstream provider fails
          // This surfaces provider errors (model not found, auth failure, rate limit, etc.)
          const streamingError: StreamingError = Object.assign(
            new Error(data.error),
            {
              status: undefined,
              code: 'provider_error',
              context: data.error,
              retryable: false
            }
          );

          if (callbacks.onError) {
            callbacks.onError(streamingError);
          }
          completionTriggered = true; // Prevent fallback from also firing
          break; // Stream is done after an error
        } else if (isReasoningEvent(data)) {
          // Handle reasoning event - sent as "event: reasoning"
          const reasoning = data.content;
          if (reasoning) {
            totalReasoning += reasoning;

            // Call the reasoning callback if available
            if (callbacks.onReasoning) {
              callbacks.onReasoning(reasoning, totalReasoning);
            }
          }
        } else if (isToolExecutingEvent(data)) {
          // Handle tool execution status event - sent as "event: tool-executing"
          // Provides real-time feedback during function calling
          if (callbacks.onToolExecuting) {
            callbacks.onToolExecuting({
              tool_call_id: data.tool_call_id,
              function_name: data.function_name,
              status: data.status,
              result: data.result,
              cost: data.cost,
              error_message: data.error_message,
              function_execution_id: data.function_execution_id
            });
          }
        } else if (isToolResultEvent(data)) {
          // Handle individual tool result event - sent as "event: tool-result"
          // Optional detailed logging of tool execution outcomes
          if (callbacks.onToolResult) {
            callbacks.onToolResult({
              tool_call_id: data.tool_call_id,
              result: data.result,
              error: data.error
            });
          }
        }
      }

      // If stream ended without receiving metrics-final event, use fallback metadata
      // This can happen if:
      // 1. Provider doesn't send metrics-final (shouldn't happen with our backend)
      // 2. Stream was interrupted before metrics-final arrived
      // 3. Race condition where finish_reason chunk was the last chunk
      if (!completionTriggered && callbacks.onComplete) {
        const fallbackMetadata = {
          model: options.model,
          finishReason: lastFinishReason ?? 'stop',
          tokensUsed: undefined,
          completionTokens: undefined,
          promptTokens: undefined,
          latency: undefined,
          timeToFirstToken: undefined,
          tokensPerSecond: undefined,
          streaming: true,
          provider: undefined,
          toolCalls: toolCalls.length > 0 ? toolCalls : undefined
        };

        // Use reasoning as fallback if no regular content was received
        const finalContent = totalContent.length > 0 ? totalContent : totalReasoning;

        callbacks.onComplete({
          content: finalContent,
          metadata: fallbackMetadata
        });
      }
    } catch (error) {
      // Handle abort separately
      if (error instanceof Error && error.name === 'AbortError') {
        if (callbacks.onAbort) {
          callbacks.onAbort();
        }
      } else {
        if (callbacks.onError) {
          // Convert error to StreamingError
          let streamingError: StreamingError;

          if (error && typeof error === 'object' && 'status' in error && 'code' in error) {
            // Already a normalized streaming error.
            streamingError = error as StreamingError;
          } else {
            // Create StreamingError from generic error
            const baseError = error instanceof Error ? error : new Error(String(error));
            const partialContent = totalContent.length > 0 ? totalContent : totalReasoning;
            streamingError = Object.assign(baseError, {
              context: partialContent.length > 0 ? `Partial content: ${partialContent.slice(0, 100)}...` : undefined,
              retryable: false
            } as Partial<StreamingError>);
          }

          callbacks.onError(streamingError);
        }
      }
    } finally {
      this.abortController = null;
    }
  }

  /**
   * Abort the current streaming operation
   */
  abort(): void {
    if (this.abortController) {
      this.abortController.abort();
      this.abortController = null;
    }
  }

  /**
   * Clean up resources
   */
  dispose(): void {
    this.abort();
  }
}
