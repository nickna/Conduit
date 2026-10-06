'use client';

import { useState } from 'react';
import {
  Modal,
  Stack,
  Alert,
  FileInput,
  Table,
  ScrollArea,
  Button,
  Group,
  Text,
  Badge,
  LoadingOverlay,
  Card,
} from '@mantine/core';
import { IconFileTypeCsv, IconAlertCircle, IconCheck } from '@tabler/icons-react';
import { notify } from '@/lib/notifications';
import { useImportModelCostsWithAliases } from '../hooks/useModelCostsApi';
import { parseCSVContent, ParsedModelCost } from '../utils/csvHelpers';

interface ImportModelCostsModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSuccess?: () => void;
}


export function ImportModelCostsModal({ isOpen, onClose, onSuccess }: ImportModelCostsModalProps) {
  const importMutation = useImportModelCostsWithAliases();
  const [file, setFile] = useState<File | null>(null);
  const [parsedData, setParsedData] = useState<ParsedModelCost[]>([]);
  const [isParsing, setIsParsing] = useState(false);
  const [parseError, setParseError] = useState<string | null>(null);

  const parseCSV = async (csvFile: File) => {
    setIsParsing(true);
    setParseError(null);
    
    try {
      const text = await csvFile.text();
      const parsed = parseCSVContent(text);
      setParsedData(parsed);
    } catch (error) {
      setParseError(error instanceof Error ? error.message : 'Failed to parse CSV file');
      setParsedData([]);
    } finally {
      setIsParsing(false);
    }
  };


  const handleFileChange = (newFile: File | null) => {
    setFile(newFile);
    setParsedData([]);
    setParseError(null);
    
    if (newFile) {
      void parseCSV(newFile);
    }
  };

  const handleImport = async () => {
    const validData = parsedData.filter(d => d.isValid);
    if (validData.length === 0) {
      notify.error(new Error('No valid data to import'));
      return;
    }

    try {
      const costsWithAliases = validData.map(cost => ({
        costName: cost.costName,
        modelAliases: cost.modelAliases,
        modelType: cost.modelType,
        inputCostPerMillionTokens: cost.inputCostPerMillion, // Already per million
        outputCostPerMillionTokens: cost.outputCostPerMillion,
        cachedInputCostPerMillionTokens: cost.cachedInputCostPerMillion,
        cachedInputWriteCostPerMillionTokens: cost.cachedInputWriteCostPerMillion,
        embeddingCostPerMillionTokens: cost.embeddingCostPerMillion,
        costPerSearchUnit: cost.searchUnitCostPer1K,
        supportsBatchProcessing: cost.supportsBatchProcessing,
        batchProcessingMultiplier: cost.batchProcessingMultiplier,
        priority: cost.priority,
        description: cost.description,
        isActive: cost.active,
      }));

      const result = await importMutation.mutateAsync(costsWithAliases);
      
      if (result.success > 0) {
        onSuccess?.();
        onClose();
        setFile(null);
        setParsedData([]);
      }
    } catch {
      // Error handling is done in the hook.
    }
  };

  const validCount = parsedData.filter(d => d.isValid).length;
  const invalidCount = parsedData.filter(d => !d.isValid && !d.isSkipped).length;
  const skippedCount = parsedData.filter(d => d.isSkipped).length;

  return (
    <Modal
      opened={isOpen}
      onClose={onClose}
      title="Import Model Pricing from CSV"
      size="xl"
    >
      <Stack gap="md">
        <Alert icon={<IconAlertCircle size={16} />} color="blue">
          Upload a CSV file with model pricing data. The file should include columns for:
          Cost Name, Associated Model Aliases (comma-separated), Model Type, and relevant cost fields.
          Download the current data as CSV to see the expected format.
          <Text size="xs" mt="xs" c="dimmed">
            Note: Model aliases will be matched to existing model mappings during import.
          </Text>
        </Alert>

        <FileInput
          label="Select CSV File"
          placeholder="Click to browse files"
          leftSection={<IconFileTypeCsv size={16} />}
          accept=".csv"
          value={file}
          onChange={handleFileChange}
        />

        {parseError && (
          <Alert color="red" icon={<IconAlertCircle size={16} />}>
            {parseError}
          </Alert>
        )}

        {parsedData.length > 0 && (
          <>
            <Card withBorder>
              <Group justify="space-between">
                <Text fw={600}>File Processed Successfully</Text>
                <Group gap="xs">
                  {validCount > 0 && (
                    <Badge color="green" variant="light">
                      {validCount} valid
                    </Badge>
                  )}
                  {invalidCount > 0 && (
                    <Badge color="red" variant="light">
                      {invalidCount} invalid
                    </Badge>
                  )}
                  {skippedCount > 0 && (
                    <Badge color="orange" variant="light">
                      {skippedCount} skipped
                    </Badge>
                  )}
                </Group>
              </Group>
            </Card>

            <ScrollArea h={300}>
              <LoadingOverlay visible={isParsing} />
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th w={40}></Table.Th>
                    <Table.Th>Row #</Table.Th>
                    <Table.Th>Cost Name</Table.Th>
                    <Table.Th>Model Aliases</Table.Th>
                    <Table.Th>Type</Table.Th>
                    <Table.Th>Input/Output Cost</Table.Th>
                    <Table.Th>Priority</Table.Th>
                    <Table.Th>Status</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {parsedData.map((cost) => (
                    <Table.Tr 
                      key={`${cost.rowNumber}-${cost.costName}`} 
                      style={{ 
                        backgroundColor: (() => {
                          if (cost.isValid) return undefined;
                          if (cost.isSkipped) return 'var(--mantine-color-orange-0)';
                          return 'var(--mantine-color-red-0)';
                        })()
                      }}>
                      <Table.Td>
                        {cost.isValid ? (
                          <IconCheck size={16} color="green" />
                        ) : (
                          <IconAlertCircle size={16} color={cost.isSkipped ? "orange" : "red"} />
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm">{cost.rowNumber}</Text>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm">{cost.costName || '(empty)'}</Text>
                        {!cost.isValid && (
                          <Text size="xs" c={cost.isSkipped ? "orange" : "red"}>
                            {cost.isSkipped ? cost.skipReason : cost.errors.join(', ')}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Text size="xs">{cost.modelAliases.join(', ') || '(none)'}</Text>
                      </Table.Td>
                      <Table.Td>
                        <Badge variant="outline" size="sm">{cost.modelType}</Badge>
                      </Table.Td>
                      <Table.Td>
                        <Text size="xs">
                          ${cost.inputCostPerMillion}/M • ${cost.outputCostPerMillion}/M
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm">{cost.priority}</Text>
                      </Table.Td>
                      <Table.Td>
                        <Badge 
                          size="sm" 
                          variant="light"
                          color={cost.active ? 'green' : 'gray'}
                        >
                          {cost.active ? 'Active' : 'Inactive'}
                        </Badge>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea>

            {validCount > 0 ? (
              <Alert icon={<IconCheck size={16} />} color="green">
                Ready to import {validCount} valid pricing configuration{validCount !== 1 ? 's' : ''}.
                This will create new entries and link them to the specified model aliases.
              </Alert>
            ) : (
              <Alert icon={<IconAlertCircle size={16} />} color="orange">
                Import is disabled because no valid entries were found in the CSV file.
                {(() => {
                  if (invalidCount > 0 && skippedCount > 0) {
                    return ` Found ${invalidCount} validation errors and ${skippedCount} skipped rows. Please fix the issues shown above and try again.`;
                  }
                  if (invalidCount > 0) {
                    return ` All ${invalidCount} entries have validation errors. Please fix the errors shown above and try again.`;
                  }
                  if (skippedCount > 0) {
                    return ` All ${skippedCount} rows were skipped due to formatting issues. Please check your CSV format.`;
                  }
                  return ' Please check that your CSV file has the correct format with required columns: Cost Name, Associated Model Aliases, Model Type, and cost fields.';
                })()}
              </Alert>
            )}
          </>
        )}

        <Group justify="flex-end" mt="md">
          <Button variant="subtle" onClick={onClose}>
            Cancel
          </Button>
          <Button 
            onClick={() => void handleImport()}
            loading={importMutation.isPending}
            disabled={validCount === 0}
          >
            Import {validCount > 0 ? `${validCount} Items` : ''}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
