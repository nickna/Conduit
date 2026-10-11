import { ScrollArea, Stack, Text, Group, Badge, Paper, Code, Collapse, ActionIcon, HoverCard, CopyButton, Tooltip } from '@mantine/core';
import { IconUser, IconRobot, IconClock, IconBolt, IconChevronDown, IconChevronUp, IconInfoCircle, IconCopy, IconCheck, IconCode, IconEye } from '@tabler/icons-react';
import { ChatMessage } from '../types';
import React, { useEffect, useRef, useState, useMemo } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { vscDarkPlus } from 'react-syntax-highlighter/dist/cjs/styles/prism';
import { AttachmentPreview } from './AttachmentUpload';
import { processStructuredContent } from '@/lib/gateway-api';
import { MessageErrorCard } from './MessageErrorCard';
import { ToolExecutionDisplay } from './ToolExecutionDisplay';
import { CollapsibleThinking } from './CollapsibleThinking';
import { streamingMarkdownComponents, createMessageMarkdownComponents } from '../utils/markdown';

interface ChatMessagesProps {
  messages: ChatMessage[];
  isLoading?: boolean;
  streamingContent?: string;
  streamingChannel?: string | null;
  tokensPerSecond?: number | null;
  reasoningExpanded?: boolean;
  onRetryMessage?: (messageId: string) => void;
}

