'use client';

import { useCallback, useState } from 'react';
import { notify } from '@/lib/notifications';
import { withAdminClient } from '@/lib/client/adminClient';
import type { CreateIpFilterDto, IpFilterDto, UpdateIpFilterDto } from '@/lib/admin-api';

export interface IpStats {
  totalRules: number;
  allowRules: number;
  blockRules: number;
  activeRules: number;
  lastRuleUpdate: string | null;
}

export function useSecurityApi() {
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(async <T,>(operation: () => Promise<T>, failureMessage: string): Promise<T> => {
    setIsLoading(true);
    setError(null);
    try {
      return await operation();
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : failureMessage;
      setError(message);
      throw cause;
    } finally {
      setIsLoading(false);
    }
  }, []);

  const getIpRules = useCallback(
    () => run(() => withAdminClient(client => client.ipFilters.list()), 'Failed to fetch IP rules'),
    [run],
  );

  const getIpRulesForKey = useCallback(
    (virtualKeyId: number) => run(
      () => withAdminClient(client => client.ipFilters.listByVirtualKey(virtualKeyId)),
      'Failed to fetch IP rules',
    ),
    [run],
  );

  const createIpRule = useCallback(async (rule: CreateIpFilterDto): Promise<IpFilterDto> => {
    try {
      const result = await run(
        () => withAdminClient(client => client.ipFilters.create(rule)),
        'Failed to create IP rule',
      );
      notify.success('IP rule created successfully');
      return result;
    } catch (cause) {
      notify.error(cause);
      throw cause;
    }
  }, [run]);

  const createIpRuleForKey = useCallback(
    (virtualKeyId: number, rule: CreateIpFilterDto) => createIpRule({ ...rule, virtualKeyId }),
    [createIpRule],
  );

  const updateIpRule = useCallback(async (id: number, rule: UpdateIpFilterDto): Promise<IpFilterDto> => {
    try {
      const result = await run(async () => {
        await withAdminClient(client => client.ipFilters.update(id, rule));
        return withAdminClient(client => client.ipFilters.getById(id));
      }, 'Failed to update IP rule');
      notify.success('IP rule updated successfully');
      return result;
    } catch (cause) {
      notify.error(cause);
      throw cause;
    }
  }, [run]);

  const deleteIpRule = useCallback(async (id: number): Promise<void> => {
    try {
      await run(
        () => withAdminClient(client => client.ipFilters.deleteById(id)),
        'Failed to delete IP rule',
      );
      notify.success('IP rule deleted successfully');
    } catch (cause) {
      notify.error(cause);
      throw cause;
    }
  }, [run]);

  const getIpStats = useCallback(async (): Promise<IpStats> => {
    const filters = await getIpRules();
    return {
      totalRules: filters.length,
      allowRules: filters.filter(filter => filter.filterType === 'whitelist').length,
      blockRules: filters.filter(filter => filter.filterType === 'blacklist').length,
      activeRules: filters.filter(filter => filter.isEnabled).length,
      lastRuleUpdate: filters.length > 0
        ? new Date(Math.max(...filters.map(filter => new Date(filter.updatedAt).getTime()))).toISOString()
        : null,
    };
  }, [getIpRules]);

  return {
    getIpRules,
    getIpRulesForKey,
    createIpRule,
    createIpRuleForKey,
    updateIpRule,
    deleteIpRule,
    getIpStats,
    isLoading,
    error,
  };
}
