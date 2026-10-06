'use client';

import {
  Modal,
  Text,
  Stack,
  Group,
  Button,
  Checkbox,
  Badge,
} from '@mantine/core';
import { useState, useEffect, useMemo } from 'react';
import type { IpFilterDto } from '@/lib/admin-api';
import type { IpFilterTemplate, IpTemplateRule } from './ipFilterTemplates';

interface IpTemplateModalProps {
  opened: boolean;
  onClose: () => void;
  template: IpFilterTemplate | null;
  existingRules: IpFilterDto[];
  onConfirm: (template: IpFilterTemplate, rulesToCreate: IpTemplateRule[]) => void;
  isLoading?: boolean;
}

export function IpTemplateModal({
  opened,
  onClose,
  template,
  existingRules,
  onConfirm,
  isLoading = false,
}: IpTemplateModalProps) {
  const [selectedCidrs, setSelectedCidrs] = useState<Set<string>>(new Set());

  const existingCidrs = useMemo(() => {
    const set = new Set<string>();
    for (const rule of existingRules) {
      set.add(rule.ipAddressOrCidr);
    }
    return set;
  }, [existingRules]);

  // Reset selections when template changes
  useEffect(() => {
    if (template) {
      const newSelected = new Set<string>();
      for (const rule of template.rules) {
        if (!existingCidrs.has(rule.ipAddressOrCidr)) {
          newSelected.add(rule.ipAddressOrCidr);
        }
      }
      setSelectedCidrs(newSelected);
    }
  }, [template, existingCidrs]);

  if (!template) return null;

  const newRulesCount = template.rules.filter(
    r => !existingCidrs.has(r.ipAddressOrCidr)
  ).length;
  const existingCount = template.rules.length - newRulesCount;
  const allExist = newRulesCount === 0;

  const handleToggle = (cidr: string) => {
    setSelectedCidrs(prev => {
      const next = new Set(prev);
      if (next.has(cidr)) {
        next.delete(cidr);
      } else {
        next.add(cidr);
      }
      return next;
    });
  };

  const handleConfirm = () => {
    const rulesToCreate = template.rules.filter(r =>
      selectedCidrs.has(r.ipAddressOrCidr)
    );
    onConfirm(template, rulesToCreate);
  };

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={template.label}
      size="md"
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          {template.description}
        </Text>

        <Stack gap="xs">
          {template.rules.map(rule => {
            const alreadyExists = existingCidrs.has(rule.ipAddressOrCidr);

            return (
              <Group
                key={rule.ipAddressOrCidr}
                justify="space-between"
                p="xs"
                style={{
                  borderRadius: 'var(--mantine-radius-sm)',
                  backgroundColor: alreadyExists
                    ? 'var(--mantine-color-gray-light)'
                    : undefined,
                  opacity: alreadyExists ? 0.6 : 1,
                }}
              >
                <Checkbox
                  label={
                    <Stack gap={2}>
                      <Group gap="xs">
                        <Text size="sm" fw={500}>
                          {rule.ipAddressOrCidr}
                        </Text>
                        {alreadyExists && (
                          <Badge size="xs" variant="light" color="gray">
                            Already exists
                          </Badge>
                        )}
                      </Group>
                      <Text size="xs" c="dimmed">
                        {rule.description}
                      </Text>
                    </Stack>
                  }
                  checked={
                    alreadyExists ? false : selectedCidrs.has(rule.ipAddressOrCidr)
                  }
                  disabled={alreadyExists}
                  onChange={() => handleToggle(rule.ipAddressOrCidr)}
                />
              </Group>
            );
          })}
        </Stack>

        <Text size="sm" c="dimmed">
          Will create {selectedCidrs.size} rule{selectedCidrs.size !== 1 ? 's' : ''}
          {existingCount > 0 &&
            `, ${existingCount} already exist${existingCount !== 1 ? '' : 's'}`}
        </Text>

        <Group justify="flex-end">
          <Button variant="light" onClick={onClose} disabled={isLoading}>
            Cancel
          </Button>
          <Button
            onClick={handleConfirm}
            loading={isLoading}
            disabled={allExist || selectedCidrs.size === 0}
          >
            Apply Template
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
