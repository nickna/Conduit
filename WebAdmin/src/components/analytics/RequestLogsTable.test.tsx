import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { VirtualKeyDto, VirtualKeyGroupDto } from '@/lib/admin-api';
import type { RequestLogEntry } from '@/hooks/useRequestLogs';
import { RequestLogsTable } from './RequestLogsTable';

const baseLog: RequestLogEntry = {
  id: 42,
  virtualKeyId: 7,
  modelName: 'muse-spark-1.1',
  providerId: 3,
  providerType: 'MiniMax',
  modelProviderMappingId: 11,
  promptCachingEligible: true,
  promptCachingPolicyApplied: true,
  cachedReadSavings: 0.0002,
  cacheWritePremium: 0.0001,
  routingAffinityUsed: true,
  routingDecisionReason: 'cache-affinity',
  routingFailoverCount: 1,
  requestType: 'chat',
  inputTokens: 8,
  outputTokens: 625,
  cachedInputTokens: 4,
  cachedWriteTokens: 2,
  cost: 0.000063,
  billingMethod: 'providerReportedCost',
  providerReportedCostUsd: 0.00005,
  providerCostMarkupMultiplier: 1.2,
  billedAtUtc: '2026-07-22T06:00:01Z',
  responseTimeMs: 6730,
  userId: 'Historical key name',
  clientIp: '127.0.0.1',
  requestPath: '/v1/chat/completions',
  statusCode: 200,
  timestamp: '2026-07-22T06:00:00Z',
  metadata: null,
};

const virtualKey = {
  id: 7,
  keyName: 'Customer API',
  keyPrefix: 'condt_live',
  virtualKeyGroupId: 9,
  isEnabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  description: 'Production customer traffic',
  rateLimitRpm: 60,
  rateLimitRpd: 1000,
} as VirtualKeyDto;

const group = {
  id: 9,
  groupName: 'Acme Corporation',
  externalGroupId: 'acct_acme',
  balance: 25,
  lifetimeCreditsAdded: 100,
  lifetimeSpent: 75,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  virtualKeyCount: 1,
} as VirtualKeyGroupDto;

function renderTable(log: RequestLogEntry = baseLog, onViewVirtualKey = jest.fn()) {
  render(
    <MantineProvider>
      <RequestLogsTable
        data={[log]}
        virtualKeys={[virtualKey]}
        virtualKeyGroups={[group]}
        onViewVirtualKey={onViewVirtualKey}
      />
    </MantineProvider>
  );
  return onViewVirtualKey;
}

describe('RequestLogsTable', () => {
  it('explains duration and exposes customer context from keyboard focus', async () => {
    const onViewVirtualKey = renderTable();

    expect(screen.getByText('Duration')).toBeInTheDocument();
    expect(screen.getByText('6.7s')).toBeInTheDocument();

    const keyButton = screen.getByRole('button', { name: 'View virtual key Customer API' });
    fireEvent.focus(keyButton);

    expect(await screen.findByText('Acme Corporation')).toBeInTheDocument();
    expect(screen.getByText('acct_acme')).toBeInTheDocument();

    fireEvent.click(keyButton);
    expect(onViewVirtualKey).toHaveBeenCalledWith(virtualKey);
  });

  it('opens diagnostics even when type-specific metadata is absent', () => {
    renderTable();

    fireEvent.click(screen.getByRole('button', { name: 'View request details for request 42' }));

    expect(screen.getByText('Request details')).toBeInTheDocument();
    expect(screen.getByText('/v1/chat/completions')).toBeInTheDocument();
    expect(screen.getAllByText('Provider-reported cost')).toHaveLength(2);
    expect(screen.getByText('No type-specific metadata was recorded.')).toBeInTheDocument();
  });

  it('renders recognized function metadata as structured diagnostics', () => {
    renderTable({
      ...baseLog,
      metadata: {
        type: 'chat_with_functions',
        functionCalls: [{
          functionName: 'lookup_customer',
          status: 'completed',
          functionExecutionId: 'execution-123',
          cost: 0.001,
        }],
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'View request details for request 42' }));

    expect(screen.getByText('Function executions')).toBeInTheDocument();
    expect(screen.getByText('lookup_customer')).toBeInTheDocument();
    expect(screen.getByText('execution-123')).toBeInTheDocument();
  });

  it('falls back to the historical key identity when the current key is unavailable', () => {
    render(
      <MantineProvider>
        <RequestLogsTable data={[baseLog]} />
      </MantineProvider>
    );

    expect(screen.getByText('Historical key name')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /current key details unavailable/i })).toBeInTheDocument();
  });
});