export function ChatMessages({ messages, isLoading, streamingContent, streamingChannel, tokensPerSecond, reasoningExpanded = true, onRetryMessage }: ChatMessagesProps) {
  const scrollAreaRef = useRef<HTMLDivElement>(null);
  const lastMessageRef = useRef<HTMLDivElement>(null);
  const [expandedErrors, setExpandedErrors] = useState<Set<string>>(new Set());
  const [expandedReasoning, setExpandedReasoning] = useState<Set<string>>(new Set());
  const [rawViewMessages, setRawViewMessages] = useState<Set<string>>(new Set());

  const messageMarkdownComponents = useMemo(
    () => createMessageMarkdownComponents(CollapsibleThinking),
    []
  );

  useEffect(() => {
    if (lastMessageRef.current) {
      lastMessageRef.current.scrollIntoView({ behavior: 'smooth' });
    }
  }, [messages, streamingContent]);

  const toggleSet = (setter: React.Dispatch<React.SetStateAction<Set<string>>>, id: string) => {
    setter(prev => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const isReasoningExpanded = (messageId: string) =>
    expandedReasoning.has(messageId) ? !reasoningExpanded : reasoningExpanded;

  const renderMessage = (message: ChatMessage, isStreaming = false) => {
    const isUser = message.role === 'user';
    const content = message.content;
    const hasError = message.error && !isUser;
    const isRawView = rawViewMessages.has(message.id);
    const copyLabel = isRawView ? 'Copy JSON' : 'Copy message';
    const hasReasoning = !isUser && message.metadata?.hasReasoning && message.metadata?.reasoning;
    const reasoningText = hasReasoning ? message.metadata?.reasoning : null;
    const attachments = message.attachments ?? message.images ?? [];
    const hasAttachments = attachments.length > 0;

    // Error messages get a dedicated card
    if (hasError && message.error) {
      return (
        <MessageErrorCard
          key={message.id}
          message={message}
          isExpanded={expandedErrors.has(message.id)}
          onToggleDetails={() => toggleSet(setExpandedErrors, message.id)}
          onRetry={onRetryMessage ? () => onRetryMessage(message.id) : undefined}
          isLoading={isLoading}
        />
      );
    }

    return (
      <Paper
        key={message.id}
        p="md"
        radius="md"
        className={isUser ? 'chat-message-user' : 'chat-message-assistant'}
        style={{
          alignSelf: isUser ? 'flex-end' : 'flex-start',
          maxWidth: '80%',
        }}
      >
        <Stack gap="xs">
          {/* Message header */}
          <Group justify="space-between" wrap="nowrap">
            <Group gap="xs" wrap="wrap">
              {isUser ? <IconUser size={16} /> : <IconRobot size={16} />}
              <Text fw={600} size="sm">
                {isUser ? 'You' : message.model ?? 'Assistant'}
              </Text>

              {isUser && message.metadata?.functionNames && message.metadata.functionNames.length > 0 && (
                <Group gap={4}>
                  {message.metadata.functionNames.map((name: string) => (
                    <Badge key={name} size="xs" variant="light" color="violet">
                      {name}
                    </Badge>
                  ))}
                </Group>
              )}
            </Group>

            {/* Raw view toggle + metadata badges */}
            {isUser && (
              <Tooltip label={isRawView ? 'Show formatted view' : 'Show raw request'} withArrow>
                <ActionIcon
                  variant="subtle"
                  size="sm"
                  onClick={() => toggleSet(setRawViewMessages, message.id)}
                  color={isRawView ? 'blue' : 'gray'}
                >
                  {isRawView ? <IconEye size={16} /> : <IconCode size={16} />}
                </ActionIcon>
              </Tooltip>
            )}

            {!isUser && (message.metadata ?? (isStreaming && tokensPerSecond)) && (
              <Group gap="xs">
                {message.metadata && !isStreaming && (
                  <Tooltip label={isRawView ? 'Show formatted view' : 'Show raw response'} withArrow>
                    <ActionIcon
                      variant="subtle"
                      size="sm"
                      onClick={() => toggleSet(setRawViewMessages, message.id)}
                      color={isRawView ? 'blue' : 'gray'}
                    >
                      {isRawView ? <IconEye size={16} /> : <IconCode size={16} />}
                    </ActionIcon>
                  </Tooltip>
                )}
                {message.metadata?.tokensUsed !== null && message.metadata?.tokensUsed !== undefined && message.metadata.tokensUsed > 0 && (
                  <Badge size="xs" variant="light">
                    {message.metadata.tokensUsed} tokens
                  </Badge>
                )}
                {(() => {
                  const tps = message.metadata?.tokensPerSecond ?? (isStreaming ? tokensPerSecond : null);
                  return tps !== null && tps !== undefined && tps > 0 ? (
                    <Badge size="xs" variant="light" color="green">
                      <Group gap={4}>
                        <IconBolt size={12} />
                        {tps.toFixed(1)} t/s
                      </Group>
                    </Badge>
                  ) : null;
                })()}
                {message.metadata?.latency !== null && message.metadata?.latency !== undefined && message.metadata.latency > 0 && (
                  <Badge size="xs" variant="light" color="blue">
                    <Group gap={4}>
                      <IconClock size={12} />
                      {(message.metadata.latency / 1000).toFixed(1)}s
                    </Group>
                  </Badge>
                )}
                {(message.metadata?.provider ?? message.metadata?.model ?? message.metadata?.promptTokens ?? message.metadata?.completionTokens) && (
                  <HoverCard width={280} shadow="md" withArrow>
                    <HoverCard.Target>
                      <ActionIcon variant="subtle" size="sm" color="gray">
                        <IconInfoCircle size={16} />
                      </ActionIcon>
                    </HoverCard.Target>
                    <HoverCard.Dropdown>
                      <Stack gap="xs">
                        <Text size="sm" fw={600}>Response Details</Text>
                        {message.metadata.streaming !== undefined && (
                          <Group gap="xs">
                            <Text size="xs" c="dimmed">Response Type:</Text>
                            <Text size="xs">{message.metadata.streaming ? 'SSE (Streaming)' : 'JSON (Complete)'}</Text>
                          </Group>
                        )}
                        {message.metadata.provider && (
                          <Group gap="xs">
                            <Text size="xs" c="dimmed">Provider:</Text>
                            <Text size="xs">{message.metadata.provider}</Text>
                          </Group>
                        )}
                        {message.metadata.model && (
                          <Group gap="xs">
                            <Text size="xs" c="dimmed">Model:</Text>
                            <Text size="xs">{message.metadata.model}</Text>
                          </Group>
                        )}
                        {message.metadata.promptTokens !== undefined && (
                          <Group gap="xs">
                            <Text size="xs" c="dimmed">Prompt Tokens:</Text>
                            <Text size="xs">{message.metadata.promptTokens}</Text>
                          </Group>
                        )}
                        {message.metadata.completionTokens !== undefined && (
                          <Group gap="xs">
                            <Text size="xs" c="dimmed">Completion Tokens:</Text>
                            <Text size="xs">{message.metadata.completionTokens}</Text>
                          </Group>
                        )}
                      </Stack>
                    </HoverCard.Dropdown>
                  </HoverCard>
                )}
              </Group>
            )}
          </Group>

          {/* Attachments */}
          {hasAttachments && (
            <AttachmentPreview attachments={attachments} />
          )}

          {/* Function calls */}
          {message.functionCall && (
            <Paper p="xs" radius="sm" withBorder>
              <Text size="xs" fw={600} mb={4}>Function Call:</Text>
              <Text size="xs" c="blue" fw={500}>{message.functionCall.name}</Text>
              <Code block mt={4}>
                {message.functionCall.arguments}
              </Code>
            </Paper>
          )}

          {/* Tool calls */}
          {message.toolCalls && message.toolCalls.length > 0 && (
            <Stack gap="xs">
              <Text size="xs" fw={600}>Tool Calls:</Text>
              {message.toolCalls.map((tool) => (
                <Paper key={tool.id || tool.function.name} p="xs" radius="sm" withBorder>
                  <Text size="xs" c="green" fw={500}>{tool.function.name}</Text>
                  <Code block mt={4} style={{ fontSize: '0.75rem' }}>
                    {tool.function.arguments}
                  </Code>
                </Paper>
              ))}
            </Stack>
          )}

          {/* Tool execution progress */}
          {message.metadata?.toolExecutions && message.metadata.toolExecutions.length > 0 && (
            <ToolExecutionDisplay executions={message.metadata.toolExecutions} />
          )}

          {/* Reasoning block (collapsible) */}
          {reasoningText && !isRawView && (
            <Paper
              p="sm"
              radius="md"
              withBorder
              style={{
                backgroundColor: 'var(--mantine-color-gray-light)',
                marginBottom: '0.75rem',
                cursor: 'pointer',
                transition: 'all 0.2s ease',
                userSelect: 'none'
              }}
              className="reasoning-block"
              onClick={() => toggleSet(setExpandedReasoning, message.id)}
            >
              <Group gap="xs" wrap="nowrap">
                <ActionIcon variant="subtle" size="sm" style={{ pointerEvents: 'none' }}>
                  {isReasoningExpanded(message.id) ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
                </ActionIcon>
                <Text size="sm" fw={500} style={{ flex: 1 }}>
                  {'\uD83E\uDDE0'} Reasoning
                </Text>
                <Text size="xs" c="dimmed">
                  {isReasoningExpanded(message.id) ? 'Click to collapse' : 'Click to expand'}
                </Text>
              </Group>
              <Collapse expanded={isReasoningExpanded(message.id)}>
                <div className="reasoning-content markdown-content" style={{ marginTop: '0.5rem', paddingLeft: '1.5rem' }}>
                  <ReactMarkdown remarkPlugins={[remarkGfm]}>{reasoningText}</ReactMarkdown>
                </div>
              </Collapse>
            </Paper>
          )}

          {/* Message content: raw JSON or formatted markdown */}
          {(() => {
            if (isRawView && !isStreaming && isUser) {
              return (
                <Stack gap="md">
                  <div>
                    <Text size="xs" fw={600} mb={4} c="dimmed">Message Data:</Text>
                    <div style={{ maxHeight: '300px', overflow: 'auto' }}>
                      <SyntaxHighlighter language="json" style={vscDarkPlus} customStyle={{ margin: 0, fontSize: '0.85rem', borderRadius: '4px' }}>
                        {JSON.stringify({
                          id: message.id, role: message.role, timestamp: message.timestamp, content: message.content,
                          ...(hasAttachments && {
                            attachments
                          }),
                          ...(message.metadata?.functionIds && { function_ids: message.metadata.functionIds }),
                          ...(message.metadata?.functionNames && { function_names: message.metadata.functionNames })
                        }, null, 2)}
                      </SyntaxHighlighter>
                    </div>
                  </div>
                  {message.metadata?.apiRequest && (
                    <div>
                      <Text size="xs" fw={600} mb={4} c="dimmed">API Request Sent to Conduit:</Text>
                      <div style={{ maxHeight: '400px', overflow: 'auto' }}>
                        <SyntaxHighlighter language="json" style={vscDarkPlus} customStyle={{ margin: 0, fontSize: '0.85rem', borderRadius: '4px' }}>
                          {JSON.stringify(message.metadata.apiRequest, null, 2)}
                        </SyntaxHighlighter>
                      </div>
                    </div>
                  )}
                </Stack>
              );
            }

            if (isRawView && !isStreaming && !isUser) {
              return (
                <div style={{ maxHeight: '400px', overflow: 'auto' }}>
                  <SyntaxHighlighter language="json" style={vscDarkPlus} customStyle={{ margin: 0, fontSize: '0.85rem', borderRadius: '4px' }}>
                    {JSON.stringify({
                      id: message.id, timestamp: message.timestamp,
                      model: message.model ?? message.metadata?.model, content: message.content,
                      metadata: message.metadata ? {
                        ...(message.metadata.latency !== undefined && { latency_ms: message.metadata.latency }),
                        ...(message.metadata.timeToFirstToken !== undefined && { time_to_first_token_ms: message.metadata.timeToFirstToken }),
                        ...(message.metadata.tokensPerSecond !== undefined && { tokens_per_second: message.metadata.tokensPerSecond }),
                        ...(message.metadata.promptTokens !== undefined && { prompt_tokens: message.metadata.promptTokens }),
                        ...(message.metadata.completionTokens !== undefined && { completion_tokens: message.metadata.completionTokens }),
                        ...(message.metadata.tokensUsed !== undefined && { total_tokens: message.metadata.tokensUsed }),
                        ...(message.metadata.provider && { provider: message.metadata.provider }),
                        ...(message.metadata.model && { model: message.metadata.model }),
                        ...(message.metadata.streaming !== undefined && { streaming: message.metadata.streaming }),
                        ...(message.metadata.finishReason && { finish_reason: message.metadata.finishReason }),
                        ...(message.metadata.hasReasoning && { has_reasoning: message.metadata.hasReasoning }),
                        ...(message.metadata.reasoning && { reasoning: message.metadata.reasoning })
                      } : undefined,
                      ...(message.functionCall && { function_call: message.functionCall }),
                      ...(message.toolCalls && { tool_calls: message.toolCalls }),
                      ...(hasAttachments && {
                        attachments
                      })
                    }, null, 2)}
                  </SyntaxHighlighter>
                </div>
              );
            }

            // Normal markdown view
            return (
              <div className={`markdown-content ${isStreaming && streamingChannel === 'analysis' ? 'reasoning-content' : ''}`}>
                {isStreaming ? (
                  <pre style={{ whiteSpace: 'pre-wrap', fontFamily: 'inherit' }}>{content}</pre>
                ) : (
                  <ReactMarkdown remarkPlugins={[remarkGfm]} components={messageMarkdownComponents}>
                    {processStructuredContent(content ?? '')}
                  </ReactMarkdown>
                )}
              </div>
            );
          })()}

          {/* Copy button */}
          {content && (
            <Group justify="flex-end" mt="xs">
              <CopyButton value={(() => {
                if (!isRawView) return content;
                if (isUser) {
                  return JSON.stringify({
                    message_data: { id: message.id, role: message.role, timestamp: message.timestamp, content: message.content, attachments: message.attachments ?? message.images, function_ids: message.metadata?.functionIds, function_names: message.metadata?.functionNames },
                    api_request: message.metadata?.apiRequest
                  }, null, 2);
                }
                return JSON.stringify({
                  id: message.id, timestamp: message.timestamp, model: message.model ?? message.metadata?.model,
                  content: message.content, metadata: message.metadata, function_call: message.functionCall,
                  tool_calls: message.toolCalls, attachments: message.attachments ?? message.images
                }, null, 2);
              })()} timeout={2000}>
                {({ copied, copy }) => (
                  <Tooltip label={copied ? 'Copied!' : copyLabel} withArrow position="left">
                    <ActionIcon color={copied ? 'teal' : 'gray'} onClick={copy} variant="subtle" size="sm">
                      {copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
                    </ActionIcon>
                  </Tooltip>
                )}
              </CopyButton>
            </Group>
          )}
        </Stack>
      </Paper>
    );
  };

  return (
    <ScrollArea
      style={{ height: '100%', flex: 1 }}
      viewportRef={scrollAreaRef}
      type="auto"
      scrollbarSize={8}
      scrollHideDelay={800}
    >
      <Stack gap="md" p="xs">
        {messages.map((message) => renderMessage(message))}
        {/* eslint-disable-next-line @typescript-eslint/prefer-nullish-coalescing */}
        {(isLoading || streamingContent) && (
          <Paper
            p="md"
            radius="md"
            className="chat-message-assistant"
            style={{ alignSelf: 'flex-start', maxWidth: '80%' }}
          >
            <Stack gap="xs">
              <Group gap="xs">
                <IconRobot size={16} />
                <Text fw={600} size="sm">
                  {streamingContent ? 'Streaming...' : 'Assistant'}
                </Text>
              </Group>
              {streamingContent ? (
                <div className="markdown-content">
                  <ReactMarkdown remarkPlugins={[remarkGfm]} components={streamingMarkdownComponents}>
                    {streamingContent}
                  </ReactMarkdown>
                </div>
              ) : (
                <Group gap={4}>
                  <span className="loading-dots">
                    <span>.</span>
                    <span>.</span>
                    <span>.</span>
                  </span>
                </Group>
              )}
            </Stack>
          </Paper>
        )}
        <div ref={lastMessageRef} />
      </Stack>
    </ScrollArea>
  );
}
