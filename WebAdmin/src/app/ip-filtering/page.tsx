'use client';

import {
  Stack,
  Title,
  Text,
  Card,
  Group,
  Button,
  LoadingOverlay,
  Tabs,
  Badge,
  Menu,
  rem,
} from '@mantine/core';
import {
  IconPlus,
  IconDownload,
  IconUpload,
  IconFilter,
  IconShieldCheck,
  IconShieldX,
  IconFileTypeCsv,
  IconJson,
  IconTestPipe,
  IconChevronDown,
  IconTemplate,
} from '@tabler/icons-react';
import { useState, useEffect } from 'react';
import { useDisclosure } from '@mantine/hooks';
import type { CreateIpFilterDto, IpFilterDto } from '@/lib/admin-api';
import { IpRulesTable } from '@/components/ip-filtering/IpRulesTable';
import { IpRuleModal } from '@/components/ip-filtering/IpRuleModal';
import { IpTestModal } from '@/components/ip-filtering/IpTestModal';
import { IpTemplateModal } from '@/components/ip-filtering/IpTemplateModal';
import { IpFilterPolicyBanner } from '@/components/ip-filtering/IpFilterPolicyBanner';
import { ipFilterTemplates, type IpFilterTemplate } from '@/components/ip-filtering/ipFilterTemplates';
import { useIpFilteringData } from './hooks';
import { useIpFilteringHandlers } from './handlers';
import { IpFilteringStats } from './IpFilteringStats';
import { useBulkSelection } from '@/hooks/useBulkSelection';

const getRuleId = (rule: IpFilterDto) => rule.id;

