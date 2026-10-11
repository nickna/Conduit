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
  TextInput,
  Textarea,
  NumberInput,
  Checkbox,
  Modal,
  Card,
  LoadingOverlay,
  ActionIcon,
  Menu,
  Grid
} from '@mantine/core';
import {
  IconPlus,
  IconRefresh,
  IconEdit,
  IconTrash,
  IconSearch,
  IconDots,
  IconTrashX
} from '@tabler/icons-react';
import { notify } from '@/lib/notifications';
import { modals } from '@mantine/modals';
import { useAdminClient } from '@/lib/client/adminClient';
import {
  FunctionCostDto,
  CreateFunctionCostDto,
  UpdateFunctionCostDto,
  FunctionProviderType,
  FunctionPurpose,
  FunctionPricingModel,
  getProviderTypeName,
  getPurposeName,
  getPricingModelName,
  getAvailableFunctionProviders,
} from '../types';

// Pricing config templates
const PRICING_TEMPLATES: Record<FunctionPricingModel, string> = {
  [FunctionPricingModel.FlatRate]: JSON.stringify({
    pricingModel: FunctionPricingModel.FlatRate,
    costPerExecution: 0.001
  }, null, 2),
  [FunctionPricingModel.PerResult]: JSON.stringify({
    pricingModel: FunctionPricingModel.PerResult,
    costPerResult: 0.0001,
    minimumCost: 0.001
  }, null, 2),
  [FunctionPricingModel.PerToken]: JSON.stringify({
    pricingModel: FunctionPricingModel.PerToken,
    costPerMillionTokens: 1.0,
    minimumCost: 0.001
  }, null, 2),
  [FunctionPricingModel.TimeBased]: JSON.stringify({
    pricingModel: FunctionPricingModel.TimeBased,
    costPerSecond: 0.0001,
    minimumCost: 0.001,
    roundUpToNearestSecond: true
  }, null, 2),
  [FunctionPricingModel.Tiered]: JSON.stringify({
    pricingModel: FunctionPricingModel.Tiered,
    tiers: [
      { minResults: 1, maxResults: 25, costPerResult: 0.0001 },
      { minResults: 26, maxResults: 100, costPerResult: 0.00008 }
    ],
    baseCost: 0.001
  }, null, 2),
  [FunctionPricingModel.Hybrid]: JSON.stringify({
    pricingModel: FunctionPricingModel.Hybrid,
    neuralSearchCosts: {
      tier1: { minResults: 1, maxResults: 25, costPerResult: 0.002 },
      tier2: { minResults: 26, costPerResult: 0.001 }
    },
    keywordSearchCosts: {
      tier1: { minResults: 1, maxResults: 25, costPerResult: 0.0005 },
      tier2: { minResults: 26, costPerResult: 0.0002 }
    },
    contentExtractionCosts: {
      text: 0.001,
      highlights: 0.002,
      summary: 0.003
    }
  }, null, 2),
};

