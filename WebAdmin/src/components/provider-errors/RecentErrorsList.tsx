import { useState } from 'react';
import {
  Table,
  Badge,
  Text,
  ScrollArea,
  TextInput,
  Select,
  Group,
  Card,
  Collapse,
  ActionIcon,
  Code,
} from '@mantine/core';
import { IconSearch, IconChevronDown, IconChevronRight } from '@tabler/icons-react';
import type { components } from '@/lib/admin-api';
import { getHttpStatusColor } from '@/components/analytics/formatters';

type ProviderErrorDto = components['schemas']['ProviderErrorDto'];

interface RecentErrorsListProps {
  errors: ProviderErrorDto[];
}

export function RecentErrorsList({ errors }: RecentErrorsListProps) {
  const [search, setSearch] = useState('');
  const [filterType, setFilterType] = useState<string | null>(null);
  const [expandedError, setExpandedError] = useState<number | null>(null);

  const errorTypes = Array.from(new Set(errors.map(e => e.errorType ?? 'Unknown')));

  const filteredErrors = errors.filter((error) => {
    const matchesSearch = 
      (error.errorMessage ?? '').toLowerCase().includes(search.toLowerCase()) ||
      (error.providerName ?? '').toLowerCase().includes(search.toLowerCase()) ||
      (error.modelName ?? '').toLowerCase().includes(search.toLowerCase());
    
    const matchesType = !filterType || error.errorType === filterType;
    
    return matchesSearch && matchesType;
  });

  const formatTimestamp = (timestamp: string | undefined | null) => {
    if (!timestamp) return 'N/A';
    const date = new Date(timestamp);
    return date.toLocaleString();
  };

  const getErrorBadgeColor = (errorType: string | undefined) => {
    if (!errorType) return 'gray';
    if (errorType.toLowerCase().includes('fatal')) return 'red';
    if (errorType.toLowerCase().includes('auth')) return 'orange';
    if (errorType.toLowerCase().includes('rate')) return 'yellow';
    if (errorType.toLowerCase().includes('timeout')) return 'blue';
    return 'gray';
  };

  const getHttpStatusBadge = (statusCode: number | undefined | null) => {
    if (!statusCode) return null;

    return (
      <Badge size="xs" color={getHttpStatusColor(statusCode)} variant="dot">
        {statusCode}
      </Badge>
    );
  };

  return (
    <>
      <Group mb="md">
        <TextInput
          placeholder="Search errors..."
          leftSection={<IconSearch size={16} />}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          style={{ flex: 1, maxWidth: 300 }}
        />
        
        <Select
          placeholder="Filter by type"
          value={filterType}
          onChange={setFilterType}
          data={[
            { value: '', label: 'All types' },
            ...errorTypes.map(type => ({ value: type, label: type }))
          ]}
          clearable
          style={{ width: 200 }}
        />
        
        <Text size="sm" c="dimmed">
          Showing {filteredErrors.length} of {errors.length} errors
        </Text>
      </Group>

      <ScrollArea h={400}>
        {filteredErrors.length === 0 ? (
          <Text c="dimmed" ta="center" py="lg">
            No errors found
          </Text>
        ) : (
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={30}></Table.Th>
                <Table.Th>Provider</Table.Th>
                <Table.Th>Error Type</Table.Th>
                <Table.Th>Model</Table.Th>
                <Table.Th>Status</Table.Th>
                <Table.Th>Timestamp</Table.Th>
                <Table.Th>Fatal</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {filteredErrors.map((error, index) => (
                <>
                  <Table.Tr 
                    key={index}
                    style={{ cursor: 'pointer' }}
                    onClick={() => setExpandedError(expandedError === index ? null : index)}
                  >
                    <Table.Td>
                      <ActionIcon variant="transparent" size="sm">
                        {expandedError === index ? (
                          <IconChevronDown size={16} />
                        ) : (
                          <IconChevronRight size={16} />
                        )}
                      </ActionIcon>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" fw={500}>
                        {error.providerName ?? 'Unknown'}
                      </Text>
                      {error.keyName && (
                        <Text size="xs" c="dimmed">
                          Key: {error.keyName}
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Badge
                        size="sm"
                        color={getErrorBadgeColor(error.errorType ?? undefined)}
                        variant="light"
                      >
                        {error.errorType ?? 'Unknown'}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed">
                        {error.modelName ?? '-'}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      {getHttpStatusBadge(error.httpStatusCode)}
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" c="dimmed">
                        {formatTimestamp(error.occurredAt)}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      {error.isFatal && (
                        <Badge color="red" size="xs">
                          Fatal
                        </Badge>
                      )}
                    </Table.Td>
                  </Table.Tr>
                  <Table.Tr>
                    <Table.Td colSpan={7} p={0}>
                      <Collapse expanded={expandedError === index}>
                        <Card p="md" radius={0}>
                          <Text size="sm" fw={500} mb="xs">
                            Error Details:
                          </Text>
                          <Code block>
                            {error.errorMessage ?? 'No error message available'}
                          </Code>
                          {error.keyCredentialId && (
                            <Text size="xs" c="dimmed" mt="xs">
                              Key ID: {error.keyCredentialId}
                            </Text>
                          )}
                        </Card>
                      </Collapse>
                    </Table.Td>
                  </Table.Tr>
                </>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </ScrollArea>
    </>
  );
}