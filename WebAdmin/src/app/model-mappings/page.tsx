'use client';

import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Container, Title, Text, Button, Group, Stack, Tooltip } from '@mantine/core';
import { IconPlus, IconRefresh, IconFileImport, IconTrash } from '@tabler/icons-react';
import { ModelMappingsTable } from '@/components/modelmappings/ModelMappingsTable';
import { CreateModelMappingModal } from '@/components/modelmappings/CreateModelMappingModal';
import { BulkMappingModal } from '@/components/modelmappings/BulkMappingModal';
import { notify } from '@/lib/notifications';
import { useAdminClient } from '@/lib/client/adminClient';

export default function ModelMappingsPage() {
  const { executeWithAdmin } = useAdminClient();
  const queryClient = useQueryClient();
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [bulkModalOpen, setBulkModalOpen] = useState(false);

  const handleRefresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['model-mappings'] });
  };

  const handleInvalidateCache = async () => {
    try {
      notify.loading('invalidating-cache', 'Please wait...', 'Invalidating Discovery Cache');

      const result = await executeWithAdmin(client =>
        client.system.invalidateDiscoveryCache()
      );

      notify.updateLoading('invalidating-cache', {
        success: true,
        message: (result as { message?: string })?.message ?? 'Discovery cache has been successfully cleared',
        title: 'Cache Invalidated',
      });

      // Refresh the table after cache invalidation
      handleRefresh();
    } catch (error) {
      console.error('Failed to invalidate cache:', error);
      notify.updateLoading('invalidating-cache', {
        success: false,
        message: error instanceof Error ? error.message : 'An error occurred while invalidating the cache',
        title: 'Failed to Invalidate Cache',
      });
    }
  };

  return (
    <Container size="xl">
      <Stack gap="md">
        <Group justify="space-between" align="flex-end">
          <div>
            <Title order={2}>Model Mappings</Title>
            <Text c="dimmed" size="sm" mt={4}>
              Configure how models are routed to different providers
            </Text>
          </div>
          <Group>
            <Tooltip label="Clear the discovery cache to force reload of model mappings">
              <Button
                leftSection={<IconTrash size={16} />}
                variant="subtle"
                color="orange"
                onClick={() => void handleInvalidateCache()}
              >
                Clear Cache
              </Button>
            </Tooltip>
            <Button
              leftSection={<IconFileImport size={16} />}
              variant="light"
              onClick={() => setBulkModalOpen(true)}
            >
              Bulk Import
            </Button>
            <Button
              leftSection={<IconRefresh size={16} />}
              variant="subtle"
              onClick={handleRefresh}
            >
              Refresh
            </Button>
            <Button
              leftSection={<IconPlus size={16} />}
              onClick={() => setCreateModalOpen(true)}
            >
              Add Mapping
            </Button>
          </Group>
        </Group>

        <ModelMappingsTable />
      </Stack>

      <CreateModelMappingModal
        isOpen={createModalOpen}
        onClose={() => setCreateModalOpen(false)}
        onSuccess={() => {
          setCreateModalOpen(false);
          handleRefresh();
        }}
      />
      
      <BulkMappingModal
        isOpen={bulkModalOpen}
        onClose={() => setBulkModalOpen(false)}
        onSuccess={() => {
          setBulkModalOpen(false);
          handleRefresh();
        }}
      />
    </Container>
  );
}
