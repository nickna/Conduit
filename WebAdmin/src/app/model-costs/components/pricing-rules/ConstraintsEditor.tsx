'use client';

import { useCallback } from 'react';
import {
  Stack,
  Group,
  Text,
  NumberInput,
  TagsInput,
  Tooltip,
  ActionIcon,
  Switch,
  Card,
  Collapse,
} from '@mantine/core';
import { IconInfoCircle, IconShieldCheck } from '@tabler/icons-react';
import type { PricingConstraints, PricingType } from '@/lib/admin-api';

interface ConstraintsEditorProps {
  constraints?: PricingConstraints;
  pricingType: PricingType;
  onChange: (constraints: PricingConstraints | undefined) => void;
  readOnly?: boolean;
}

const DEFAULT_RESOLUTIONS = [
  '360p', '480p', '540p', '720p', '768p', '1080p', '1280p', '4k',
];

/**
 * Editor for pricing validation constraints
 */
export function ConstraintsEditor({
  constraints,
  pricingType,
  onChange,
  readOnly = false,
}: ConstraintsEditorProps) {
  const hasConstraints = !!constraints;

  // Determine which constraint fields are relevant based on pricing type
  const showDuration = pricingType === 'per_second';
  const showSteps = pricingType === 'per_step';
  const showResolutions = true; // Always show resolutions

  // Toggle constraints on/off
  const handleToggleConstraints = useCallback((enabled: boolean) => {
    if (enabled) {
      onChange({});
    } else {
      onChange(undefined);
    }
  }, [onChange]);

  // Update specific constraint fields
  const updateConstraint = useCallback(
    <K extends keyof PricingConstraints>(field: K, value: PricingConstraints[K]) => {
      const updated = { ...constraints };
      if (value === undefined || value === null || (typeof value === 'number' && isNaN(value))) {
        delete updated[field];
      } else {
        updated[field] = value;
      }
      // If all fields are empty, keep the object empty but defined
      onChange(updated);
    },
    [constraints, onChange]
  );

  return (
    <Stack gap="sm">
      <Group gap="xs">
        <Switch
          checked={hasConstraints}
          onChange={(e) => handleToggleConstraints(e.currentTarget.checked)}
          disabled={readOnly}
          size="sm"
        />
        <Group gap={4}>
          <IconShieldCheck size={16} />
          <Text size="sm" fw={500}>Validation Constraints</Text>
          <Tooltip
            label="Optional constraints to validate input parameters before pricing calculation"
            withArrow
            multiline
            w={300}
          >
            <ActionIcon variant="subtle" size="xs">
              <IconInfoCircle size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </Group>

      <Collapse expanded={hasConstraints}>
        <Card withBorder p="md" bg="gray.0">
          <Stack gap="md">
            {/* Duration Constraints - shown for per_second pricing */}
            {showDuration && (
              <Group grow align="flex-start">
                <NumberInput
                  label={
                    <Group gap={4}>
                      <Text size="sm" fw={500}>Min Duration</Text>
                      <Tooltip label="Minimum allowed video duration in seconds" withArrow>
                        <ActionIcon variant="subtle" size="xs">
                          <IconInfoCircle size={14} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  }
                  description="Seconds (optional)"
                  value={constraints?.minDuration ?? ''}
                  onChange={(value) =>
                    updateConstraint('minDuration', typeof value === 'number' ? value : undefined)
                  }
                  min={0}
                  step={0.1}
                  decimalScale={2}
                  placeholder="No minimum"
                  disabled={readOnly}
                />

                <NumberInput
                  label={
                    <Group gap={4}>
                      <Text size="sm" fw={500}>Max Duration</Text>
                      <Tooltip label="Maximum allowed video duration in seconds" withArrow>
                        <ActionIcon variant="subtle" size="xs">
                          <IconInfoCircle size={14} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  }
                  description="Seconds (optional)"
                  value={constraints?.maxDuration ?? ''}
                  onChange={(value) =>
                    updateConstraint('maxDuration', typeof value === 'number' ? value : undefined)
                  }
                  min={0}
                  step={0.1}
                  decimalScale={2}
                  placeholder="No maximum"
                  disabled={readOnly}
                />
              </Group>
            )}

            {/* Steps Constraints - shown for per_step pricing */}
            {showSteps && (
              <Group grow align="flex-start">
                <NumberInput
                  label={
                    <Group gap={4}>
                      <Text size="sm" fw={500}>Min Steps</Text>
                      <Tooltip label="Minimum allowed inference steps" withArrow>
                        <ActionIcon variant="subtle" size="xs">
                          <IconInfoCircle size={14} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  }
                  description="Steps (optional)"
                  value={constraints?.minSteps ?? ''}
                  onChange={(value) =>
                    updateConstraint('minSteps', typeof value === 'number' ? Math.floor(value) : undefined)
                  }
                  min={1}
                  step={1}
                  placeholder="No minimum"
                  disabled={readOnly}
                />

                <NumberInput
                  label={
                    <Group gap={4}>
                      <Text size="sm" fw={500}>Max Steps</Text>
                      <Tooltip label="Maximum allowed inference steps" withArrow>
                        <ActionIcon variant="subtle" size="xs">
                          <IconInfoCircle size={14} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  }
                  description="Steps (optional)"
                  value={constraints?.maxSteps ?? ''}
                  onChange={(value) =>
                    updateConstraint('maxSteps', typeof value === 'number' ? Math.floor(value) : undefined)
                  }
                  min={1}
                  step={1}
                  placeholder="No maximum"
                  disabled={readOnly}
                />
              </Group>
            )}

            {/* Resolution Constraints - always shown */}
            {showResolutions && (
              <TagsInput
                label={
                  <Group gap={4}>
                    <Text size="sm" fw={500}>Allowed Resolutions</Text>
                    <Tooltip
                      label="List of valid resolution values. Leave empty to allow any resolution."
                      withArrow
                      multiline
                      w={250}
                    >
                      <ActionIcon variant="subtle" size="xs">
                        <IconInfoCircle size={14} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                }
                description="Type or select values (optional)"
                data={DEFAULT_RESOLUTIONS}
                value={constraints?.allowedResolutions ?? []}
                onChange={(values) =>
                  updateConstraint('allowedResolutions', values.length > 0 ? values : undefined)
                }
                placeholder="Any resolution allowed"
                disabled={readOnly}
              />
            )}

            {/* Show hint when no relevant constraints */}
            {!showDuration && !showSteps && (
              <Text size="sm" c="dimmed">
                Duration and step constraints are available for per_second and per_step pricing types respectively.
              </Text>
            )}
          </Stack>
        </Card>
      </Collapse>
    </Stack>
  );
}
