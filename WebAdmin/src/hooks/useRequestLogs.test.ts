import { act, renderHook, waitFor } from '@testing-library/react';
import { createElement, type PropsWithChildren } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { withAdminClient } from '@/lib/client/adminClient';
import {
  requestLogFiltersToApiParams,
  useRequestLogs,
} from './useRequestLogs';

jest.mock('@/lib/client/adminClient', () => ({
  withAdminClient: jest.fn(),
}));

const mockedWithAdminClient = jest.mocked(withAdminClient);

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

function response(id: number, page: number) {
  return {
    data: [{ id, modelName: `model-${id}`, timestamp: '2026-07-22T00:00:00Z' }],
    pagination: { totalItems: 1, totalPages: 1, page, pageSize: 25 },
  };
}

describe('useRequestLogs', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('ignores a stale response that resolves after a newer page request', async () => {
    const firstRequest = deferred<ReturnType<typeof response>>();
    const secondRequest = deferred<ReturnType<typeof response>>();
    mockedWithAdminClient
      .mockImplementationOnce(() => firstRequest.promise as never)
      .mockImplementationOnce(() => secondRequest.promise as never);

    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const wrapper = ({ children }: PropsWithChildren) =>
      createElement(QueryClientProvider, { client: queryClient }, children);
    const { result, rerender } = renderHook(
      ({ page }) => useRequestLogs({ page, pageSize: 25 }),
      { initialProps: { page: 1 }, wrapper }
    );

    rerender({ page: 2 });
    await waitFor(() => expect(mockedWithAdminClient).toHaveBeenCalledTimes(2));

    await act(async () => {
      secondRequest.resolve(response(2, 2));
      await secondRequest.promise;
    });
    await waitFor(() => expect(result.current.logs[0]?.id).toBe(2));

    await act(async () => {
      firstRequest.resolve(response(1, 1));
      await firstRequest.promise;
    });

    expect(result.current.logs[0]?.id).toBe(2);
    expect(result.current.currentPage).toBe(2);
    expect(result.current.isLoading).toBe(false);
  });

  it('adapts form-state dates and status to the API contract', () => {
    expect(requestLogFiltersToApiParams({
      startDate: new Date('2026-07-01T00:00:00Z'),
      endDate: new Date('2026-07-02T00:00:00Z'),
      model: 'nova',
      virtualKeyId: 42,
      status: 429,
    }, 3, 25)).toEqual({
      page: 3,
      pageSize: 25,
      startDate: '2026-07-01T00:00:00.000Z',
      endDate: '2026-07-02T00:00:00.000Z',
      model: 'nova',
      virtualKeyId: '42',
      statusCode: 429,
    });
  });
});