export default function FunctionCostsPage() {
  const { executeWithAdmin } = useAdminClient();
  const [costs, setCosts] = useState<FunctionCostDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [showModal, setShowModal] = useState(false);
  const [editingCost, setEditingCost] = useState<FunctionCostDto | null>(null);
  const [searchTerm, setSearchTerm] = useState('');

  // Form state
  const [formData, setFormData] = useState<CreateFunctionCostDto>({
    costName: '',
    providerType: FunctionProviderType.Exa,
    purpose: undefined,
    pricingModel: FunctionPricingModel.Hybrid,
    pricingConfiguration: PRICING_TEMPLATES[FunctionPricingModel.Hybrid],
    baseCost: undefined,
    isActive: true,
    priority: 0,
    effectiveDate: new Date().toISOString().split('T')[0],
    expiryDate: undefined,
    description: '',
  });

  const loadCosts = useCallback(async () => {
    try {
      setLoading(true);
      const response = await executeWithAdmin(client =>
        client.functionCosts.list()
      );
      setCosts(response);
    } catch (err) {
      console.warn('Error loading costs:', err);
      notify.error(err, 'Failed to load costs');
    } finally {
      setLoading(false);
    }
  }, [executeWithAdmin]);

  useEffect(() => {
    void loadCosts();
  }, [loadCosts]);

  const handleCreate = async () => {
    try {
      // Validate JSON
      JSON.parse(formData.pricingConfiguration);

      await executeWithAdmin(client =>
        client.functionCosts.create(formData)
      );
      notify.success('Cost configuration created successfully');
      setShowModal(false);
      resetForm();
      await loadCosts();
    } catch (err) {
      console.warn('Error creating cost:', err);
      notify.error(err, 'Failed to create cost');
    }
  };

  const handleUpdate = async () => {
    if (!editingCost) return;

    try {
      // Validate JSON
      JSON.parse(formData.pricingConfiguration);

      const updateData: UpdateFunctionCostDto = {
        id: editingCost.id,
        costName: formData.costName,
        purpose: formData.purpose,
        pricingModel: formData.pricingModel,
        pricingConfiguration: formData.pricingConfiguration,
        baseCost: formData.baseCost,
        isActive: formData.isActive,
        priority: formData.priority,
        effectiveDate: formData.effectiveDate,
        expiryDate: formData.expiryDate,
        description: formData.description,
      };
      await executeWithAdmin(client =>
        client.functionCosts.update(editingCost.id, updateData)
      );
      notify.success('Cost configuration updated successfully');
      setShowModal(false);
      setEditingCost(null);
      resetForm();
      await loadCosts();
    } catch (err) {
      console.warn('Error updating cost:', err);
      notify.error(err, 'Failed to update cost');
    }
  };

  const handleDelete = (id: number) => {
    modals.openConfirmModal({
      title: 'Delete Cost Configuration',
      children: (
        <Text size="sm">
          Are you sure you want to delete this cost configuration? This action cannot be undone.
        </Text>
      ),
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red' },
      onConfirm: () => {
        void (async () => {
          try {
            await executeWithAdmin(client =>
              client.functionCosts.deleteById(id)
            );
            notify.success('Cost configuration deleted successfully');
            await loadCosts();
          } catch (err) {
            console.warn('Error deleting cost:', err);
            notify.error(err, 'Failed to delete cost');
          }
        })();
      },
    });
  };

  const handleClearCache = () => {
    modals.openConfirmModal({
      title: 'Clear Cost Calculation Cache',
      children: (
        <Text size="sm">
          Are you sure you want to clear the cost calculation cache? This will force all cost calculations to be recalculated.
        </Text>
      ),
      labels: { confirm: 'Clear Cache', cancel: 'Cancel' },
      confirmProps: { color: 'orange' },
      onConfirm: () => {
        void (async () => {
          try {
            await executeWithAdmin(client =>
              client.functionCosts.clearCache()
            );
            notify.success('Cache cleared successfully');
          } catch (err) {
            console.warn('Error clearing cache:', err);
            notify.error(err, 'Failed to clear cache');
          }
        })();
      },
    });
  };

  const openCreateModal = () => {
    resetForm();
    setEditingCost(null);
    setShowModal(true);
  };

  const openEditModal = (cost: FunctionCostDto) => {
    setEditingCost(cost);
    setFormData({
      costName: cost.costName,
      providerType: cost.providerType,
      purpose: cost.purpose,
      pricingModel: cost.pricingModel,
      pricingConfiguration: cost.pricingConfiguration,
      baseCost: cost.baseCost,
      isActive: cost.isActive,
      priority: cost.priority,
      effectiveDate: cost.effectiveDate.split('T')[0],
      expiryDate: cost.expiryDate ? cost.expiryDate.split('T')[0] : undefined,
      description: cost.description,
    });
    setShowModal(true);
  };

  const resetForm = () => {
    setFormData({
      costName: '',
      providerType: FunctionProviderType.Exa,
      purpose: undefined,
      pricingModel: FunctionPricingModel.Hybrid,
      pricingConfiguration: PRICING_TEMPLATES[FunctionPricingModel.Hybrid],
      baseCost: undefined,
      isActive: true,
      priority: 0,
      effectiveDate: new Date().toISOString().split('T')[0],
      expiryDate: undefined,
      description: '',
    });
  };

  const handlePricingModelChange = (model: FunctionPricingModel) => {
    setFormData({
      ...formData,
      pricingModel: model,
      pricingConfiguration: PRICING_TEMPLATES[model],
    });
  };

  const filteredCosts = (costs ?? []).filter((cost) =>
    cost.costName.toLowerCase().includes(searchTerm.toLowerCase()) ||
    (cost.description ?? '').toLowerCase().includes(searchTerm.toLowerCase())
  );

  return (
    <Container size="xl">
      <Stack gap="md">
        <Group justify="space-between" align="flex-end">
          <div>
            <Title order={2}>Function Cost Management</Title>
            <Text c="dimmed" size="sm" mt={4}>
              Configure pricing models and cost tracking for function executions
            </Text>
          </div>
          <Group gap="xs">
            <Button
              leftSection={<IconTrashX size={16} />}
              variant="subtle"
              color="orange"
              onClick={() => void handleClearCache()}
            >
              Clear Cache
            </Button>
            <Button
              leftSection={<IconRefresh size={16} />}
              variant="subtle"
              onClick={() => void loadCosts()}
            >
              Refresh
            </Button>
            <Button
              leftSection={<IconPlus size={16} />}
              onClick={openCreateModal}
            >
              Create Cost
            </Button>
          </Group>
        </Group>

        <TextInput
          placeholder="Search costs..."
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          leftSection={<IconSearch size={16} />}
        />

        <Card withBorder>
          <LoadingOverlay visible={loading} />
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Name</Table.Th>
                <Table.Th>Provider</Table.Th>
                <Table.Th>Purpose</Table.Th>
                <Table.Th>Pricing Model</Table.Th>
                <Table.Th>Priority</Table.Th>
                <Table.Th>Status</Table.Th>
                <Table.Th>Effective Date</Table.Th>
                <Table.Th>Actions</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {filteredCosts.length === 0 ? (
                <Table.Tr>
                  <Table.Td colSpan={8}>
                    <Text ta="center" c="dimmed" py="xl">
                      No costs found
                    </Text>
                  </Table.Td>
                </Table.Tr>
              ) : (
                filteredCosts.map((cost) => (
                  <Table.Tr key={cost.id}>
                    <Table.Td>
                      <div>
                        <Text fw={500}>{cost.costName}</Text>
                        {cost.description && (
                          <Text size="sm" c="dimmed">{cost.description}</Text>
                        )}
                      </div>
                    </Table.Td>
                    <Table.Td>{getProviderTypeName(cost.providerType)}</Table.Td>
                    <Table.Td>{cost.purpose ? getPurposeName(cost.purpose) : '-'}</Table.Td>
                    <Table.Td>{getPricingModelName(cost.pricingModel)}</Table.Td>
                    <Table.Td>{cost.priority}</Table.Td>
                    <Table.Td>
                      <Badge color={cost.isActive ? 'green' : 'gray'} variant="light">
                        {cost.isActive ? 'Active' : 'Inactive'}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      <div>
                        <Text size="sm" c="dimmed">
                          {new Date(cost.effectiveDate).toLocaleDateString()}
                        </Text>
                        {cost.expiryDate && (
                          <Text size="xs" c="dimmed">
                            Expires: {new Date(cost.expiryDate).toLocaleDateString()}
                          </Text>
                        )}
                      </div>
                    </Table.Td>
                    <Table.Td>
                      <Menu shadow="md" width={200}>
                        <Menu.Target>
                          <ActionIcon variant="subtle" color="gray">
                            <IconDots size={16} />
                          </ActionIcon>
                        </Menu.Target>
                        <Menu.Dropdown>
                          <Menu.Item
                            leftSection={<IconEdit size={14} />}
                            onClick={() => openEditModal(cost)}
                          >
                            Edit
                          </Menu.Item>
                          <Menu.Item
                            leftSection={<IconTrash size={14} />}
                            color="red"
                            onClick={() => void handleDelete(cost.id)}
                          >
                            Delete
                          </Menu.Item>
                        </Menu.Dropdown>
                      </Menu>
                    </Table.Td>
                  </Table.Tr>
                ))
              )}
            </Table.Tbody>
          </Table>
        </Card>
      </Stack>

      <Modal
        opened={showModal}
        onClose={() => {
          setShowModal(false);
          setEditingCost(null);
          resetForm();
        }}
        title={editingCost ? 'Edit Cost Configuration' : 'Create Cost Configuration'}
        size="xl"
      >
        <Grid gap="md">
          <Grid.Col span={6}>
            <Stack gap="md">
              <TextInput
                label="Cost Name"
                placeholder="e.g., Exa Standard Pricing"
                value={formData.costName}
                onChange={(e) => setFormData({ ...formData, costName: e.target.value })}
                required
              />

              <Select
                label="Provider Type"
                value={formData.providerType.toString()}
                onChange={(value) => setFormData({ ...formData, providerType: value as FunctionProviderType })}
                data={getAvailableFunctionProviders().map(p => ({
                  value: p.value.toString(),
                  label: p.label
                }))}
                required
              />

              <Select
                label="Purpose"
                placeholder="Any"
                value={formData.purpose?.toString() ?? ''}
                onChange={(value) => setFormData({ ...formData, purpose: value ? value as FunctionPurpose : undefined })}
                data={[
                  { value: '', label: 'Any' },
                  { value: FunctionPurpose.Search.toString(), label: 'Search' },
                  { value: FunctionPurpose.Answer.toString(), label: 'Answer' },
                  { value: FunctionPurpose.ContentRetrieval.toString(), label: 'Content Retrieval' },
                  { value: FunctionPurpose.RAG.toString(), label: 'RAG' },
                ]}
              />

              <Select
                label="Pricing Model"
                value={formData.pricingModel.toString()}
                onChange={(value) => handlePricingModelChange(value as FunctionPricingModel)}
                data={[
                  { value: FunctionPricingModel.FlatRate.toString(), label: 'Flat Rate' },
                  { value: FunctionPricingModel.PerResult.toString(), label: 'Per Result' },
                  { value: FunctionPricingModel.PerToken.toString(), label: 'Per Token' },
                  { value: FunctionPricingModel.TimeBased.toString(), label: 'Time Based' },
                  { value: FunctionPricingModel.Tiered.toString(), label: 'Tiered' },
                  { value: FunctionPricingModel.Hybrid.toString(), label: 'Hybrid (Exa)' },
                ]}
                required
              />

              <NumberInput
                label="Base Cost"
                placeholder="Optional base cost"
                value={formData.baseCost}
                onChange={(value) => setFormData({ ...formData, baseCost: typeof value === 'number' ? value : undefined })}
                decimalScale={4}
                step={0.0001}
              />

              <NumberInput
                label="Priority"
                description="Higher priority = used first when multiple costs match"
                value={formData.priority}
                onChange={(value) => setFormData({ ...formData, priority: Number(value) })}
                min={0}
              />

              <TextInput
                label="Effective Date"
                type="date"
                value={formData.effectiveDate}
                onChange={(e) => setFormData({ ...formData, effectiveDate: e.target.value })}
                required
              />

              <TextInput
                label="Expiry Date"
                type="date"
                value={formData.expiryDate ?? ''}
                onChange={(e) => setFormData({ ...formData, expiryDate: e.target.value || undefined })}
              />

              <Checkbox
                label="Active"
                checked={formData.isActive}
                onChange={(e) => setFormData({ ...formData, isActive: e.currentTarget.checked })}
              />

              <Textarea
                label="Description"
                placeholder="Optional description"
                value={formData.description}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                rows={3}
              />
            </Stack>
          </Grid.Col>

          <Grid.Col span={6}>
            <Textarea
              label="Pricing Configuration (JSON)"
              value={formData.pricingConfiguration}
              onChange={(e) => setFormData({ ...formData, pricingConfiguration: e.target.value })}
              rows={28}
              styles={{ input: { fontFamily: 'monospace', fontSize: '12px' } }}
              required
            />
          </Grid.Col>
        </Grid>

        <Group justify="flex-end" mt="md">
          <Button
            variant="subtle"
            onClick={() => {
              setShowModal(false);
              setEditingCost(null);
              resetForm();
            }}
          >
            Cancel
          </Button>
          <Button
            onClick={() => void (editingCost ? handleUpdate() : handleCreate())}
          >
            {editingCost ? 'Update' : 'Create'}
          </Button>
        </Group>
      </Modal>
    </Container>
  );
}
