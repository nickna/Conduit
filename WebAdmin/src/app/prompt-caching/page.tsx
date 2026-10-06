'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Container, Group, NumberInput, Paper,
  Select, SimpleGrid, Skeleton, Stack, Switch, Text, TextInput, Title,
} from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import { notify } from '@/lib/notifications';
import { withAdminClient } from '@/lib/client/adminClient';
import type {
  CacheInjectionPointDto,
  PromptCachingAnalyticsDto,
  PromptCachingCapabilityDto,
  PromptCachingConfigDto,
  PromptCachingRuleDto,
  PromptCachingStrategy,
} from '@/lib/admin-api';

const emptyConfig: PromptCachingConfigDto = { schemaVersion: 3, enabled: false, rules: [] };

export default function PromptCachingPage() {
  const [config, setConfig] = useState<PromptCachingConfigDto>(emptyConfig);
  const [capabilities, setCapabilities] = useState<PromptCachingCapabilityDto[]>([]);
  const [snapshot, setSnapshot] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [configurationError, setConfigurationError] = useState<string | null>(null);
  const [analytics, setAnalytics] = useState<PromptCachingAnalyticsDto | null>(null);
  const [aliasFilter, setAliasFilter] = useState('');
  const [providerFilter, setProviderFilter] = useState('');

  const managedCapabilities = useMemo(() => capabilities.filter(c => !c.providerManaged), [capabilities]);
  const providerManaged = useMemo(() => capabilities.filter(c => c.providerManaged), [capabilities]);
  const isDirty = JSON.stringify(config) !== snapshot;

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [rawConfig, rawCapabilities] = await Promise.all([
        withAdminClient(client => client.configuration.getPromptCachingConfig()),
        withAdminClient(client => client.configuration.getPromptCachingCapabilities()),
      ]);
      setConfig(rawConfig);
      setSnapshot(JSON.stringify(rawConfig));
      setCapabilities(rawCapabilities);
      const analyticsResult = await withAdminClient(client => client.configuration.getPromptCachingAnalytics({ alias: aliasFilter || undefined, provider: providerFilter || undefined }));
      setAnalytics(analyticsResult);
      setConfigurationError(null);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Stored configuration must be replaced.';
      setConfig(emptyConfig);
      setSnapshot('');
      setConfigurationError(message);
      try {
        const rawCapabilities = await withAdminClient(client => client.configuration.getPromptCachingCapabilities());
        setCapabilities(rawCapabilities);
      } catch { /* retain an empty catalog and surface the configuration error */ }
    } finally {
      setLoading(false);
    }
  }, [aliasFilter, providerFilter]);

  useEffect(() => { void load(); }, [load]);

  const updateRule = (index: number, update: Partial<PromptCachingRuleDto>) => {
    setConfig(current => ({
      ...current,
      rules: current.rules.map((rule, i) => i === index ? { ...rule, ...update } : rule),
    }));
  };

  const addRule = () => {
    const capability = managedCapabilities[0];
    if (!capability) return;
    const strategy = capability.strategies[0];
    setConfig(current => ({
      ...current,
      enabled: true,
      rules: [...current.rules, {
        name: `Caching rule ${current.rules.length + 1}`,
        enabled: true,
        provider: capability.provider,
        modelPattern: capability.modelPattern,
        strategy,
        ttl: capability.ttls[0] ?? null,
        injectionPoints: strategy === 'Explicit' ? [{ role: 'system', index: 0 }] : [],
      }],
    }));
  };

  const save = async () => {
    setSaving(true);
    try {
      const next = await withAdminClient(client => client.configuration.updatePromptCachingConfig(config));
      setConfig(next);
      setSnapshot(JSON.stringify(next));
      setConfigurationError(null);
      notify.success('Provider-aware prompt caching policy saved.', 'Saved');
    } catch (error) {
      notify.error(error instanceof Error ? error.message : 'Failed to save prompt caching policy.');
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <Container size="lg" py="xl"><Skeleton height={280} /></Container>;

  return (
    <Container size="lg" py="xl">
      <Stack gap="lg">
        <div>
          <Group gap="sm"><Title order={2}>Prompt Caching</Title><Badge>Policy v3</Badge></Group>
          <Text c="dimmed" size="sm">
            Admin rules fill missing provider controls. Caller-provided cache keys, modes, lifetimes, and
            content breakpoints always take precedence.
          </Text>
        </div>

        <Paper withBorder p="md">
          <Stack gap="sm">
            <Group grow><TextInput label="Alias filter" value={aliasFilter} onChange={event => setAliasFilter(event.currentTarget.value)} /><TextInput label="Provider filter" value={providerFilter} onChange={event => setProviderFilter(event.currentTarget.value)} /></Group>
            <SimpleGrid cols={{ base: 2, md: 4 }}>
              <div><Text size="xs" c="dimmed">Realized net savings</Text><Text fw={700}>${(analytics?.netSavings ?? 0).toFixed(4)}</Text></div>
              <div><Text size="xs" c="dimmed">Hit rate</Text><Text fw={700}>{analytics && analytics.readEvents + analytics.eligibleMisses > 0 ? `${(100 * analytics.readEvents / (analytics.readEvents + analytics.eligibleMisses)).toFixed(1)}%` : '—'}</Text></div>
              <div><Text size="xs" c="dimmed">Cached tokens</Text><Text fw={700}>{(analytics?.cachedTokens ?? 0).toLocaleString()}</Text></div>
              <div><Text size="xs" c="dimmed">Write premium</Text><Text fw={700}>${(analytics?.writePremium ?? 0).toFixed(4)}</Text></div>
              <div><Text size="xs" c="dimmed">Latency delta</Text><Text fw={700}>{analytics && typeof analytics.hitLatencyMs === 'number' && typeof analytics.missLatencyMs === 'number' ? `${(analytics.missLatencyMs - analytics.hitLatencyMs).toFixed(0)} ms` : '—'}</Text></div>
              <div><Text size="xs" c="dimmed">Affinity reuse</Text><Text fw={700}>{analytics?.affinityReuse ?? 0}</Text></div>
              <div><Text size="xs" c="dimmed">Failovers</Text><Text fw={700}>{analytics?.failovers ?? 0}</Text></div>
              <div><Text size="xs" c="dimmed">Requests</Text><Text fw={700}>{analytics?.requests ?? 0}</Text></div>
            </SimpleGrid>
          </Stack>
        </Paper>

        {configurationError && <Alert color="red" title="Configuration replacement required">{configurationError}</Alert>}

        <Switch
          size="lg"
          label="Enable managed prompt caching rules"
          checked={config.enabled}
          onChange={event => setConfig(current => ({ ...current, enabled: event.currentTarget.checked }))}
        />

        <Group justify="space-between">
          <Text fw={600}>Ordered rules (first match wins)</Text>
          <Button leftSection={<IconPlus size={16} />} onClick={addRule} disabled={!managedCapabilities.length}>Add rule</Button>
        </Group>

        {config.rules.length === 0 && <Alert color="blue">No managed rules configured. Automatic provider caching is unaffected.</Alert>}

        {config.rules.map((rule, index) => {
          const capability = managedCapabilities.find(c => c.modelPattern === rule.modelPattern && c.provider === rule.provider);
          const explicit = rule.strategy === 'Explicit';
          return (
            <Paper key={`${rule.name}-${index}`} withBorder p="md">
              <Stack gap="sm">
                <Group justify="space-between">
                  <Switch checked={rule.enabled} label={`Rule ${index + 1}`} onChange={event => updateRule(index, { enabled: event.currentTarget.checked })} />
                  <ActionIcon color="red" variant="subtle" aria-label="Delete rule" onClick={() => setConfig(current => ({ ...current, rules: current.rules.filter((ruleItem, i) => ruleItem && i !== index) }))}><IconTrash size={16} /></ActionIcon>
                </Group>
                <TextInput label="Name" value={rule.name} onChange={event => updateRule(index, { name: event.currentTarget.value })} />
                <Select
                  label="Supported route"
                  data={managedCapabilities.map(c => ({ value: `${c.provider}|${c.modelPattern}`, label: `${c.provider} · ${c.modelPattern}` }))}
                  value={`${rule.provider}|${rule.modelPattern}`}
                  onChange={value => {
                    const nextCapability = managedCapabilities.find(c => `${c.provider}|${c.modelPattern}` === value);
                    if (!nextCapability) return;
                    const strategy = nextCapability.strategies[0];
                    updateRule(index, {
                      modelPattern: nextCapability.modelPattern,
                      provider: nextCapability.provider,
                      strategy,
                      ttl: nextCapability.ttls[0] ?? null,
                      injectionPoints: strategy === 'Explicit' ? [{ role: 'system', index: 0 }] : [],
                    });
                  }}
                />
                <Group grow align="end">
                  <Select label="Strategy" data={(capability?.strategies ?? []).map(s => ({ value: s, label: s === 'Automatic' ? 'Automatic conversation caching' : 'Explicit breakpoints' }))} value={rule.strategy} onChange={value => {
                    if (!value) return;
                    let injectionPoints: CacheInjectionPointDto[] = [];
                    if (value === 'Explicit') {
                      injectionPoints = rule.injectionPoints.length > 0
                        ? rule.injectionPoints
                        : [{ role: 'system', index: 0 }];
                    }
                    updateRule(index, { strategy: value as PromptCachingStrategy, injectionPoints });
                  }} />
                  <Select label="Lifetime" data={(capability?.ttls ?? []).map(ttl => ({ value: ttl, label: ttl }))} value={rule.ttl ?? null} onChange={value => updateRule(index, { ttl: value })} disabled={!capability?.ttls.length} />
                </Group>
                {capability?.minimumTokens && <Text size="xs" c="dimmed">Prefixes shorter than approximately {capability.minimumTokens.toLocaleString()} tokens may not be cached.</Text>}
                {explicit && (
                  <Stack gap="xs">
                    <Group justify="space-between"><Text size="sm" fw={600}>Breakpoints ({rule.injectionPoints.length}/{capability?.maxBreakpoints ?? 4})</Text><Button size="xs" variant="light" disabled={rule.injectionPoints.length >= (capability?.maxBreakpoints ?? 4)} onClick={() => updateRule(index, { injectionPoints: [...rule.injectionPoints, { role: 'user', index: -1 }] })}>Add breakpoint</Button></Group>
                    {rule.injectionPoints.map((point, pointIndex) => <Group key={pointIndex} grow>
                      <Select data={['system', 'developer', 'user', 'assistant']} value={point.role ?? null} onChange={value => updateRule(index, { injectionPoints: rule.injectionPoints.map((p, i) => i === pointIndex ? { ...p, role: value as CacheInjectionPointDto['role'] } : p) })} />
                      <NumberInput value={point.index ?? ''} placeholder="All matching" allowDecimal={false} min={-100} max={100} onChange={value => updateRule(index, { injectionPoints: rule.injectionPoints.map((p, i) => i === pointIndex ? { ...p, index: value === '' ? null : Number(value) } : p) })} />
                      <ActionIcon color="red" variant="subtle" onClick={() => updateRule(index, { injectionPoints: rule.injectionPoints.filter((pointItem, i) => pointItem && i !== pointIndex) })}><IconTrash size={16} /></ActionIcon>
                    </Group>)}
                  </Stack>
                )}
              </Stack>
            </Paper>
          );
        })}

        {providerManaged.length > 0 && <Alert title="Provider-managed automatic caching" color="gray">{providerManaged.map(c => `${c.provider} ${c.modelPattern}`).join(' · ')}</Alert>}
        <Group justify="flex-end"><Button variant="default" onClick={() => void load()}>Reset</Button><Button loading={saving} disabled={!isDirty} onClick={() => void save()}>Save policy</Button></Group>
      </Stack>
    </Container>
  );
}
