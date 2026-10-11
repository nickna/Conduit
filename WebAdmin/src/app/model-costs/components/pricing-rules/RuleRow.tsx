'use client';

import {
  Card,
  Group,
  Stack,
  NumberInput,
  TextInput,
  ActionIcon,
  Badge,
  Text,
  Tooltip,
  Collapse,
  Button,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import {
  IconTrash,
  IconCurrencyDollar,
  IconChevronDown,
  IconChevronUp,
  IconPlus,
} from '@tabler/icons-react';
import type { PricingRule, PricingValidationError } from '@/lib/admin-api';
import { ConditionBuilder } from './ConditionBuilder';

export interface ParameterOption {
  key: string;
  label: string;
  type: 'string' | 'number' | 'boolean' | 'enum';
  options?: string[];
}

interface RuleRowProps {
  rule: PricingRule;
  index: number;
  parameterOptions: ParameterOption[];
  onUpdate: (updates: Partial<PricingRule>) => void;
  onRemove: () => void;
  onAddCondition: (key: string, value: string | number | boolean) => void;
  onRemoveCondition: (key: string) => void;
  readOnly?: boolean;
  validationErrors: PricingValidationError[];
}

/**
 * A single pricing rule with conditions and rate
 */
export function RuleRow({
  rule,
  index,
  parameterOptions,
  onUpdate,
  onRemove,
  onAddCondition,
  onRemoveCondition,
  readOnly = false,
  validationErrors,
}: RuleRowProps) {
  const [expanded, { toggle }] = useDisclosure(true);

  const conditionCount = Object.keys(rule.conditions).length;
  const hasErrors = validationErrors.length > 0;

  // Find parameters not yet used in conditions
  const availableParameters = parameterOptions.filter(
    p => !(p.key in rule.conditions)
  );

  const getDefaultValueForParam = (param: ParameterOption): string | number | boolean => {
    if (param.type === 'boolean') return false;
    if (param.type === 'number') return 0;
    return param.options?.[0] ?? '';
  };

  const handleAddCondition = () => {
    if (availableParameters.length > 0) {
      const param = availableParameters[0];
      const defaultValue = getDefaultValueForParam(param);
      onAddCondition(param.key, defaultValue);
    }
  };

  return (
    <Card withBorder p="sm" style={{ borderColor: hasErrors ? 'var(--mantine-color-red-5)' : undefined }}>
      <Stack gap="sm">
        {/* Rule Header */}
        <Group justify="space-between">
          <Group gap="sm">
            <Badge variant="light" color="blue">
              Rule {index + 1}
            </Badge>
            <Badge variant="outline" color="gray">
              Priority: {rule.priority ?? 0}
            </Badge>
            <Badge variant="outline" color={conditionCount > 0 ? 'green' : 'yellow'}>
              {conditionCount} condition{conditionCount !== 1 ? 's' : ''}
            </Badge>
          </Group>

          <Group gap="xs">
            <ActionIcon
              variant="subtle"
              onClick={toggle}
              title={expanded ? 'Collapse' : 'Expand'}
            >
              {expanded ? <IconChevronUp size={16} /> : <IconChevronDown size={16} />}
            </ActionIcon>
            {!readOnly && (
              <Tooltip label="Delete rule">
                <ActionIcon
                  variant="subtle"
                  color="red"
                  onClick={onRemove}
                >
                  <IconTrash size={16} />
                </ActionIcon>
              </Tooltip>
            )}
          </Group>
        </Group>

        <Collapse expanded={expanded}>
          <Stack gap="sm">
            {/* Rule Configuration */}
            <Group grow align="flex-start">
              <NumberInput
                label="Rate"
                description="Cost rate (USD)"
                value={rule.rate}
                onChange={(value) => onUpdate({ rate: typeof value === 'number' ? value : 0 })}
                min={0}
                step={0.001}
                decimalScale={6}
                leftSection={<IconCurrencyDollar size={16} />}
                disabled={readOnly}
                error={validationErrors.find(e => e.field === 'rate')?.message}
              />

              <NumberInput
                label="Priority"
                description="Higher = evaluated first"
                value={rule.priority ?? 0}
                onChange={(value) => onUpdate({ priority: typeof value === 'number' ? value : 0 })}
                min={0}
                disabled={readOnly}
              />

              <TextInput
                label="Description"
                description="Human-readable label"
                value={rule.description ?? ''}
                onChange={(e) => onUpdate({ description: e.target.value })}
                placeholder="e.g., 1080p HD pricing"
                disabled={readOnly}
              />
            </Group>

            {/* Conditions */}
            <Stack gap="xs">
              <Group justify="space-between">
                <Text size="sm" fw={500}>Conditions (all must match)</Text>
                {!readOnly && availableParameters.length > 0 && (
                  <Button
                    variant="subtle"
                    size="xs"
                    leftSection={<IconPlus size={14} />}
                    onClick={handleAddCondition}
                  >
                    Add Condition
                  </Button>
                )}
              </Group>

              {conditionCount === 0 ? (
                <Text size="xs" c="dimmed" fs="italic">
                  No conditions - this rule will always match at its priority level
                </Text>
              ) : (
                <Stack gap="xs">
                  {Object.entries(rule.conditions).map(([key, value]) => {
                    const paramDef = parameterOptions.find(p => p.key === key);
                    return (
                      <ConditionBuilder
                        key={key}
                        paramKey={key}
                        value={value}
                        parameterDef={paramDef}
                        onChange={(newValue) => onAddCondition(key, newValue)}
                        onRemove={() => onRemoveCondition(key)}
                        readOnly={readOnly}
                      />
                    );
                  })}
                </Stack>
              )}
            </Stack>
          </Stack>
        </Collapse>
      </Stack>
    </Card>
  );
}
