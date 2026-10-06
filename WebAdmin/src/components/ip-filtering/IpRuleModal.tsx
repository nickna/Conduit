'use client';

import {
  Modal,
  TextInput,
  Select,
  Textarea,
  Button,
  Group,
  Stack,
  Alert,
  Text,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconAlertCircle } from '@tabler/icons-react';
import type { CreateIpFilterDto, IpFilterDto } from '@/lib/admin-api';
import { getIpOrCidrValidationError } from '@/lib/utils/ip-validation';

interface IpRuleModalProps {
  opened: boolean;
  onClose: () => void;
  onSubmit: (values: CreateIpFilterDto) => Promise<void>;
  rule?: IpFilterDto | null;
  isLoading?: boolean;
}

// Accepts IPv4/IPv6 addresses and CIDR ranges, matching the backend's rules
const validateIpAddress = (value: string) => getIpOrCidrValidationError(value);

export function IpRuleModal({ 
  opened, 
  onClose, 
  onSubmit, 
  rule, 
  isLoading = false 
}: IpRuleModalProps) {
  const isEditing = !!rule;
  
  const form = useForm({
    initialValues: {
      name: rule?.name ?? '',
      ipAddressOrCidr: rule?.ipAddressOrCidr ?? '',
      filterType: rule?.filterType ?? 'blacklist',
      description: rule?.description ?? '',
    },
    validate: {
      ipAddressOrCidr: validateIpAddress,
      description: (value) => {
        if (value && value.length > 500) {
          return 'Description must be 500 characters or less';
        }
        return null;
      },
    },
  });

  const handleSubmit = async (values: typeof form.values) => {
    try {
      await onSubmit({ ...values, isEnabled: rule?.isEnabled ?? true });
      form.reset();
      // Don't close here - let the parent handle it after successful save
    } catch (error) {
      // Re-throw so parent can handle the error properly
      console.error('Failed to save IP rule:', error);
      throw error;
    }
  };

  const handleClose = () => {
    form.reset();
    onClose();
  };

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={isEditing ? 'Edit IP Rule' : 'Add IP Rule'}
      size="md"
    >
      <form onSubmit={form.onSubmit(handleSubmit)}>
        <Stack gap="md">
          <TextInput
            label="Name"
            placeholder="e.g., Office network"
            required
            {...form.getInputProps('name')}
          />

          <TextInput
            label="IP Address / CIDR"
            placeholder="e.g., 192.168.1.1 or 10.0.0.0/24"
            required
            {...form.getInputProps('ipAddressOrCidr')}
            disabled={isEditing}
          />

          {isEditing && (
            <Alert icon={<IconAlertCircle size={16} />} color="blue">
              <Text size="sm">
                IP addresses cannot be changed after creation. 
                To use a different IP address, please create a new rule.
              </Text>
            </Alert>
          )}

          <Select
            label="Action"
            data={[
              { value: 'whitelist', label: 'Allow (Whitelist)' },
              { value: 'blacklist', label: 'Block (Blacklist)' },
            ]}
            required
            {...form.getInputProps('filterType')}
          />

          <Textarea
            label="Description"
            placeholder="Optional description for this rule"
            rows={3}
            {...form.getInputProps('description')}
          />

          <Group justify="flex-end" mt="md">
            <Button variant="light" onClick={handleClose} disabled={isLoading}>
              Cancel
            </Button>
            <Button type="submit" loading={isLoading}>
              {isEditing ? 'Update Rule' : 'Add Rule'}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
