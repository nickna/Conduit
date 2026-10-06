'use client';

import { Alert, Text, Stack } from '@mantine/core';
import { IconInfoCircle, IconShieldCheck, IconShieldOff } from '@tabler/icons-react';
import type { IpFilterDto } from '@/lib/admin-api';

interface IpFilterPolicyBannerProps {
  rules: IpFilterDto[];
}

type PolicyState = 'no-rules' | 'block-only' | 'allow-only' | 'allow-and-block';

function getPolicyState(rules: IpFilterDto[]): PolicyState {
  const enabledRules = rules.filter(r => r.isEnabled !== false);
  const hasAllow = enabledRules.some(r => r.filterType === 'whitelist');
  const hasBlock = enabledRules.some(r => r.filterType === 'blacklist');

  if (!hasAllow && !hasBlock) return 'no-rules';
  if (!hasAllow && hasBlock) return 'block-only';
  if (hasAllow && !hasBlock) return 'allow-only';
  return 'allow-and-block';
}

export function IpFilterPolicyBanner({ rules }: IpFilterPolicyBannerProps) {
  const state = getPolicyState(rules);

  return (
    <Stack gap="xs">
      <PolicyAlert state={state} />
      <PrivateIpNote />
    </Stack>
  );
}

function PolicyAlert({ state }: { state: PolicyState }) {
  switch (state) {
    case 'no-rules':
      return (
        <Alert
          variant="light"
          color="gray"
          icon={<IconShieldOff size={18} />}
        >
          <Text size="sm">
            <Text span fw={600}>No filtering active</Text> — all traffic is currently allowed.
          </Text>
        </Alert>
      );

    case 'block-only':
      return (
        <Alert
          variant="light"
          color="blue"
          icon={<IconInfoCircle size={18} />}
        >
          <Text size="sm">
            <Text span fw={600}>Default: Allow</Text> — all traffic is allowed unless it matches a block rule above.
          </Text>
        </Alert>
      );

    case 'allow-only':
      return (
        <Alert
          variant="light"
          color="yellow"
          icon={<IconShieldCheck size={18} />}
        >
          <Text size="sm">
            <Text span fw={600}>Default: Deny</Text> — only traffic matching an allow rule is permitted. All other IPs are blocked.
          </Text>
        </Alert>
      );

    case 'allow-and-block':
      return (
        <Alert
          variant="light"
          color="yellow"
          icon={<IconShieldCheck size={18} />}
        >
          <Text size="sm">
            <Text span fw={600}>Default: Deny</Text> — traffic must match an allow rule and not match any block rule. Block rules take priority.
          </Text>
        </Alert>
      );
  }
}

function PrivateIpNote() {
  return (
    <Text size="xs" c="dimmed" pl="xs">
      Private network IPs (10.x, 172.16-31.x, 192.168.x, 127.x) are always allowed via server configuration, regardless of rules.
    </Text>
  );
}
