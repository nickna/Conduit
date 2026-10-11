'use client';

import { useState, useEffect, useCallback } from 'react';
import {
  Container,
  Title,
  Text,
  Button,
  Group,
  Stack,
  Table,
  Badge,
  Select,
  Modal,
  Card,
  LoadingOverlay,
  ActionIcon,
  Checkbox,
  Grid,
  Code
} from '@mantine/core';
import {
  IconRefresh,
  IconEye,
  IconTrash
} from '@tabler/icons-react';
import { notify } from '@/lib/notifications';
import { modals } from '@mantine/modals';
import { useAdminClient } from '@/lib/client/adminClient';
import {
  FunctionExecutionDto,
  FunctionConfigurationDto,
  ExecutionState,
  getExecutionStateName,
  getExecutionStateBadgeColor,
} from '../types';
import { formatCost, formatDuration } from '@/lib/utils/formatters';

export default function FunctionExecutionsPage() {
  const { executeWithAdmin } = useAdminClient();
  const [executions, setExecutions] = useState<FunctionExecutionDto[]>([]);
  const [configurations, setConfigurations] = useState<FunctionConfigurationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [selectedExecution, setSelectedExecution] = useState<FunctionExecutionDto | null>(null);

  // Filters
  const [filterState, setFilterState] = useState<string>('all');
  const [filterConfigId, setFilterConfigId] = useState<string>('all');
  const [autoRefresh, setAutoRefresh] = useState(false);

  const loadConfigurations = useCallback(async () => {
    try {
      const response = await executeWithAdmin(client =>
        client.functionConfigurations.list()
      );
      setConfigurations(response);
    } catch (err) {
      console.warn('Error loading configurations:', err);
    }
  }, [executeWithAdmin]);

  const loadExecutions = useCallback(async () => {
    try {
      setLoading(true);
      let response;
      if (filterState !== 'all') {
        response = await executeWithAdmin(client =>
          client.functionExecutions.getByState(filterState as ExecutionState)
        );
      } else if (filterConfigId !== 'all') {
        response = await executeWithAdmin(client =>
          client.functionExecutions.getByConfiguration(Number(filterConfigId))
        );
      } else {
        // "All States" means all states: the Admin API has no unfiltered list
        // endpoint, so fetch every state and merge. Silently showing only
        // Completed here hid failed/running executions behind an "All States"
        // filter label.
        const allStates = Object.values(ExecutionState);
        const byState = await Promise.all(
          allStates.map(state =>
            executeWithAdmin(client => client.functionExecutions.getByState(state))
          )
        );
        response = byState
          .flat()
          .sort((a, b) =>
            new Date(b.startedAt ?? 0).getTime() - new Date(a.startedAt ?? 0).getTime()
          );
      }

      setExecutions(response);
    } catch (err) {
      console.warn('Error loading executions:', err);
      notify.error(err, 'Failed to load executions');
    } finally {
      setLoading(false);
    }
  }, [filterState, filterConfigId, executeWithAdmin]);

  const loadData = useCallback(async () => {
    await Promise.all([loadConfigurations(), loadExecutions()]);
  }, [loadConfigurations, loadExecutions]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  useEffect(() => {
    if (!autoRefresh) return;

    const interval = setInterval(() => {
      void loadExecutions();
    }, 5000); // Refresh every 5 seconds

    return () => clearInterval(interval);
  }, [autoRefresh, filterState, filterConfigId, loadExecutions]);

  const handleCleanup = () => {
    modals.openConfirmModal({
      title: 'Cleanup Old Executions',
      children: (
        <Stack gap="md">
          <Text size="sm">
            Enter the number of days. Executions older than this will be permanently deleted.
          </Text>
          <Text size="sm" c="dimmed">
            Default: 30 days
          </Text>
        </Stack>
      ),
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red' },
      onConfirm: () => {
        void (async () => {
          try {
            const result = await executeWithAdmin(client =>
              client.functionExecutions.cleanup(30)
            );
            notify.success(`Deleted ${(result as { deletedCount?: number }).deletedCount ?? 0} executions`);
            await loadExecutions();
          } catch (err) {
            console.warn('Error cleaning up executions:', err);
            notify.error(err, 'Failed to cleanup executions');
          }
        })();
      },
    });
  };

  const getConfigurationName = (configId: number): string => {
    const config = configurations.find((c) => c.id === configId);
    return config?.configurationName ?? `Config #${configId}`;
  };

  return (
    <Container size="xl">
      <Stack gap="md">
        <Group justify="space-between" align="flex-end">
          <div>
            <Title order={2}>Function Executions</Title>
            <Text c="dimmed" size="sm" mt={4}>
              Monitor and track function execution history
            </Text>
          </div>
          <Group gap="xs">
            <Checkbox
              label="Auto Refresh"
              checked={autoRefresh}
              onChange={(e) => setAutoRefresh(e.currentTarget.checked)}
            />
            <Button
              leftSection={<IconRefresh size={16} />}
              variant="subtle"
              onClick={() => void loadExecutions()}
            >
              Refresh
            </Button>
            <Button
              leftSection={<IconTrash size={16} />}
              color="red"
              onClick={() => void handleCleanup()}
            >
              Cleanup Old
            </Button>
          </Group>
        </Group>

        <Card withBorder>
          <Card.Section p="md" withBorder>
            <Group gap="md">
              <Select
                label="State"
                placeholder="All States"
                value={filterState}
                onChange={(value) => {
                  setFilterState(value ?? 'all');
                  setFilterConfigId('all');
                  void loadExecutions();
                }}
                data={[
                  { value: 'all', label: 'All States' },
                  { value: ExecutionState.Pending.toString(), label: 'Pending' },
                  { value: ExecutionState.Running.toString(), label: 'Running' },
                  { value: ExecutionState.Completed.toString(), label: 'Completed' },
                  { value: ExecutionState.Failed.toString(), label: 'Failed' },
                  { value: ExecutionState.Cancelled.toString(), label: 'Cancelled' },
                ]}
                style={{ flex: 1 }}
              />
              <Select
                label="Configuration"
                placeholder="All Configurations"
                value={filterConfigId}
                onChange={(value) => {
                  setFilterConfigId(value ?? 'all');
                  setFilterState('all');
                  void loadExecutions();
                }}
                data={[
                  { value: 'all', label: 'All Configurations' },
                  ...(configurations ?? []).map((config) => ({
                    value: config.id.toString(),
                    label: config.configurationName,
                  })),
                ]}
                style={{ flex: 1 }}
              />
            </Group>
          </Card.Section>
        </Card>

        <Card withBorder>
          <LoadingOverlay visible={loading} />
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>ID</Table.Th>
                <Table.Th>Configuration</Table.Th>
                <Table.Th>Virtual Key</Table.Th>
                <Table.Th>State</Table.Th>
                <Table.Th>Duration</Table.Th>
                <Table.Th>Estimated</Table.Th>
                <Table.Th>Actual</Table.Th>
                <Table.Th>Started</Table.Th>
                <Table.Th>Actions</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(executions ?? []).length === 0 ? (
                <Table.Tr>
                  <Table.Td colSpan={9}>
                    <Text ta="center" c="dimmed" py="xl">
                      {loading ? 'Loading executions...' : 'No executions found'}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              ) : (
                (executions ?? []).map((execution) => (
                  <Table.Tr key={execution.id}>
                    <Table.Td>
                      <Code>{execution.id.substring(0, 8)}...</Code>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{getConfigurationName(execution.functionId)}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{execution.admin.virtualKeyId}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Badge color={getExecutionStateBadgeColor(execution.status)} variant="light">
                        {getExecutionStateName(execution.status)}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{formatDuration(execution.durationMs)}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{formatCost(execution.cost.estimated)}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{formatCost(execution.cost.actual)}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed">
                        {execution.startedAt ? new Date(execution.startedAt).toLocaleString() : '-'}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <ActionIcon
                        variant="subtle"
                        color="blue"
                        onClick={() => setSelectedExecution(execution)}
                      >
                        <IconEye size={16} />
                      </ActionIcon>
                    </Table.Td>
                  </Table.Tr>
                ))
              )}
            </Table.Tbody>
          </Table>
        </Card>
      </Stack>

      <Modal
        opened={!!selectedExecution}
        onClose={() => setSelectedExecution(null)}
        title="Execution Details"
        size="xl"
      >
        {selectedExecution && (
          <Stack gap="md">
            <Grid gap="md">
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">Execution ID</Text>
                <Code>{selectedExecution.id}</Code>
              </Grid.Col>
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">State</Text>
                <Badge color={getExecutionStateBadgeColor(selectedExecution.status)} variant="light" mt={4}>
                  {getExecutionStateName(selectedExecution.status)}
                </Badge>
              </Grid.Col>
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">Configuration</Text>
                <Text size="sm">{getConfigurationName(selectedExecution.functionId)}</Text>
              </Grid.Col>
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">Virtual Key</Text>
                <Text size="sm">{selectedExecution.admin.virtualKeyId}</Text>
              </Grid.Col>
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">Duration</Text>
                <Text size="sm">{formatDuration(selectedExecution.durationMs)}</Text>
              </Grid.Col>
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">Costs</Text>
                <Text size="sm">
                  Estimated: {formatCost(selectedExecution.cost.estimated)}<br />
                  Actual: {formatCost(selectedExecution.cost.actual)}
                </Text>
              </Grid.Col>
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">Started At</Text>
                <Text size="sm">
                  {selectedExecution.startedAt ? new Date(selectedExecution.startedAt).toLocaleString() : '-'}
                </Text>
              </Grid.Col>
              <Grid.Col span={6}>
                <Text size="sm" fw={500} c="dimmed">Completed At</Text>
                <Text size="sm">
                  {selectedExecution.completedAt ? new Date(selectedExecution.completedAt).toLocaleString() : '-'}
                </Text>
              </Grid.Col>
            </Grid>

            {selectedExecution.error && (
              <div>
                <Text size="sm" fw={500} c="dimmed" mb="xs">Error Message</Text>
                <Card withBorder p="md" bg="red.0">
                  <Text size="sm" c="red.7">{selectedExecution.error}</Text>
                </Card>
              </div>
            )}

            {selectedExecution.input !== undefined && selectedExecution.input !== null && (
              <div>
                <Text size="sm" fw={500} c="dimmed" mb="xs">Request</Text>
                <Card withBorder p="md" bg="gray.0">
                  <Code block style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                    {JSON.stringify(selectedExecution.input, null, 2)}
                  </Code>
                </Card>
              </div>
            )}

            {selectedExecution.output !== undefined && selectedExecution.output !== null && (
              <div>
                <Text size="sm" fw={500} c="dimmed" mb="xs">Response</Text>
                <Card withBorder p="md" bg="gray.0">
                  <Code block style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                    {JSON.stringify(selectedExecution.output, null, 2)}
                  </Code>
                </Card>
              </div>
            )}

            {selectedExecution.cost.breakdown !== undefined && selectedExecution.cost.breakdown !== null && (
              <div>
                <Text size="sm" fw={500} c="dimmed" mb="xs">Cost Calculation Details</Text>
                <Card withBorder p="md" bg="gray.0">
                  <Code block style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                    {JSON.stringify(selectedExecution.cost.breakdown, null, 2)}
                  </Code>
                </Card>
              </div>
            )}

            <Group justify="flex-end" mt="md">
              <Button onClick={() => setSelectedExecution(null)}>
                Close
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Container>
  );
}
