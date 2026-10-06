'use client';

import { useState, useEffect, useCallback, useMemo } from 'react';
import {
  Stack,
  Title,
  Text,
  Group,
  Button,
  Card,
  ThemeIcon,
  LoadingOverlay,
  Alert,
  Menu,
  rem,
} from '@mantine/core';
import {
  IconListDetails,
  IconAlertCircle,
  IconDownload,
  IconFileTypeCsv,
  IconJson,
  IconActivity,
  IconCoin,
  IconClock,
  IconCheck,
} from '@tabler/icons-react';
import { StatCardGrid } from '@/components/common/StatCardGrid';
import { notify } from '@/lib/notifications';
import { TablePagination } from '@/components/common/TablePagination';
import { RequestLogsTable } from '@/components/analytics/RequestLogsTable';
import { RequestLogsFilters } from '@/components/analytics/RequestLogsFilters';
import { ViewVirtualKeyModal } from '@/components/virtualkeys/ViewVirtualKeyModal';
import { useRequestLogs, useDistinctModels, type RequestLogEntry, type RequestLogFormFilters } from '@/hooks/useRequestLogs';
import { exportToCSV, exportToJSON, formatDateForExport } from '@/lib/utils/export';
import { withAdminClient } from '@/lib/client/adminClient';
import type { VirtualKeyDto, VirtualKeyGroupDto } from '@/lib/admin-api';

function formatBillingMethodForExport(method: RequestLogEntry['billingMethod']): string {
  if (method === 'providerReportedCost') return 'provider-reported';
  if (method === 'modelCost') return 'model-cost';
  return '';
}

