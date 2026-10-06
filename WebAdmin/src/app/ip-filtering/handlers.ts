import { useSecurityApi } from '@/hooks/useSecurityApi';
import type { CreateIpFilterDto, IpFilterDto } from '@/lib/admin-api';
import { withAdminClient } from '@/lib/client/adminClient';
import { notify } from '@/lib/notifications';
import { downloadBlob, escapeCsvField } from '@/lib/utils/export';
import type { IpFilterTemplate, IpTemplateRule } from '@/components/ip-filtering/ipFilterTemplates';

export function useIpFilteringHandlers(
  fetchIpRules: () => Promise<void>,
  clearSelection: () => void,
  deselectRule: (ruleId: number) => void,
) {
  const { updateIpRule, deleteIpRule, createIpRule } = useSecurityApi();

  const handleBulkOperation = async (operation: string, selectedRules: number[]) => {
    if (selectedRules.length === 0) return;
    
    try {
      const promises = selectedRules.map(async (ruleId) => {
        switch (operation) {
          case 'enable':
            await withAdminClient(client => 
              client.ipFilters.enableFilter(ruleId)
            );
            break;
          case 'disable':
            await withAdminClient(client => 
              client.ipFilters.disableFilter(ruleId)
            );
            break;
          case 'delete':
            await withAdminClient(client => 
              client.ipFilters.deleteById(ruleId)
            );
            break;
          default:
            throw new Error(`Unsupported operation: ${operation}`);
        }
      });

      await Promise.all(promises);

      notify.success(`Successfully ${operation}d ${selectedRules.length} rule(s)`);

      await fetchIpRules();
      clearSelection();
    } catch (error) {
      notify.error(error, `Failed to ${operation} rules`);
    }
  };

  const handleExport = async (format: string) => {
    try {
      // Get all rules using the Admin SDK
      const filters = await withAdminClient(client => 
        client.ipFilters.list()
      );

      let content: string;
      let mimeType: string;

      if (format === 'json') {
        content = JSON.stringify(filters, null, 2);
        mimeType = 'application/json';
      } else if (format === 'csv') {
        // Convert to CSV format
        const headers = ['id', 'name', 'ipAddressOrCidr', 'filterType', 'isEnabled', 'description', 'createdAt'];
        const csvContent = [
          headers.join(','),
          ...filters.map(filter =>
            headers.map(header => escapeCsvField(filter[header as keyof typeof filter])).join(',')
          )
        ].join('\n');
        content = csvContent;
        mimeType = 'text/csv';
      } else {
        throw new Error(`Unsupported export format: ${format}`);
      }

      // Create and download the file
      const blob = new Blob([content], { type: mimeType });
      downloadBlob(blob, `ip-rules.${format}`);

      notify.success(`IP rules exported as ${format.toUpperCase()}`);
    } catch (error) {
      notify.error(error, 'Failed to export IP rules');
    }
  };

  const handleImport = () => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.json,.csv';
    
    input.onchange = async (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (!file) return;

      const format = file.name.endsWith('.csv') ? 'csv' : 'json';

      try {
        const content = await file.text();
        let rulesData: Array<{
          name: string;
          ipAddressOrCidr: string;
          filterType: 'whitelist' | 'blacklist';
          isEnabled?: boolean;
          description?: string;
        }>;

        if (format === 'json') {
          const parsed = JSON.parse(content) as unknown;
          if (Array.isArray(parsed)) {
            rulesData = parsed as typeof rulesData;
          } else if (typeof parsed === 'object' && parsed !== null) {
            rulesData = [parsed as typeof rulesData[0]];
          } else {
            throw new Error('Invalid JSON format');
          }
        } else {
          // Parse CSV
          const lines = content.split('\n').filter(line => line.trim());
          const headers = lines[0].split(',').map(h => h.replace(/"/g, '').trim());
          rulesData = lines.slice(1).map(line => {
            const values = line.split(',').map(v => v.replace(/"/g, '').trim());
            const rule: Record<string, string | boolean> = {};
            headers.forEach((header, index) => {
              if (header === 'isEnabled') {
                rule[header] = values[index] === 'true';
              } else {
                rule[header] = values[index];
              }
            });
            return rule as typeof rulesData[0];
          });
        }

        // Import rules using Admin SDK
        let imported = 0;
        let failed = 0;

        for (const ruleData of rulesData) {
          try {
            if (!ruleData.name || !ruleData.ipAddressOrCidr || !ruleData.filterType) {
              failed++;
              continue;
            }

            await withAdminClient(client =>
              client.ipFilters.create({
                name: ruleData.name,
                ipAddressOrCidr: ruleData.ipAddressOrCidr,
                filterType: ruleData.filterType,
                isEnabled: ruleData.isEnabled ?? true,
                description: ruleData.description,
              })
            );
            imported++;
          } catch {
            failed++;
          }
        }

        notify.success(`Imported ${imported} rule(s) successfully${failed > 0 ? `, ${failed} failed` : ''}`);

        await fetchIpRules();
      } catch (error) {
        notify.error(error, 'Failed to import IP rules');
      }
    };

    input.click();
  };

  const handleDeleteRule = async (ruleId: number) => {
    try {
      await deleteIpRule(ruleId);
      await fetchIpRules();
      deselectRule(ruleId);
    } catch (error) {
      console.error('Failed to delete IP rule:', error);
    }
  };

  const handleToggleRule = async (ruleId: number, enabled: boolean, rules: IpFilterDto[]) => {
    try {
      const rule = rules.find(r => r.id === ruleId);
      if (!rule) return;
      
      await updateIpRule(ruleId, { isEnabled: enabled });
      await fetchIpRules();
    } catch (error) {
      console.error('Failed to toggle IP rule:', error);
    }
  };

  const handleModalSubmit = async (
    values: CreateIpFilterDto,
    selectedRule: IpFilterDto | null,
    setIsSubmitting: React.Dispatch<React.SetStateAction<boolean>>
  ) => {
    setIsSubmitting(true);
    try {
      if (selectedRule) {
        await updateIpRule(selectedRule.id, values);
      } else {
        await createIpRule(values);
      }
      await fetchIpRules();
    } catch (error) {
      // Error is already handled by useSecurityApi which shows notifications
      console.error('Failed to save IP rule:', error);
      throw error; // Re-throw so modal doesn't close
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleApplyTemplate = async (
    template: IpFilterTemplate,
    rulesToCreate: IpTemplateRule[],
    setIsSubmitting: React.Dispatch<React.SetStateAction<boolean>>
  ) => {
    if (rulesToCreate.length === 0) return;

    setIsSubmitting(true);
    let created = 0;
    let failed = 0;

    try {
      for (const rule of rulesToCreate) {
        try {
          await withAdminClient(client =>
            client.ipFilters.create({
              name: rule.name,
              ipAddressOrCidr: rule.ipAddressOrCidr,
              filterType: 'whitelist',
              isEnabled: true,
              description: rule.description,
            })
          );
          created++;
        } catch {
          failed++;
        }
      }

      if (failed > 0) {
        notify.warning(`Created ${created} rule${created !== 1 ? 's' : ''} from "${template.label}", ${failed} failed`);
      } else {
        notify.success(`Created ${created} rule${created !== 1 ? 's' : ''} from "${template.label}"`, 'Template Applied');
      }

      await fetchIpRules();
    } catch (error) {
      notify.error(error, 'Failed to apply template');
    } finally {
      setIsSubmitting(false);
    }
  };

  return {
    handleBulkOperation,
    handleExport,
    handleImport,
    handleDeleteRule,
    handleToggleRule,
    handleModalSubmit,
    handleApplyTemplate,
  };
}
