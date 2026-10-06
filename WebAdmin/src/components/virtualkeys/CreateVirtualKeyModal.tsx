'use client';

import {
  Modal,
  TextInput,
  Button,
  Text,
  JsonInput,
  Alert,
  MultiSelect,
  Divider,
  Stack,
  Group,
  Select,
  Anchor,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconInfoCircle } from '@tabler/icons-react';
import { useState, useEffect } from 'react';
import { validators } from '@/lib/utils/form-validators';
import { notify } from '@/lib/notifications';
import type { VirtualKeyGroupDto } from '@/lib/admin-api';
import { withAdminClient } from '@/lib/client/adminClient';
import { RateLimitFields } from './RateLimitFields';

interface CreateVirtualKeyModalProps {
  opened: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}

interface CreateVirtualKeyForm {
  keyName: string;
  virtualKeyGroupId?: number;
  rateLimitRpm?: number;
  rateLimitRpd?: number;
  rateLimitTpm?: number;
  maxParallelRequests?: number;
  allowedModels: string[];
  metadata?: string;
}

export function CreateVirtualKeyModal({ opened, onClose, onSuccess }: CreateVirtualKeyModalProps) {
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [groups, setGroups] = useState<VirtualKeyGroupDto[]>([]);
  const [isLoadingGroups, setIsLoadingGroups] = useState(false);
  const [modelOptions, setModelOptions] = useState<{ value: string; label: string }[]>([
    { value: '*', label: 'All Models' },
  ]);

  // Fetch groups and the deployment's actual model aliases when the modal opens
  useEffect(() => {
    const fetchGroups = async () => {
      if (!opened) return;

      try {
        setIsLoadingGroups(true);
        const data = await withAdminClient(client =>
          client.virtualKeyGroups.list()
        );
        setGroups(data.data ?? []);
      } catch (error) {
        console.warn('Failed to fetch virtual key groups:', error);
      } finally {
        setIsLoadingGroups(false);
      }
    };

    const fetchModels = async () => {
      if (!opened) return;

      try {
        const mappings = await withAdminClient(client => client.modelMappings.list());
        const aliases = [...new Set(mappings.map(m => m.modelAlias))].sort();
        setModelOptions([
          { value: '*', label: 'All Models' },
          ...aliases.map(alias => ({ value: alias, label: alias })),
        ]);
      } catch (error) {
        // Leave only the "All Models" wildcard rather than offering a made-up list
        console.warn('Failed to fetch model mappings:', error);
      }
    };

    void fetchGroups();
    void fetchModels();
  }, [opened]);

  const form = useForm<CreateVirtualKeyForm>({
    initialValues: {
      keyName: '',
      virtualKeyGroupId: undefined,
      rateLimitRpm: undefined,
      rateLimitRpd: undefined,
      rateLimitTpm: undefined,
      maxParallelRequests: undefined,
      allowedModels: ['*'], // Default to all models
      metadata: '',
    },
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
      virtualKeyGroupId: (value) => {
        if (!value) return 'Virtual Key Group is required';
        return null;
      },
      rateLimitRpm: validators.minValue('Requests per minute', 1),
      rateLimitRpd: validators.minValue('Requests per day', 1),
      rateLimitTpm: validators.minValue('Tokens per minute', 1),
      maxParallelRequests: validators.minValue('Max parallel requests', 1),
      allowedModels: validators.arrayMinLength('model', 1),
      metadata: validators.jsonObject('Metadata'),
    },
  });

  const handleSubmit = async (values: CreateVirtualKeyForm) => {
    setIsSubmitting(true);
    try {
      // Ensure required fields are properly validated
      if (!values.virtualKeyGroupId) {
        form.setFieldError('virtualKeyGroupId', 'Virtual Key Group is required');
        return;
      }

      const payload = {
        keyName: values.keyName.trim(),
        virtualKeyGroupId: values.virtualKeyGroupId, // Now guaranteed to be number
        rateLimitRpm: values.rateLimitRpm ?? undefined,
        rateLimitRpd: values.rateLimitRpd ?? undefined,
        rateLimitTpm: values.rateLimitTpm ?? undefined,
        maxParallelRequests: values.maxParallelRequests ?? undefined,
        allowedModels: values.allowedModels.length > 0 ? values.allowedModels : undefined,
        metadata: values.metadata?.trim() ? JSON.parse(values.metadata) as Record<string, unknown> : undefined,
      };

      await withAdminClient(client => 
        client.virtualKeys.create(payload)
      );

      notify.success('Virtual key created successfully');
      
      handleClose();
      if (onSuccess) {
        onSuccess();
      }
    } catch (error) {
      notify.error(error, 'Failed to create virtual key');
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleClose = () => {
    setShowAdvanced(false);
    form.reset();
    onClose();
  };

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title="Create Virtual Key"
      size="lg"
    >
      <form onSubmit={form.onSubmit(handleSubmit)}>
        <Stack gap="md">
      <TextInput
        label="Key Name"
        placeholder="Enter a unique name for this key"
        required
        {...form.getInputProps('keyName')}
      />

      {groups.length === 0 && !isLoadingGroups ? (
        <Alert icon={<IconInfoCircle size={16} />} color="yellow">
          <Text size="sm">
            No Virtual Key Groups found.{' '}
            <Anchor href="/virtualkeys/groups" size="sm">
              Create a group first
            </Anchor>{' '}
            to organize your keys.
          </Text>
        </Alert>
      ) : (
        <Select
          label="Virtual Key Group"
          description="Select the group this key belongs to"
          placeholder={isLoadingGroups ? "Loading groups..." : "Select a group"}
          required
          disabled={isLoadingGroups}
          data={groups.map(group => ({
            value: group.id.toString(),
            label: `${group.groupName} (Balance: $${group.balance.toFixed(2)})`
          }))}
          value={form.values.virtualKeyGroupId?.toString() ?? null}
          onChange={(value) => form.setFieldValue('virtualKeyGroupId', value ? parseInt(value, 10) : undefined)}
          error={form.errors.virtualKeyGroupId}
        />
      )}

      <Button
        variant="subtle"
        onClick={() => setShowAdvanced(!showAdvanced)}
        mb="md"
      >
        {showAdvanced ? 'Hide' : 'Show'} Advanced Settings
      </Button>

      {showAdvanced && (
        <>
          <Divider mb="md" />

          <Text size="sm" fw={500}>Rate limits</Text>
          <RateLimitFields
            values={{
              rateLimitRpm: form.values.rateLimitRpm,
              rateLimitRpd: form.values.rateLimitRpd,
              rateLimitTpm: form.values.rateLimitTpm,
              maxParallelRequests: form.values.maxParallelRequests,
            }}
            onChange={(field, value) => form.setFieldValue(field, value)}
          />

          <MultiSelect
            label="Allowed Models"
            description="Models this key can access"
            data={modelOptions}
            placeholder="Select models"
            searchable
            clearable
            required
            {...form.getInputProps('allowedModels')}
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

          <Alert icon={<IconInfoCircle size={16} />} color="blue" mt="md">
            <Text size="sm">
              Advanced settings help you control access and usage. Leave empty for default values.
            </Text>
          </Alert>
        </>
      )}
      
      <Group justify="flex-end" mt="md">
        <Button variant="subtle" onClick={handleClose}>
          Cancel
        </Button>
        <Button 
          type="submit" 
          loading={isSubmitting}
          disabled={groups.length === 0 && !isLoadingGroups}
        >
          Create Key
        </Button>
      </Group>
      </Stack>
      </form>
    </Modal>
  );
}