export default function RequestLogsPage() {
  // Pagination state
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);

  // Filter state
  const [filters, setFilters] = useState<RequestLogFormFilters>({});

  // Virtual keys for filter dropdown
  const [virtualKeys, setVirtualKeys] = useState<VirtualKeyDto[]>([]);
  const [virtualKeyGroups, setVirtualKeyGroups] = useState<VirtualKeyGroupDto[]>([]);
  const [selectedVirtualKey, setSelectedVirtualKey] = useState<VirtualKeyDto | null>(null);

  // Fetch request logs
  const {
    logs,
    totalCount,
    isLoading,
    error,
    stats,
    refetch,
  } = useRequestLogs({
    page,
    pageSize,
    filters,
  });

  // Fetch distinct models for filter
  const { models } = useDistinctModels();

  // Fetch virtual keys for filters and request-log identity context.
  useEffect(() => {
    const fetchVirtualKeys = async () => {
      try {
        const result = await withAdminClient((client) => client.virtualKeys.list(1, Number.MAX_SAFE_INTEGER));
        const validKeys = result.items.filter(
          (key): key is VirtualKeyDto => key.id !== undefined && key.id !== null
        );
        setVirtualKeys(validKeys);
      } catch (err) {
        console.warn('Error fetching virtual keys:', err);
      }
    };

    const fetchVirtualKeyGroups = async () => {
      try {
        const groups = await withAdminClient(async (client) => {
          const firstPage = await client.virtualKeyGroups.list({ page: 1, pageSize: 100 });
          const allGroups = [...(firstPage.data ?? [])];
          for (let groupPage = 2; groupPage <= (firstPage.pagination?.totalPages ?? 1); groupPage += 1) {
            const result = await client.virtualKeyGroups.list({ page: groupPage, pageSize: 100 });
            allGroups.push(...(result.data ?? []));
          }
          return allGroups;
        });
        setVirtualKeyGroups(groups);
      } catch (err) {
        console.warn('Error fetching virtual key groups:', err);
      }
    };

    void Promise.all([fetchVirtualKeys(), fetchVirtualKeyGroups()]);
  }, []);

  const exportData = useMemo(() => {
    const keyMap = new Map(virtualKeys.map((key) => [key.id, key]));
    const groupMap = new Map(virtualKeyGroups.map((group) => [group.id, group]));

    return logs.map((log) => {
      const key = keyMap.get(log.virtualKeyId);
      const group = key ? groupMap.get(key.virtualKeyGroupId) : undefined;
      return {
        id: log.id,
        timestamp: formatDateForExport(log.timestamp),
        model: log.modelName,
        providerType: log.providerType ?? '',
        providerId: log.providerId ?? '',
        modelProviderMappingId: log.modelProviderMappingId ?? '',
        requestType: log.requestType,
        inputTokens: log.inputTokens,
        outputTokens: log.outputTokens,
        cachedInputTokens: log.cachedInputTokens ?? '',
        cachedWriteTokens: log.cachedWriteTokens ?? '',
        totalTokens: log.inputTokens + log.outputTokens,
        cost: log.cost,
        billingMethod: formatBillingMethodForExport(log.billingMethod),
        providerReportedCostUsd: log.providerReportedCostUsd ?? '',
        providerCostMarkupMultiplier: log.providerCostMarkupMultiplier ?? '',
        billedAtUtc: log.billedAtUtc ?? '',
        durationMs: log.responseTimeMs,
        statusCode: log.statusCode ?? '',
        virtualKeyId: log.virtualKeyId,
        virtualKeyName: key?.keyName ?? log.userId ?? '',
        virtualKeyPrefix: key?.keyPrefix ?? '',
        customerName: group?.groupName ?? '',
        externalCustomerId: group?.externalGroupId ?? '',
        userId: log.userId ?? '',
        clientIp: log.clientIp ?? '',
        requestPath: log.requestPath ?? '',
        promptCachingEligible: log.promptCachingEligible,
        promptCachingPolicyApplied: log.promptCachingPolicyApplied,
        cachedReadSavings: log.cachedReadSavings,
        cacheWritePremium: log.cacheWritePremium,
        routingAffinityUsed: log.routingAffinityUsed,
        routingDecisionReason: log.routingDecisionReason ?? '',
        routingFailoverCount: log.routingFailoverCount,
        metadata: log.metadata ?? '',
      };
    });
  }, [logs, virtualKeys, virtualKeyGroups]);

  // Handle page change
  const handlePageChange = useCallback((newPage: number) => {
    setPage(newPage);
  }, []);

  // Handle page size change
  const handlePageSizeChange = useCallback((newPageSize: number) => {
    setPageSize(newPageSize);
    setPage(1); // Reset to first page when changing page size
  }, []);

  // Handle filter changes
  const handleFiltersChange = useCallback((newFilters: RequestLogFormFilters) => {
    setFilters(newFilters);
    setPage(1); // Reset to first page when changing filters
  }, []);

  // Export handlers
  const handleExportCSV = useCallback(() => {
    if (logs.length === 0) {
      notify.warning('There are no request logs to export with the current filters', 'No data to export');
      return;
    }

    exportToCSV(exportData, `request-logs-${new Date().toISOString().split('T')[0]}`, [
      { key: 'id', label: 'ID' },
      { key: 'timestamp', label: 'Timestamp' },
      { key: 'model', label: 'Model' },
      { key: 'providerType', label: 'Provider' },
      { key: 'providerId', label: 'Provider ID' },
      { key: 'modelProviderMappingId', label: 'Provider Mapping ID' },
      { key: 'requestType', label: 'Request Type' },
      { key: 'inputTokens', label: 'Input Tokens' },
      { key: 'outputTokens', label: 'Output Tokens' },
      { key: 'cachedInputTokens', label: 'Cached Input Tokens' },
      { key: 'cachedWriteTokens', label: 'Cached Write Tokens' },
      { key: 'totalTokens', label: 'Total Tokens' },
      { key: 'cost', label: 'Cost' },
      { key: 'billingMethod', label: 'Billing Method' },
      { key: 'providerReportedCostUsd', label: 'Provider Reported Cost (USD)' },
      { key: 'providerCostMarkupMultiplier', label: 'Provider Cost Markup' },
      { key: 'billedAtUtc', label: 'Billed At (UTC)' },
      { key: 'durationMs', label: 'Duration (ms)' },
      { key: 'statusCode', label: 'Status Code' },
      { key: 'virtualKeyId', label: 'Virtual Key ID' },
      { key: 'virtualKeyName', label: 'Virtual Key Name' },
      { key: 'virtualKeyPrefix', label: 'Virtual Key Prefix' },
      { key: 'customerName', label: 'Customer' },
      { key: 'externalCustomerId', label: 'External Customer ID' },
      { key: 'userId', label: 'User ID' },
      { key: 'clientIp', label: 'Client IP' },
      { key: 'requestPath', label: 'Request Path' },
      { key: 'promptCachingEligible', label: 'Prompt Caching Eligible' },
      { key: 'promptCachingPolicyApplied', label: 'Prompt Caching Policy Applied' },
      { key: 'cachedReadSavings', label: 'Cached Read Savings' },
      { key: 'cacheWritePremium', label: 'Cache Write Premium' },
      { key: 'routingAffinityUsed', label: 'Routing Affinity Used' },
      { key: 'routingDecisionReason', label: 'Routing Decision Reason' },
      { key: 'routingFailoverCount', label: 'Routing Failover Count' },
      { key: 'metadata', label: 'Metadata' },
    ]);

    notify.success(`Exported ${logs.length} request logs`, 'Export successful');
  }, [exportData, logs.length]);

  const handleExportJSON = useCallback(() => {
    if (logs.length === 0) {
      notify.warning('There are no request logs to export with the current filters', 'No data to export');
      return;
    }

    exportToJSON(exportData, `request-logs-${new Date().toISOString().split('T')[0]}`);

    notify.success(`Exported ${logs.length} request logs`, 'Export successful');
  }, [exportData, logs.length]);

  // Statistics cards
  const statCards = useMemo(() => {
    if (!stats) return [];

    // All but Total Requests are computed from the rows on the current page,
    // so they are labeled as page-scoped — presenting them as period totals
    // would change with every pagination click.
    return [
      {
        title: 'Total Requests',
        value: stats.totalRequests.toLocaleString(),
        icon: IconActivity,
        color: 'blue',
      },
      {
        title: 'Success Rate (page)',
        value: `${stats.successRate.toFixed(1)}%`,
        icon: IconCheck,
        color: 'green',
      },
      {
        title: 'Cost (page)',
        value: `$${stats.totalCost.toFixed(4)}`,
        icon: IconCoin,
        color: 'orange',
      },
      {
        title: 'Avg Duration (page)',
        value: `${Math.round(stats.avgLatency)} ms`,
        icon: IconClock,
        color: 'violet',
      },
    ];
  }, [stats]);

  if (error) {
    return (
      <Stack gap="xl">
        <div>
          <Title order={1}>Request Logs</Title>
          <Text c="dimmed">View individual API request details</Text>
        </div>

        <Alert icon={<IconAlertCircle size={16} />} title="Error loading request logs" color="red">
          {error.message}
        </Alert>
      </Stack>
    );
  }

  return (
    <Stack gap="xl">
      <Group justify="space-between">
        <div>
          <Title order={1}>Request Logs</Title>
          <Text c="dimmed">View individual API request details with full metadata</Text>
        </div>

        <Menu shadow="md" width={200}>
          <Menu.Target>
            <Button variant="light" leftSection={<IconDownload size={16} />}>
              Export
            </Button>
          </Menu.Target>

          <Menu.Dropdown>
            <Menu.Item
              leftSection={<IconFileTypeCsv style={{ width: rem(14), height: rem(14) }} />}
              onClick={handleExportCSV}
            >
              Export as CSV
            </Menu.Item>
            <Menu.Item
              leftSection={<IconJson style={{ width: rem(14), height: rem(14) }} />}
              onClick={handleExportJSON}
            >
              Export as JSON
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>

      {/* Statistics Cards */}
      <StatCardGrid items={statCards} />

      {/* Filters */}
      <Card>
        <Card.Section p="md" withBorder>
          <Group justify="space-between">
            <Group gap="xs">
              <ThemeIcon size="sm" variant="light" color="cyan">
                <IconListDetails size={14} />
              </ThemeIcon>
              <Text fw={600}>Filters</Text>
            </Group>
            <Text size="sm" c="dimmed">
              {totalCount.toLocaleString()} request{totalCount !== 1 ? 's' : ''} found
            </Text>
          </Group>
        </Card.Section>

        <Card.Section p="md">
          <RequestLogsFilters
            filters={filters}
            onFiltersChange={handleFiltersChange}
            models={models}
            virtualKeys={virtualKeys}
            isLoading={isLoading}
            onRefresh={() => void refetch()}
          />
        </Card.Section>
      </Card>

      {/* Request Logs Table */}
      <Card>
        <Card.Section p="md" withBorder>
          <Group justify="space-between">
            <Text fw={600}>Request Logs</Text>
            <Text size="sm" c="dimmed">
              Showing {logs.length} of {totalCount.toLocaleString()} entries
            </Text>
          </Group>
        </Card.Section>

        <Card.Section p="md" pt={0} style={{ position: 'relative' }}>
          <LoadingOverlay visible={isLoading} overlayProps={{ radius: 'sm', blur: 2 }} />
          <RequestLogsTable
            data={logs}
            virtualKeys={virtualKeys}
            virtualKeyGroups={virtualKeyGroups}
            isLoading={isLoading}
            onViewVirtualKey={setSelectedVirtualKey}
          />
          {totalCount > 0 && (
            <TablePagination
              total={totalCount}
              page={page}
              pageSize={pageSize}
              onPageChange={handlePageChange}
              onPageSizeChange={handlePageSizeChange}
              pageSizeOptions={['25', '50', '100']}
            />
          )}
        </Card.Section>
      </Card>

      <ViewVirtualKeyModal
        opened={selectedVirtualKey !== null}
        onClose={() => setSelectedVirtualKey(null)}
        virtualKey={selectedVirtualKey}
        virtualKeyGroup={virtualKeyGroups.find(
          (group) => group.id === selectedVirtualKey?.virtualKeyGroupId
        )}
      />
    </Stack>
  );
}