export default function IpFilteringPage() {
  const [activeTab, setActiveTab] = useState<string | null>('all');
  const [selectedRule, setSelectedRule] = useState<IpFilterDto | null>(null);
  const [modalOpened, { open: openModal, close: closeModal }] = useDisclosure(false);
  const [testModalOpened, { open: openTestModal, close: closeTestModal }] = useDisclosure(false);
  const [templateModalOpened, { open: openTemplateModal, close: closeTemplateModal }] = useDisclosure(false);
  const [selectedTemplate, setSelectedTemplate] = useState<IpFilterTemplate | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  
  const { isLoading, rules, stats, fetchIpRules } = useIpFilteringData();

  // Filter rules based on active tab
  const filteredRules = rules.filter(rule => {
    if (activeTab === 'allow') return rule.filterType === 'whitelist';
    if (activeTab === 'block') return rule.filterType === 'blacklist';
    return true;
  });
  const {
    selectedKeys,
    selectedCount,
    isAllSelected,
    isIndeterminate,
    toggleAll,
    toggleOne,
    clearSelection,
    deselect,
  } = useBulkSelection({
    items: filteredRules,
    getKey: getRuleId,
  });
  const selectedRules = Array.from(selectedKeys);
  const {
    handleBulkOperation,
    handleExport,
    handleImport,
    handleDeleteRule,
    handleToggleRule,
    handleModalSubmit,
    handleApplyTemplate,
  } = useIpFilteringHandlers(fetchIpRules, clearSelection, deselect);

  useEffect(() => {
    void fetchIpRules();
  }, [fetchIpRules]);

  const handleTestIp = () => {
    openTestModal();
  };

  const handleEditRule = (rule: IpFilterDto) => {
    setSelectedRule(rule);
    openModal();
  };

  const handleModalSubmitWrapper = async (values: CreateIpFilterDto) => {
    try {
      await handleModalSubmit(values, selectedRule, setIsSubmitting);
      closeModal();
      setSelectedRule(null);
    } catch {
      // Don't close modal on error so user can fix and retry
    }
  };

  return (
    <>
      <Stack gap="xl">
      {/* Header */}
      <Card shadow="sm" p="md" radius="md">
        <Group justify="space-between" align="center">
          <div>
            <Title order={2}>IP Filtering</Title>
            <Text size="sm" c="dimmed" mt={4}>
              Manage IP access control and filtering rules
            </Text>
          </div>
          
          <Group gap="sm">
            <Button
              variant="light"
              leftSection={<IconTestPipe size={16} />}
              onClick={handleTestIp}
            >
              Test IP
            </Button>
            
            <Menu shadow="md" width={200}>
              <Menu.Target>
                <Button 
                  variant="light" 
                  leftSection={<IconDownload size={16} />}
                >
                  Export
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item
                  leftSection={<IconJson style={{ width: rem(14), height: rem(14) }} />}
                  onClick={() => void handleExport('json')}
                >
                  Export as JSON
                </Menu.Item>
                <Menu.Item
                  leftSection={<IconFileTypeCsv style={{ width: rem(14), height: rem(14) }} />}
                  onClick={() => void handleExport('csv')}
                >
                  Export as CSV
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
            
            <Button
              variant="light"
              leftSection={<IconUpload size={16} />}
              onClick={handleImport}
            >
              Import
            </Button>
            
            <Button.Group>
              <Button
                leftSection={<IconPlus size={16} />}
                onClick={() => {
                  setSelectedRule(null);
                  openModal();
                }}
              >
                Add Rule
              </Button>
              <Menu shadow="md" width={280} position="bottom-end">
                <Menu.Target>
                  <Button
                    variant="filled"
                    px="xs"
                    aria-label="Template rules"
                  >
                    <IconChevronDown size={14} />
                  </Button>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Label>Apply Template</Menu.Label>
                  {ipFilterTemplates.map(template => (
                    <Menu.Item
                      key={template.id}
                      leftSection={<IconTemplate style={{ width: rem(14), height: rem(14) }} />}
                      onClick={() => {
                        setSelectedTemplate(template);
                        openTemplateModal();
                      }}
                    >
                      {template.label}
                    </Menu.Item>
                  ))}
                </Menu.Dropdown>
              </Menu>
            </Button.Group>
          </Group>
        </Group>
      </Card>


      {/* Statistics Cards */}
      <IpFilteringStats stats={stats} isLoading={isLoading} />

      {/* Rules Table Container */}
      <Card shadow="sm" p={0} radius="md" withBorder>
        <Tabs value={activeTab} onChange={setActiveTab}>
          <Tabs.List>
            <Tabs.Tab 
              value="all" 
              leftSection={<IconFilter size={16} />}
              rightSection={
                <Badge size="sm" variant="filled" color="gray">
                  {stats?.totalRules ?? 0}
                </Badge>
              }
            >
              All Rules
            </Tabs.Tab>
            <Tabs.Tab 
              value="allow" 
              leftSection={<IconShieldCheck size={16} />}
              rightSection={
                <Badge size="sm" variant="filled" color="green">
                  {stats?.allowRules ?? 0}
                </Badge>
              }
            >
              Allow List
            </Tabs.Tab>
            <Tabs.Tab 
              value="block" 
              leftSection={<IconShieldX size={16} />}
              rightSection={
                <Badge size="sm" variant="filled" color="red">
                  {stats?.blockRules ?? 0}
                </Badge>
              }
            >
              Block List
            </Tabs.Tab>
          </Tabs.List>

          <div style={{ position: 'relative', minHeight: 400 }}>
            <LoadingOverlay visible={isLoading} overlayProps={{ radius: 'sm', blur: 2 }} />
            
            {/* Bulk Actions Bar */}
            {selectedCount > 0 && (
              <Card.Section p="md" withBorder>
                <Group justify="space-between">
                  <Text size="sm">
                    {selectedCount} rule{selectedCount !== 1 ? 's' : ''} selected
                  </Text>
                  <Group gap="xs">
                    <Button 
                      size="xs" 
                      variant="light"
                      onClick={() => void handleBulkOperation('enable', selectedRules)}
                    >
                      Enable
                    </Button>
                    <Button 
                      size="xs" 
                      variant="light"
                      onClick={() => void handleBulkOperation('disable', selectedRules)}
                    >
                      Disable
                    </Button>
                    <Button 
                      size="xs" 
                      variant="light" 
                      color="red"
                      onClick={() => void handleBulkOperation('delete', selectedRules)}
                    >
                      Delete
                    </Button>
                  </Group>
                </Group>
              </Card.Section>
            )}
            
            {/* IP Rules Table */}
            <Card.Section>
              <IpRulesTable
                data={filteredRules}
                selectedRules={selectedKeys}
                allSelected={isAllSelected}
                someSelected={isIndeterminate}
                onSelectAll={toggleAll}
                onSelectRule={toggleOne}
                onEdit={handleEditRule}
                onDelete={ruleId => void handleDeleteRule(ruleId)}
                onToggle={(ruleId, enabled) => void handleToggleRule(ruleId, enabled, rules)}
              />
            </Card.Section>

            {/* Effective Policy Banner */}
            {!isLoading && (
              <Card.Section p="md">
                <IpFilterPolicyBanner rules={rules} />
              </Card.Section>
            )}
          </div>
        </Tabs>
      </Card>
    </Stack>

    {/* IP Rule Modal */}
    <IpRuleModal
      opened={modalOpened}
      onClose={() => {
        closeModal();
        setSelectedRule(null); // Clear selection when modal is closed
      }}
      onSubmit={handleModalSubmitWrapper}
      rule={selectedRule}
      isLoading={isSubmitting}
    />

    {/* IP Test Modal */}
    <IpTestModal
      opened={testModalOpened}
      onClose={closeTestModal}
    />

    {/* IP Template Modal */}
    <IpTemplateModal
      opened={templateModalOpened}
      onClose={() => {
        closeTemplateModal();
        setSelectedTemplate(null);
      }}
      template={selectedTemplate}
      existingRules={rules}
      onConfirm={(template, rulesToCreate) => {
        void handleApplyTemplate(template, rulesToCreate, setIsSubmitting).then(() => {
          closeTemplateModal();
          setSelectedTemplate(null);
        });
      }}
      isLoading={isSubmitting}
    />
    </>
  );
}
