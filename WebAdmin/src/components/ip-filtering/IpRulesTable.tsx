'use client';

import {
  Table,
  Group,
  Text,
  Badge,
  ActionIcon,
  Tooltip,
  Box,
  Menu,
  rem,
  Checkbox,
} from '@mantine/core';
import {
  IconEdit,
  IconTrash,
  IconDotsVertical,
  IconCopy,
  IconToggleLeft,
  IconToggleRight,
} from '@tabler/icons-react';
import { modals } from '@mantine/modals';
import { notify } from '@/lib/notifications';
import { formatters } from '@/lib/utils/formatters';
import type { FilterType, IpFilterDto } from '@/lib/admin-api';

interface IpRulesTableProps {
  data?: IpFilterDto[];
  selectedRules: ReadonlySet<number>;
  allSelected: boolean;
  someSelected: boolean;
  onSelectAll: () => void;
  onSelectRule: (ruleId: number) => void;
  onEdit?: (rule: IpFilterDto) => void;
  onDelete?: (ruleId: number) => void;
  onToggle?: (ruleId: number, enabled: boolean) => void;
}

export function IpRulesTable({ 
  data = [], 
  selectedRules,
  allSelected,
  someSelected,
  onSelectAll,
  onSelectRule,
  onEdit, 
  onDelete,
  onToggle 
}: IpRulesTableProps) {
  const handleCopyIp = (ipAddress: string) => {
    void navigator.clipboard.writeText(ipAddress);
    notify.success('IP address copied to clipboard', 'Copied');
  };

  const handleDelete = (rule: IpFilterDto) => {
    modals.openConfirmModal({
      title: 'Delete IP Rule',
      children: (
        <Text size="sm">
          Are you sure you want to delete the IP rule for &quot;{rule.ipAddressOrCidr}&quot;?
          This action cannot be undone.
        </Text>
      ),
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red' },
      onConfirm: () => onDelete?.(rule.id),
    });
  };

  const handleToggle = (rule: IpFilterDto) => {
    onToggle?.(rule.id, !rule.isEnabled);
  };

  const getActionBadgeColor = (filterType: FilterType) => {
    return filterType === 'whitelist' ? 'green' : 'red';
  };

  const getActionIcon = (filterType: FilterType) => {
    return filterType === 'whitelist' ? '✓' : '✗';
  };

  const rows = data.map((rule) => {
    const isEnabled = rule.isEnabled;
    const isSelected = selectedRules.has(rule.id);

    return (
      <Table.Tr key={rule.id} bg={isSelected ? 'var(--mantine-color-blue-light)' : undefined}>
        <Table.Td>
          <Checkbox
            checked={isSelected}
            onChange={() => onSelectRule(rule.id)}
          />
        </Table.Td>

        <Table.Td>
          <Group gap="xs">
            <Text size="sm" style={{ fontFamily: 'monospace' }}>
              {rule.ipAddressOrCidr}
            </Text>
            <Tooltip label="Copy IP address">
              <ActionIcon
                variant="subtle"
                size="xs"
                onClick={() => handleCopyIp(rule.ipAddressOrCidr)}
              >
                <IconCopy size={14} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </Table.Td>

        <Table.Td>
          <Badge
            color={getActionBadgeColor(rule.filterType)}
            variant="light"
            size="sm"
            leftSection={getActionIcon(rule.filterType)}
          >
            {rule.filterType === 'whitelist' ? 'Allow' : 'Block'}
          </Badge>
        </Table.Td>

        <Table.Td>
          <Text size="sm" lineClamp={1}>
            {rule.description ?? '-'}
          </Text>
        </Table.Td>

        <Table.Td>
          <Badge
            color={isEnabled ? 'green' : 'gray'}
            variant="light"
            size="sm"
          >
            {isEnabled ? 'Enabled' : 'Disabled'}
          </Badge>
        </Table.Td>

        <Table.Td>
          <Text size="sm" c="dimmed">
            {formatters.date(rule.createdAt)}
          </Text>
        </Table.Td>

        <Table.Td>
          <Text size="xs" c="dimmed">Not tracked</Text>
        </Table.Td>

        <Table.Td>
          <Group gap={0} justify="flex-end">
            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <ActionIcon variant="subtle" color="gray" size="sm">
                  <IconDotsVertical style={{ width: rem(16), height: rem(16) }} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item
                  leftSection={
                    isEnabled 
                      ? <IconToggleLeft style={{ width: rem(14), height: rem(14) }} />
                      : <IconToggleRight style={{ width: rem(14), height: rem(14) }} />
                  }
                  onClick={() => handleToggle(rule)}
                >
                  {isEnabled ? 'Disable' : 'Enable'}
                </Menu.Item>
                <Menu.Item
                  leftSection={<IconEdit style={{ width: rem(14), height: rem(14) }} />}
                  onClick={() => onEdit?.(rule)}
                >
                  Edit
                </Menu.Item>
                <Menu.Divider />
                <Menu.Item
                  color="red"
                  leftSection={<IconTrash style={{ width: rem(14), height: rem(14) }} />}
                  onClick={() => handleDelete(rule)}
                >
                  Delete
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </Table.Td>
      </Table.Tr>
    );
  });

  if (data.length === 0) {
    return (
      <Box p="xl" style={{ textAlign: 'center' }}>
        <Text c="dimmed">No IP rules found. Add your first IP rule to get started.</Text>
      </Box>
    );
  }

  return (
    <Table.ScrollContainer minWidth={900}>
      <Table verticalSpacing="sm" horizontalSpacing="md">
        <Table.Thead>
          <Table.Tr>
            <Table.Th w={40}>
              <Checkbox
                checked={allSelected}
                indeterminate={someSelected}
                onChange={onSelectAll}
              />
            </Table.Th>
            <Table.Th>IP Address / CIDR</Table.Th>
            <Table.Th>Action</Table.Th>
            <Table.Th>Description</Table.Th>
            <Table.Th>Status</Table.Th>
            <Table.Th>Created</Table.Th>
            <Table.Th>Activity</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>{rows}</Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
