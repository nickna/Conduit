'use client';

import {
  Modal,
  TextInput,
  NumberInput,
  Switch,
  Stack,
  Text,
  JsonInput,
  Alert,
  Button,
  Divider,
  Group,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconAlertCircle } from '@tabler/icons-react';
import { validators } from '@/lib/utils/form-validators';
import { useEffect, useRef } from 'react';

import type { VirtualKeyDto } from '@/lib/admin-api';
import { withAdminClient } from '@/lib/client/adminClient';
import { useFormModal } from '@/hooks/useFormModal';
import { RateLimitFields } from './RateLimitFields';

interface EditVirtualKeyModalProps {
  opened: boolean;
  onClose: () => void;
  virtualKey: VirtualKeyDto | null;
  onSuccess?: () => void;
}

interface EditVirtualKeyForm {
  keyName: string;
  metadata: string;
  virtualKeyGroupId?: number;
  isEnabled: boolean;
  allowedModels: string[];
  rateLimitRpm?: number;
  rateLimitRpd?: number;
  rateLimitTpm?: number;
  maxParallelRequests?: number;
}

export function EditVirtualKeyModal({ opened, onClose, virtualKey, onSuccess }: EditVirtualKeyModalProps) {
  const initialFormValues: EditVirtualKeyForm = {
    keyName: '',
    metadata: '',
    virtualKeyGroupId: undefined,
    isEnabled: true,
    allowedModels: [],
    rateLimitRpm: undefined,
    rateLimitRpd: undefined,
    rateLimitTpm: undefined,
    maxParallelRequests: undefined,
  };
  const lastVirtualKeyId = useRef<number | undefined>(undefined);

  const form = useForm<EditVirtualKeyForm>({
    initialValues: initialFormValues,
    validate: {
      keyName: (value) => {
        const requiredError = validators.required('Key name')(value);
        if (requiredError) return requiredError;

        const minLengthError = validators.minLength('Key name', 3)(value);
        if (minLengthError) return minLengthError;

        const maxLengthError = validators.maxLength('Key name', 100)(value);
        if (maxLengthError) return maxLengthError;

        return null;
      },
      virtualKeyGroupId: validators.positiveNumber('Virtual Key Group'),
      rateLimitRpm: validators.minValue('Requests per minute', 1),
      rateLimitRpd: validators.minValue('Requests per day', 1),
      rateLimitTpm: validators.minValue('Tokens per minute', 1),
      maxParallelRequests: validators.minValue('Max parallel requests', 1),
      metadata: validators.jsonObject('Metadata'),
    },
  });

  const { loading, handleSubmit, handleClose } = useFormModal({
    form,
    onClose,
    onSuccess,
    submitAction: (values) => {
      if (!virtualKey) return Promise.resolve();

      const payload = {
        keyName: values.keyName.trim(),
        virtualKeyGroupId: values.virtualKeyGroupId ?? undefined,
        isEnabled: values.isEnabled,
        allowedModels: values.allowedModels.length > 0 ? values.allowedModels : undefined,
        rateLimitRpm: values.rateLimitRpm ?? undefined,
        rateLimitRpd: values.rateLimitRpd ?? undefined,
        rateLimitTpm: values.rateLimitTpm ?? undefined,
        maxParallelRequests: values.maxParallelRequests ?? undefined,
        metadata: values.metadata.trim()
          ? JSON.parse(values.metadata) as Record<string, unknown>
          : undefined,
      };

      return withAdminClient(client =>
        client.virtualKeys.update(virtualKey.id.toString(), payload)
      );
    },
    successMessage: 'Virtual key updated successfully',
  });

  // Reset tracking when modal closes
  useEffect(() => {
    if (!opened) {
      lastVirtualKeyId.current = undefined;
    }
  }, [opened]);

  // Update form when virtualKey changes
  useEffect(() => {
    if (!virtualKey) return;

    // Only update if this is a different virtualKey than last time
    if (lastVirtualKeyId.current === virtualKey.id) return;
    lastVirtualKeyId.current = virtualKey.id;

    const models = virtualKey.allowedModels?.filter((model) => model.trim()) ?? ['*'];

    const newFormValues: EditVirtualKeyForm = {
      keyName: virtualKey.keyName,
      metadata: virtualKey.metadata ? JSON.stringify(virtualKey.metadata, null, 2) : '',
      virtualKeyGroupId: virtualKey.virtualKeyGroupId ?? undefined,
      isEnabled: virtualKey.isEnabled,
      allowedModels: models,
      rateLimitRpm: virtualKey.rateLimitRpm ?? undefined,
      rateLimitRpd: virtualKey.rateLimitRpd ?? undefined,
      rateLimitTpm: virtualKey.rateLimitTpm ?? undefined,
      maxParallelRequests: virtualKey.maxParallelRequests ?? undefined,
    };

    form.setValues(newFormValues);
    form.resetDirty();
  }, [virtualKey, form]);

  if (!virtualKey) {
    return null;
  }

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title="Edit Virtual Key"
      size="lg"
    >
      <form onSubmit={form.onSubmit(handleSubmit)}>
        <Stack gap="md">
        <Alert icon={<IconAlertCircle size={16} />} color="blue">
          <Text size="sm" fw={500}>Key Prefix</Text>
          <Text size="xs" style={{ fontFamily: 'monospace' }}>
            {virtualKey.keyPrefix ?? 'N/A'}
          </Text>
        </Alert>

        <TextInput
          label="Key Name"
          placeholder="Enter a unique name for this key"
          required
          {...form.getInputProps('keyName')}
        />

        <JsonInput
          label="Metadata"
          description="Additional metadata stored as a JSON object"
          placeholder='{"team": "engineering"}'
          autosize
          minRows={3}
          formatOnBlur
          {...form.getInputProps('metadata')}
        />

        <Switch
          label="Enabled"
          description="Whether this key can be used for API requests"
          {...form.getInputProps('isEnabled', { type: 'checkbox' })}
        />

        <NumberInput
          label="Virtual Key Group"
          description="Group ID this key belongs to"
          placeholder="Group ID"
          min={1}
          step={1}
          {...form.getInputProps('virtualKeyGroupId')}
        />

        <Alert icon={<IconAlertCircle size={16} />} color="gray">
          <Text size="sm">
            Virtual Key Group ID: {virtualKey.virtualKeyGroupId}
          </Text>
        </Alert>

        <Divider label="Rate limits" labelPosition="left" />

        <RateLimitFields
          values={{
            rateLimitRpm: form.values.rateLimitRpm,
            rateLimitRpd: form.values.rateLimitRpd,
            rateLimitTpm: form.values.rateLimitTpm,
            maxParallelRequests: form.values.maxParallelRequests,
          }}
          onChange={(field, value) => form.setFieldValue(field, value)}
        />

        <Group justify="flex-end" mt="md">
          <Button variant="subtle" onClick={handleClose}>
            Cancel
          </Button>
          <Button type="submit" loading={loading}>
            Save Changes
          </Button>
        </Group>
      </Stack>
      </form>
    </Modal>
  );
}
