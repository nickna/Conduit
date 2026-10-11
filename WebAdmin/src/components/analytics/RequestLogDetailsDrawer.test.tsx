import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { withAdminClient } from '@/lib/client/adminClient';
import { useRequestLogs } from '@/hooks/useRequestLogs';
import { RequestLogsTable } from './RequestLogsTable';
import { formatCost } from './formatters';

jest.mock('@/lib/client/adminClient', () => ({
  withAdminClient: jest.fn(),
}));

const executionId = '00000000-0000-0000-0000-000000000001';
const gatewayCall = {
  tool_call_id: 'call-lookup',
  function_name: 'lookup_customer',
  function_execution_id: executionId,
  error_message: 'provider failed',
  status: 'failed',
  cost: 0.02,
  iteration: 2,
};
const legacyCall = {
  toolCallId: 'call-lookup',
  functionName: 'lookup_customer',
  functionExecutionId: executionId,
  errorMessage: 'provider failed',
  status: 'failed',
  cost: 0.02,
  iteration: 2,
};

function RequestLogs() {
  const { logs } = useRequestLogs({ page: 1, pageSize: 25 });
  return <RequestLogsTable data={logs} />;
}

async function openDetails(functionCall: Record<string, unknown>) {
  jest.mocked(withAdminClient).mockResolvedValue({
    data: [{
      id: 42,
      timestamp: '2026-10-11T06:00:00Z',
      metadata: {
        type: 'chat_with_functions',
        functionCallCount: 1,
        totalCost: 0.02,
        successCount: 0,
        failedCount: 1,
        functionCalls: [functionCall],
        llmCost: 0.01,
        functionCost: 0.02,
        billingRequestId: 'parent-billing-request',
      },
    }],
    pagination: { totalItems: 1, totalPages: 1, page: 1, pageSize: 25 },
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <MantineProvider>
        <RequestLogs />
      </MantineProvider>
    </QueryClientProvider>
  );
  fireEvent.click(await screen.findByRole('button', { name: 'View request details for request 42' }));
}

describe('RequestLogDetailsDrawer API metadata', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it.each([
    { format: 'Gateway snake_case', call: gatewayCall },
    { format: 'legacy camelCase', call: legacyCall },
  ])('shows failed function diagnostics from $format metadata', async ({ call }) => {
    await openDetails(call);

    expect(screen.getByText('lookup_customer')).toBeInTheDocument();
    expect(screen.getByText(executionId)).toBeInTheDocument();
    expect(screen.getByText('provider failed')).toBeInTheDocument();
    expect(screen.getByText('failed')).toBeInTheDocument();
    expect(screen.getByText(formatCost(0.02))).toBeInTheDocument();
    expect(screen.queryByText('Unknown function')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Raw metadata' }));
    const rawMetadata = screen.getByText(/"functionCalls":/);
    expect(rawMetadata).toHaveTextContent('"iteration": 2');
    expect(rawMetadata).toHaveTextContent('"toolCallId": "call-lookup"');
    expect(rawMetadata).toHaveTextContent('"llmCost": 0.01');
    expect(rawMetadata).toHaveTextContent('"functionCost": 0.02');
    expect(rawMetadata).toHaveTextContent('"billingRequestId": "parent-billing-request"');
  });

  it('shows a completed zero-cost Gateway execution without optional diagnostics', async () => {
    await openDetails({
      function_name: 'cached_lookup',
      tool_call_id: 'call-cached',
      status: 'completed',
      cost: 0,
    });

    expect(screen.getByText('cached_lookup')).toBeInTheDocument();
    expect(screen.getByText('completed')).toBeInTheDocument();
    expect(screen.queryByText('Unknown function')).not.toBeInTheDocument();
    // Table total, request total, and the individual execution each show zero cost.
    expect(screen.getAllByText('$0.00')).toHaveLength(3);
  });
});
