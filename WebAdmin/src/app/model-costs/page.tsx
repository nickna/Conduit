'use client';

import { Suspense, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Center, Container, Title, Text, Button, Group, Loader, Stack } from '@mantine/core';
import { IconPlus, IconRefresh, IconFileImport, IconFileExport } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { ModelCostsTable } from './components/ModelCostsTable';
import { ImportModelCostsModal } from './components/ImportModelCostsModal';
import { useExportModelCosts } from './hooks/useModelCostsApi';
import { useProviders } from '@/hooks/useProviderApi';
import { useModelMappings } from '@/hooks/useModelMappingsApi';

export default function ModelCostsPage() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [importModalOpen, setImportModalOpen] = useState(false);
  const exportMutation = useExportModelCosts();
  const { providers } = useProviders();
  const { mappings } = useModelMappings();

  const hasProviders = providers.length > 0;
  const hasModelMappings = mappings.length > 0;

  const handleRefresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['model-costs'] });
  };

  const handleExport = () => exportMutation.mutate('csv');

  return (
    <Container size="xl">
      <Stack gap="md">
        <Group justify="space-between" align="flex-end">
          <div>
            <Title order={2}>Model Pricing Configuration</Title>
            <Text c="dimmed" size="sm" mt={4}>
              Configure pricing for LLM models to enable accurate cost tracking
            </Text>
          </div>
          <Group>
            <Button
              leftSection={<IconFileExport size={16} />}
              variant="light"
              onClick={handleExport}
              loading={exportMutation.isPending}
            >
              Export CSV
            </Button>
            <Button
              leftSection={<IconFileImport size={16} />}
              variant="light"
              onClick={() => setImportModalOpen(true)}
            >
              Import CSV
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
              onClick={() => router.push('/model-costs/add')}
            >
              Add Pricing
            </Button>
          </Group>
        </Group>

        <Suspense fallback={<Center py="xl"><Loader /></Center>}>
          <ModelCostsTable
            hasProviders={hasProviders}
            hasModelMappings={hasModelMappings}
          />
        </Suspense>
      </Stack>
      
      <ImportModelCostsModal
        isOpen={importModalOpen}
        onClose={() => setImportModalOpen(false)}
        onSuccess={() => {
          setImportModalOpen(false);
          handleRefresh();
        }}
      />
    </Container>
  );
}
