import { useState, useCallback } from 'react';
import { useSecurityApi, type IpStats } from '@/hooks/useSecurityApi';
import type { IpFilterDto } from '@/lib/admin-api';
import { notify } from '@/lib/notifications';

export function useIpFilteringData() {
  const [isLoading, setIsLoading] = useState(true);
  const [rules, setRules] = useState<IpFilterDto[]>([]);
  const [stats, setStats] = useState<IpStats | null>(null);
  const { getIpRules, error } = useSecurityApi();

  const fetchIpRules = useCallback(async () => {
    try {
      setIsLoading(true);
      const fetchedRules = await getIpRules();
      setRules(fetchedRules);
      
      // Calculate statistics from the rules data
      const calculatedStats: IpStats = {
        totalRules: fetchedRules.length,
        allowRules: fetchedRules.filter(r => r.filterType === 'whitelist').length,
        blockRules: fetchedRules.filter(r => r.filterType === 'blacklist').length,
        activeRules: fetchedRules.filter(r => r.isEnabled).length,
        lastRuleUpdate: fetchedRules.length > 0
          ? new Date(Math.max(...fetchedRules.map(r => new Date(r.updatedAt).getTime()))).toISOString()
          : null,
      };
      setStats(calculatedStats);
    } catch {
      notify.error('Failed to load IP rules');
    } finally {
      setIsLoading(false);
    }
  }, [getIpRules]);

  return {
    isLoading,
    rules,
    stats,
    error,
    fetchIpRules,
  };
}
