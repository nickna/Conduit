'use client';

import type { ReactNode } from 'react';
import {
  Accordion,
  ActionIcon,
  Badge,
  Card,
  Code,
  CopyButton,
  Drawer,
  Group,
  ScrollArea,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core';
import { IconCheck, IconCopy, IconInfoCircle } from '@tabler/icons-react';
import type { VirtualKeyDto, VirtualKeyGroupDto } from '@/lib/admin-api';
import type { RequestLogEntry, RequestLogMetadata } from '@/hooks/useRequestLogs';
import { formatCost, formatDuration, getHttpStatusColor } from './formatters';

interface RequestLogDetailsDrawerProps {
  opened: boolean;
  onClose: () => void;
  log: RequestLogEntry | null;
  virtualKey?: VirtualKeyDto;
  virtualKeyGroup?: VirtualKeyGroupDto;
}

function formatBillingMethod(method: RequestLogEntry['billingMethod']): string {
  if (method === 'providerReportedCost') return 'Provider-reported cost';
  if (method === 'modelCost') return 'Configured model cost';
  return 'Not recorded';
}

function formatMetadataValue(value: unknown): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') {
    return String(value);
  }
  return JSON.stringify(value) ?? 'Unknown';
}

function DetailRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <Group justify="space-between" gap="md" wrap="nowrap" align="flex-start">
      <Text size="sm" c="dimmed">{label}</Text>
      <Text size="sm" ta="right" style={{ overflowWrap: 'anywhere' }}>{value}</Text>
    </Group>
  );
}

function MetadataSummary({ metadata }: { metadata: RequestLogMetadata }) {
  if (metadata.type === 'chat_with_functions' && metadata.functionCalls) {
    return (
      <Stack gap="xs">
        <Text size="sm" fw={600}>Function executions</Text>
        {metadata.functionCalls.map((call, index) => (
          <Card key={call.toolCallId ?? index} withBorder p="xs">
            <Group justify="space-between" align="flex-start">
              <Stack gap={2}>
                <Text size="sm" fw={500}>{call.functionName ?? 'Unknown function'}</Text>
                {call.functionExecutionId && <Code fz="xs">{call.functionExecutionId}</Code>}
                {call.errorMessage && <Text size="xs" c="red">{call.errorMessage}</Text>}
              </Stack>
              <Stack gap={4} align="flex-end">
                <Badge size="xs" color={call.status === 'completed' ? 'green' : 'red'}>
                  {call.status ?? 'unknown'}
                </Badge>
                {call.cost !== undefined && <Text size="xs">{formatCost(call.cost)}</Text>}
              </Stack>
            </Group>
          </Card>
        ))}
      </Stack>
    );
  }

  if (metadata.type === 'chat_with_tools' && metadata.toolCalls) {
    return (
      <Stack gap="xs">
        <Text size="sm" fw={600}>Tool calls</Text>
        {metadata.toolCalls.map((call, index) => (
          <Group key={call.id ?? index} gap="xs">
            <Badge size="sm" variant="light">{call.functionName ?? 'Unknown tool'}</Badge>
            {call.id && <Code fz="xs">{call.id}</Code>}
          </Group>
        ))}
      </Stack>
    );
  }

  const entries = Object.entries(metadata).filter(([, value]) => value !== null && value !== undefined);
  if (entries.length === 0) return <Text size="sm" c="dimmed">No type-specific metadata</Text>;

  return (
    <Stack gap="xs">
      {entries.map(([key, value]) => (
        <DetailRow
          key={key}
          label={key}
          value={formatMetadataValue(value)}
        />
      ))}
    </Stack>
  );
}

