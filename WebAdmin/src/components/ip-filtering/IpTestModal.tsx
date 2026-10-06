'use client';

import { useState } from 'react';
import {
  Modal,
  TextInput,
  Button,
  Group,
  Stack,
  Alert,
  Text,
  Badge,
  Card,
  Divider,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconAlertCircle, IconCheck, IconX } from '@tabler/icons-react';
import { withAdminClient } from '@/lib/client/adminClient';
import { getIpValidationError } from '@/lib/utils/ip-validation';

interface IpTestModalProps {
  opened: boolean;
  onClose: () => void;
}

interface IpFilterTestResult {
  allowed: boolean;
  reason?: string;
}

// Accepts a plain IPv4 or IPv6 address, matching the backend's rules
const validateIpAddress = (value: string) => getIpValidationError(value);

export function IpTestModal({ opened, onClose }: IpTestModalProps) {
  const [isLoading, setIsLoading] = useState(false);
  const [testResult, setTestResult] = useState<IpFilterTestResult | null>(null);
  
  const form = useForm({
    initialValues: {
      ipAddress: '',
    },
    validate: {
      ipAddress: validateIpAddress,
    },
  });

  const handleSubmit = async (values: typeof form.values) => {
    setIsLoading(true);
    setTestResult(null);
    
    try {
      const result = await withAdminClient(client =>
        client.ipFilters.checkIp(values.ipAddress)
      );

      const testResult: IpFilterTestResult = {
        allowed: result.isAllowed,
        reason: result.deniedReason ?? (result.isAllowed ? 'IP address is allowed' : 'IP address is blocked'),
      };

      setTestResult(testResult);
    } catch (error) {
      // If the check fails, show an error message
      const message = error instanceof Error ? error.message : 'Failed to test IP address';
      setTestResult({
        allowed: false,
        reason: `Error: ${message}`,
      });
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = () => {
    form.reset();
    setTestResult(null);
    onClose();
  };

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title="Test IP Address"
      size="md"
    >
      <form onSubmit={form.onSubmit(handleSubmit)}>
        <Stack gap="md">
          <TextInput
            label="IP Address to Test"
            placeholder="e.g., 192.168.1.100"
            required
            {...form.getInputProps('ipAddress')}
          />

          <Button type="submit" loading={isLoading} fullWidth>
            Test IP
          </Button>

          {testResult && (
            <>
              <Divider />
              
              <Card withBorder p="md" radius="md">
                <Group justify="space-between" mb="md">
                  <Text fw={500}>Test Result</Text>
                  <Badge
                    color={testResult.allowed ? 'green' : 'red'}
                    variant="filled"
                    size="lg"
                    leftSection={
                      testResult.allowed ? <IconCheck size={16} /> : <IconX size={16} />
                    }
                  >
                    {testResult.allowed ? 'ALLOWED' : 'BLOCKED'}
                  </Badge>
                </Group>

                <Text size="sm" c="dimmed">
                  {testResult.reason ?? 'No specific rule matched this IP address.'}
                </Text>
              </Card>
              
              <Alert
                icon={<IconAlertCircle size={16} />}
                color="blue"
                variant="light"
              >
                <Text size="sm">
                  This test shows what would happen if a request came from this IP address. 
                  The actual behavior depends on your IP filtering settings and rule configuration.
                </Text>
              </Alert>
            </>
          )}
        </Stack>
      </form>
    </Modal>
  );
}
