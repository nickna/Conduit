'use client';

import { useState, useEffect, useRef } from 'react';
import {
  Card,
  Stack,
  Title,
  Text,
  Group,
  Badge,
  ThemeIcon,
  Collapse,
  Button,
  Loader,
  Alert
} from '@mantine/core';
import {
  IconCheck,
  IconX,
  IconAlertTriangle,
  IconChevronDown,
  IconChevronRight
} from '@tabler/icons-react';
import type { CheckResult } from './config-checklist/types';
import { fetchConfigData } from './config-checklist/data-fetcher';
import {
  checkEnabledProviders,
  checkEnabledProviderKeys,
  checkModelMappings,
  checkModelCosts,
  checkModelCategoriesMapping,
  checkCheapModelCosts,
  checkS3Configuration
} from './config-checklist/check-functions';
import { CheckItem } from './config-checklist/CheckItem';

// Main component - simplified with no complex dependencies
export function SystemConfigChecklist() {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [checks, setChecks] = useState<CheckResult[]>([]);
  const [expanded, setExpanded] = useState(false);
  
  // Use ref to track if component is mounted - prevents state updates after unmount
  const isMountedRef = useRef(true);

  // Single function to perform all checks
  async function performChecks() {
    try {
      setLoading(true);
      setError(null);
      
      // Fetch data
      const configData = await fetchConfigData();
      
      // Only update state if still mounted
      if (!isMountedRef.current) return;
      
      // Run all checks
      const checkResults: CheckResult[] = [
        checkEnabledProviders(configData),
        checkEnabledProviderKeys(configData),
        checkModelMappings(configData),
        checkModelCosts(configData),
        checkModelCategoriesMapping(configData),
        checkCheapModelCosts(configData),
        await checkS3Configuration()
      ];
      
      // Only update state if still mounted
      if (!isMountedRef.current) return;
      
      setChecks(checkResults);
      
      // Auto-expand if there are any errors or warnings
      const hasIssues = checkResults.some(check => 
        check.status === 'error' || check.status === 'warning'
      );
      setExpanded(hasIssues);
      
    } catch (err) {
      if (!isMountedRef.current) return;
      console.error('Failed to perform configuration checks:', err);
      setError(err instanceof Error ? err.message : 'Failed to load configuration data');
    } finally {
      if (isMountedRef.current) {
        setLoading(false);
      }
    }
  }

  // Simple useEffect - only runs once on mount
  useEffect(() => {
    isMountedRef.current = true;
    
    // Perform initial checks
    void performChecks();
    
    // Cleanup function
    return () => {
      isMountedRef.current = false;
    };
  }, []); // Empty dependency array - runs once on mount

  // Calculate overall status
  const hasErrors = checks.some(check => check.status === 'error');
  const hasWarnings = checks.some(check => check.status === 'warning');
  
  const overallStatus: CheckResult['status'] = (() => {
    if (hasErrors) return 'error';
    if (hasWarnings) return 'warning';
    return 'success';
  })();
  
  const overallMessage = (() => {
    if (hasErrors) return 'System has configuration errors that require attention';
    if (hasWarnings) return 'System is functional but has some recommendations';
    return 'System is properly configured';
  })();

  // Helper functions for rendering
  const getStatusIcon = (status: CheckResult['status']) => {
    switch (status) {
      case 'success':
        return <IconCheck size={16} color="var(--mantine-color-green-6)" />;
      case 'error':
        return <IconX size={16} color="var(--mantine-color-red-6)" />;
      case 'warning':
        return <IconAlertTriangle size={16} color="var(--mantine-color-yellow-6)" />;
    }
  };

  const getStatusBadge = (status: CheckResult['status']) => {
    const config = {
      success: { color: 'green', label: 'OK' },
      error: { color: 'red', label: 'Error' },
      warning: { color: 'yellow', label: 'Warning' }
    };
    const { color, label } = config[status];
    return <Badge color={color} size="sm">{label}</Badge>;
  };

  // Loading state
  if (loading) {
    return (
      <Card withBorder>
        <Group>
          <Loader size="sm" />
          <Text>Checking system configuration...</Text>
        </Group>
      </Card>
    );
  }

  // Error state
  if (error) {
    return (
      <Alert color="red" title="Configuration Check Failed">
        <Text size="sm">{error}</Text>
        <Button 
          size="xs" 
          mt="sm" 
          onClick={() => {
            void performChecks();
          }}
        >
          Retry
        </Button>
      </Alert>
    );
  }

  // Main render
  return (
    <Card withBorder>
      <Stack gap="md">
        <Group justify="space-between">
          <Group>
            <ThemeIcon 
              size="lg" 
              variant="light" 
              color={(() => {
                if (overallStatus === 'success') return 'green';
                if (overallStatus === 'error') return 'red';
                return 'yellow';
              })()}
            >
              {getStatusIcon(overallStatus)}
            </ThemeIcon>
            <div>
              <Title order={4}>System Configuration</Title>
              <Text size="sm" c="dimmed">{overallMessage}</Text>
            </div>
          </Group>
          <Group gap="xs">
            {getStatusBadge(overallStatus)}
            <Button 
              variant="subtle" 
              size="xs" 
              onClick={() => setExpanded(!expanded)}
              rightSection={expanded ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
            >
              Details
            </Button>
          </Group>
        </Group>

        <Collapse expanded={expanded}>
          <Stack gap="sm">
            {checks.map((check) => (
              <CheckItem 
                key={check.id} 
                check={check} 
                getStatusBadge={getStatusBadge} 
              />
            ))}
            
            <Group justify="center" mt="md">
              <Button 
                size="xs" 
                variant="light" 
                onClick={() => {
                  void performChecks();
                }}
              >
                Refresh Checks
              </Button>
            </Group>
          </Stack>
        </Collapse>
      </Stack>
    </Card>
  );
}