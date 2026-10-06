'use client';

import { useCallback, useEffect, useState } from 'react';
import Link from 'next/link';
import { useParams } from 'next/navigation';
import {
  Stack,
  Title,
  Text,
  Card,
  Group,
  Button,
  Table,
  Badge,
  ActionIcon,
  LoadingOverlay,
  Anchor,
  Switch,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconPlus, IconTrash, IconArrowLeft } from '@tabler/icons-react';

import { useSecurityApi } from '@/hooks/useSecurityApi';
import type { CreateIpFilterDto, IpFilterDto } from '@/lib/admin-api';
import { IpRuleModal } from '@/components/ip-filtering/IpRuleModal';
import { notify } from '@/lib/notifications';

export default function VirtualKeyIpFiltersPage() {
  const params = useParams();
  const rawId = params?.id;
  const virtualKeyId = Number(Array.isArray(rawId) ? rawId[0] : rawId);

  const { getIpRulesForKey, createIpRuleForKey, deleteIpRule, updateIpRule } =
    useSecurityApi();

  const [rules, setRules] = useState<IpFilterDto[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [modalOpened, { open: openModal, close: closeModal }] =
    useDisclosure(false);

  const fetchRules = useCallback(async () => {
    if (!Number.isFinite(virtualKeyId)) {
      return;
    }
    try {
      setIsLoading(true);
      const fetched = await getIpRulesForKey(virtualKeyId);
      setRules(fetched);
    } catch {
      notify.error('Failed to load IP filters for this key');
    } finally {
      setIsLoading(false);
    }
  }, [virtualKeyId, getIpRulesForKey]);

  useEffect(() => {
    void fetchRules();
  }, [fetchRules]);

  const handleAdd = async (values: CreateIpFilterDto) => {
    setIsSubmitting(true);
    try {
      await createIpRuleForKey(virtualKeyId, values);
      closeModal();
      await fetchRules();
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleDelete = async (id: number) => {
    await deleteIpRule(id);
    await fetchRules();
  };

  const handleToggle = async (rule: IpFilterDto) => {
    await updateIpRule(rule.id, {
      isEnabled: !rule.isEnabled,
    });
    await fetchRules();
  };

  return (
    <>
      <Stack gap="xl">
        <Card shadow="sm" p="md" radius="md">
          <Group justify="space-between" align="center">
            <div>
              <Anchor component={Link} href="/virtualkeys" size="sm">
                <Group gap={4} component="span">
                  <IconArrowLeft size={14} /> Back to Virtual Keys
                </Group>
              </Anchor>
              <Title order={2} mt={4}>
                Per-Key IP Filters
              </Title>
              <Text size="sm" c="dimmed">
                Allow/deny rules that apply only to virtual key #{virtualKeyId},
                in addition to any global filters. A per-key allow rule restricts
                the key to the listed IPs.
              </Text>
            </div>
            <Button leftSection={<IconPlus size={16} />} onClick={openModal}>
              Add Rule
            </Button>
          </Group>
        </Card>

        <Card shadow="sm" p={0} radius="md" withBorder>
          <div style={{ position: 'relative', minHeight: 200 }}>
            <LoadingOverlay
              visible={isLoading}
              overlayProps={{ radius: 'sm', blur: 2 }}
            />
            <Table verticalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>IP / CIDR</Table.Th>
                  <Table.Th>Action</Table.Th>
                  <Table.Th>Description</Table.Th>
                  <Table.Th>Enabled</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {rules.length === 0 && !isLoading && (
                  <Table.Tr>
                    <Table.Td colSpan={5}>
                      <Text c="dimmed" ta="center" py="md">
                        No per-key IP filters. Add one to restrict this key to
                        specific source IPs.
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                )}
                {rules.map((rule) => (
                  <Table.Tr key={rule.id}>
                    <Table.Td>{rule.ipAddressOrCidr}</Table.Td>
                    <Table.Td>
                      <Badge color={rule.filterType === 'whitelist' ? 'green' : 'red'}>
                        {rule.filterType === 'whitelist' ? 'Allow' : 'Block'}
                      </Badge>
                    </Table.Td>
                    <Table.Td>{rule.description ?? '—'}</Table.Td>
                    <Table.Td>
                      <Switch
                        checked={rule.isEnabled}
                        onChange={() => void handleToggle(rule)}
                        aria-label="Toggle rule enabled"
                      />
                    </Table.Td>
                    <Table.Td>
                      <ActionIcon
                        color="red"
                        variant="subtle"
                        onClick={() => void handleDelete(rule.id)}
                        aria-label="Delete rule"
                      >
                        <IconTrash size={16} />
                      </ActionIcon>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </div>
        </Card>
      </Stack>

      <IpRuleModal
        opened={modalOpened}
        onClose={closeModal}
        onSubmit={handleAdd}
        isLoading={isSubmitting}
      />
    </>
  );
}