export function RequestLogDetailsDrawer({
  opened,
  onClose,
  log,
  virtualKey,
  virtualKeyGroup,
}: RequestLogDetailsDrawerProps) {
  if (!log) return null;

  const showCacheDetails =
    log.promptCachingEligible ||
    log.promptCachingPolicyApplied ||
    log.cachedInputTokens !== null ||
    log.cachedWriteTokens !== null ||
    log.cachedReadSavings !== 0 ||
    log.cacheWritePremium !== 0;
  const statusColor = getHttpStatusColor(log.statusCode);

  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      position="right"
      size="lg"
      title={
        <Group gap="xs">
          <Text fw={600}>Request details</Text>
          <CopyButton value={String(log.id)}>
            {({ copied, copy }) => (
              <Tooltip label={copied ? 'Copied request ID' : 'Copy request ID'}>
                <ActionIcon variant="subtle" size="sm" onClick={copy} aria-label="Copy request ID">
                  {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
                </ActionIcon>
              </Tooltip>
            )}
          </CopyButton>
        </Group>
      }
    >
      <ScrollArea h="calc(100vh - 90px)" type="auto" offsetScrollbars>
        <Stack gap="md" pr="sm">
          <Group justify="space-between">
            <Text size="sm" ff="monospace">Request #{log.id}</Text>
            <Badge color={statusColor} variant="light">
              {log.statusCode === null ? 'No status' : `HTTP ${log.statusCode}`}
            </Badge>
          </Group>

          <Card withBorder>
            <Stack gap="xs">
              <Text fw={600}>Request</Text>
              <DetailRow label="Timestamp" value={new Date(log.timestamp).toLocaleString()} />
              <DetailRow label="Endpoint" value={log.requestPath ?? '—'} />
              <DetailRow label="Type" value={log.requestType || '—'} />
              <DetailRow label="Model" value={<Code>{log.modelName || 'Unknown'}</Code>} />
              <DetailRow label="Provider" value={log.providerType ?? '—'} />
              <DetailRow label="Provider ID" value={log.providerId ?? '—'} />
              <DetailRow label="Provider mapping ID" value={log.modelProviderMappingId ?? '—'} />
              <DetailRow label="Duration" value={formatDuration(log.responseTimeMs)} />
            </Stack>
          </Card>

          <Card withBorder>
            <Stack gap="xs">
              <Text fw={600}>Usage and billing</Text>
              <DetailRow label="Input tokens" value={log.inputTokens.toLocaleString()} />
              <DetailRow label="Output tokens" value={log.outputTokens.toLocaleString()} />
              <DetailRow label="Cost" value={formatCost(log.cost)} />
              <DetailRow label="Billing method" value={formatBillingMethod(log.billingMethod)} />
              <DetailRow label="Provider-reported cost" value={formatCost(log.providerReportedCostUsd)} />
              <DetailRow label="Provider markup" value={log.providerCostMarkupMultiplier ?? '—'} />
              <DetailRow
                label="Billed at"
                value={log.billedAtUtc ? new Date(log.billedAtUtc).toLocaleString() : '—'}
              />
            </Stack>
          </Card>

          {showCacheDetails && (
            <Card withBorder>
              <Stack gap="xs">
                <Text fw={600}>Prompt caching</Text>
                <DetailRow label="Eligible" value={log.promptCachingEligible ? 'Yes' : 'No'} />
                <DetailRow label="Policy applied" value={log.promptCachingPolicyApplied ? 'Yes' : 'No'} />
                <DetailRow label="Cached read tokens" value={log.cachedInputTokens?.toLocaleString() ?? '—'} />
                <DetailRow label="Cached write tokens" value={log.cachedWriteTokens?.toLocaleString() ?? '—'} />
                <DetailRow label="Read savings" value={formatCost(log.cachedReadSavings)} />
                <DetailRow label="Write premium" value={formatCost(log.cacheWritePremium)} />
                <DetailRow
                  label="Net savings"
                  value={formatCost(log.cachedReadSavings - log.cacheWritePremium)}
                />
              </Stack>
            </Card>
          )}

          <Card withBorder>
            <Stack gap="xs">
              <Text fw={600}>Routing</Text>
              <DetailRow label="Affinity used" value={log.routingAffinityUsed ? 'Yes' : 'No'} />
              <DetailRow label="Decision reason" value={log.routingDecisionReason ?? '—'} />
              <DetailRow label="Failovers" value={log.routingFailoverCount} />
            </Stack>
          </Card>

          <Card withBorder>
            <Stack gap="xs">
              <Text fw={600}>Client and customer</Text>
              <DetailRow label="Virtual key" value={virtualKey?.keyName ?? log.userId ?? `Key #${log.virtualKeyId}`} />
              <DetailRow label="Key ID" value={log.virtualKeyId || '—'} />
              <DetailRow label="Key prefix" value={virtualKey?.keyPrefix ?? '—'} />
              <DetailRow label="Customer" value={virtualKeyGroup?.groupName ?? 'Unavailable'} />
              <DetailRow label="External customer ID" value={virtualKeyGroup?.externalGroupId ?? '—'} />
              <DetailRow label="Logged identity" value={log.userId ?? '—'} />
              <DetailRow label="Client IP" value={log.clientIp ?? '—'} />
            </Stack>
          </Card>

          <Card withBorder>
            <Stack gap="xs">
              <Group gap="xs">
                <Text fw={600}>Type-specific metadata</Text>
                {!log.metadata && (
                  <Tooltip label="This request did not record optional type-specific metadata">
                    <IconInfoCircle size={15} color="gray" />
                  </Tooltip>
                )}
              </Group>
              {!log.metadata && <Text size="sm" c="dimmed">No type-specific metadata was recorded.</Text>}
              {log.metadata && <MetadataSummary metadata={log.metadata} />}
              {log.metadata && (
                <Accordion variant="contained">
                  <Accordion.Item value="raw-metadata">
                    <Accordion.Control>Raw metadata</Accordion.Control>
                    <Accordion.Panel>
                      <Code block style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                        {JSON.stringify(log.metadata, null, 2)}
                      </Code>
                    </Accordion.Panel>
                  </Accordion.Item>
                </Accordion>
              )}
            </Stack>
          </Card>
        </Stack>
      </ScrollArea>
    </Drawer>
  );
}
