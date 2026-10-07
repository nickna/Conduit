import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';

const output = resolve(process.env.CI_SMOKE_OUTPUT ?? 'artifacts/container-smoke');
mkdirSync(output, { recursive: true });
const run = `conduit-ci-${process.pid}-${Date.now()}`;
const containers = [];
const report = { images: {}, checks: [], startedAt: new Date().toISOString() };
const images = { admin: process.env.CI_ADMIN_IMAGE ?? 'conduit-admin:ci',
  http: process.env.CI_GATEWAY_IMAGE ?? 'conduit-http:ci' };
if (process.env.CI_BROWSER_MODULE) images.webadmin = process.env.CI_WEBADMIN_IMAGE ?? 'conduit-webadmin:ci';
const master = 'conduit-ci-isolated-master-key-32-bytes';
function docker(args) { return execFileSync('docker', args, { encoding: 'utf8', timeout: 180_000 }).trim(); }
function start(service, image, port, env = {}, extra = [], command = []) {
  const name = `${run}-${service}`;
  docker(['run', '--detach', '--name', name, '--network', run, '--network-alias', service,
    ...(port ? ['--publish', `127.0.0.1::${port}`] : []),
    ...Object.entries(env).flatMap(([key, value]) => ['--env', `${key}=${value}`]), ...extra, image, ...command]);
  containers.push(name);
  if (!port) return name;
  const config = JSON.parse(docker(['inspect', name]))[0];
  return `http://127.0.0.1:${config.NetworkSettings.Ports[`${port}/tcp`][0].HostPort}`;
}
async function until(name, check, timeout = 90_000) {
  const deadline = Date.now() + timeout;
  let last;
  do {
    try { const result = await check(); if (result) { report.checks.push(name); console.log(`PASS ${name}`); return result; } }
    catch (error) { last = error.message; }
    await new Promise(resolve => setTimeout(resolve, 500));
  } while (Date.now() < deadline);
  throw new Error(`${name} did not complete: ${last ?? 'condition not satisfied'}`);
}
async function api(base, path, method = 'GET', body, headers = {}) {
  const response = await fetch(`${base}${path}`, { method, headers: { 'content-type': 'application/json', ...headers },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(15_000) });
  const text = await response.text();
  let data; try { data = JSON.parse(text); } catch { data = text; }
  return { status: response.status, data, headers: response.headers };
}

try {
  for (const [service, image] of Object.entries(images)) {
    const config = JSON.parse(docker(['image', 'inspect', image]))[0];
    report.images[service] = { reference: image, id: config.Id, digests: config.RepoDigests };
    assert.ok(config.Config.User && !['0', 'root'].includes(config.Config.User), `${service} must run as non-root`);
    assert.ok(config.Config.Healthcheck?.Test, `${service} must have a healthcheck`);
  }
  docker(['network', 'create', run]);
  start('postgres', 'postgres:16', null, { POSTGRES_USER: 'ci', POSTGRES_PASSWORD: 'ci', POSTGRES_DB: 'ci' });
  start('redis', 'redis:7-alpine');
  await until('PostgreSQL ready', () => docker(['exec', `${run}-postgres`, 'pg_isready', '-U', 'ci', '-d', 'ci']).includes('accepting connections'));
  const stub = start('provider', 'node:22-alpine', 9099, {},
    ['--mount', `type=bind,src=${resolve('scripts/test/parity-gate/mock-provider.js')},dst=/app/mock-provider.js,readonly`],
    ['node', '/app/mock-provider.js', '9099']);
  const env = { DATABASE_URL: 'postgresql://ci:ci@postgres:5432/ci', REDIS_URL: 'redis://redis:6379',
    CONDUIT_API_TO_API_BACKEND_AUTH_KEY: master, CONDUIT_MIGRATION_MODE: 'Skip', CONDUIT_ENABLE_HTTPS_REDIRECTION: 'false',
    BatchSpending__FlushIntervalSeconds: '1',
    ConduitLLM__Messaging__Backend: 'Wolverine', ConduitLLM__Messaging__Wolverine__Transport: 'Postgresql',
    ConduitLLM__Messaging__Wolverine__AutoProvision: 'false' };
  docker(['run', '--rm', '--network', run, '--entrypoint', 'dotnet',
    ...Object.entries(env).flatMap(([key, value]) => ['--env', `${key}=${value}`]),
    images.admin, '/app/migrator/ConduitLLM.Migrator.dll']);
  const admin = start('admin', images.admin, 8080, env);
  const gateway = start('gateway', images.http, 8080, env);
  await until('Admin readiness', async () => (await api(admin, '/health/ready')).status === 200);
  await until('Gateway readiness', async () => (await api(gateway, '/health/ready')).status === 200);
  const adminApi = async (path, method, body) => {
    const concurrency = method === 'PATCH' ? { 'If-Match': (await api(admin, `/v1/admin${path}`, 'GET', undefined,
      { 'X-Master-Key': master })).headers.get('etag') } : {};
    const response = await api(admin, `/v1/admin${path}`, method, body, { 'X-Master-Key': master,
      ...concurrency,
      ...(method === 'PATCH' ? { 'content-type': 'application/merge-patch+json' } : {}) });
    assert.ok(response.status >= 200 && response.status < 300, `${method ?? 'GET'} ${path}: ${response.status} ${JSON.stringify(response.data)}`);
    return response.data;
  };
  const provider = await adminApi('/providers', 'POST', { providerType: 'openAI', providerName: 'CI local provider',
    baseUrl: 'http://provider:9099/v1', isEnabled: true });
  await adminApi(`/providers/${provider.id}/keys`, 'POST', { apiKey: 'local-no-paid-provider', isPrimary: true });
  const author = await adminApi('/model-authors', 'POST', { name: 'CI local author' });
  const series = await adminApi('/model-series', 'POST', { name: 'CI local series', authorId: author.id });
  const model = await adminApi('/models', 'POST', { name: 'ci-chat', modelSeriesId: series.id,
    supportsChat: true, supportsStreaming: true, inputModalities: ['text'], outputModalities: ['text'] });
  const identifier = await adminApi(`/models/${model.id}/identifiers`, 'POST', { identifier: 'ci-chat', provider: 1, isPrimary: true });
  const association = identifier.id;
  await adminApi('/model-costs', 'POST', { costName: 'CI deterministic pricing', pricingModel: 'standard', modelType: 'chat',
    inputCostPerMillionTokens: 2, outputCostPerMillionTokens: 4, modelProviderTypeAssociationIds: [association] });
  const mapping = await adminApi('/model-provider-mappings', 'POST', { modelAlias: 'ci-chat', providerModelId: 'ci-chat',
    providerId: provider.id, modelProviderTypeAssociationId: association, priority: 0, weight: 1, isEnabled: true });
  const group = await adminApi('/virtual-key-groups', 'POST', { groupName: 'CI funded group', initialBalance: 1 });
  const key = await adminApi('/virtual-keys', 'POST', { keyName: 'CI business key', virtualKeyGroupId: group.id });
  assert.ok(key.virtualKey, 'Admin must issue a usable virtual key');
  key.id = key.keyInfo.id;
  const authorization = { Authorization: `Bearer ${key.virtualKey}` };
  assert.equal((await api(gateway, '/v1/models')).status, 401);
  assert.equal((await api(gateway, '/v1/models', 'GET', undefined, { Authorization: 'Bearer invalid-ci-key' })).status, 401);
  report.checks.push('Missing and invalid authentication rejected');
  const request = { model: 'ci-chat', messages: [{ role: 'user', content: 'CI deterministic request' }], stream: false };
  const chat = (body = request, headers = authorization) => api(gateway, '/v1/chat/completions', 'POST', body, headers);
  const billed = async (count) => until(`Exactly ${count} successful requests billed`, async () => {
    const current = await adminApi(`/virtual-key-groups/${group.id}`);
    return Math.abs(current.balance - (1 - count * 0.0004)) < 0.00000001 &&
      Math.abs(current.lifetimeSpent - count * 0.0004) < 0.00000001;
  });
  const completion = await chat();
  assert.equal(completion.status, 200, JSON.stringify(completion.data));
  assert.equal(completion.data.choices[0].message.content, 'ok');
  assert.equal(completion.data.usage.total_tokens, 150);
  await billed(1);
  assert.equal((await api(gateway, '/v1/models', 'GET', undefined, { 'X-API-Key': key.virtualKey })).status, 200);
  assert.equal((await chat({ ...request, model: 'ci-unknown-model' })).status, 404);
  assert.equal((await chat({ messages: request.messages })).status, 400);
  for (const status of [401, 429, 500]) {
    assert.equal((await api(stub, '/control', 'POST', { failRate: 1, failStatus: status })).status, 200);
    const failure = await chat();
    assert.ok(failure.status >= 400, `Provider ${status} must fail: ${failure.status}`);
    report.checks.push(`Provider ${status} failure is non-billable`);
  }
  await api(stub, '/control', 'POST', { failRate: 0 });
  assert.equal((await chat()).status, 200, 'Provider recovers after consecutive failures');
  await billed(2);
  await adminApi(`/virtual-keys/${key.id}`, 'PATCH', { isEnabled: false });
  await until('Disabled key invalidates authentication cache', async () => (await chat()).status === 401);
  await adminApi(`/virtual-keys/${key.id}`, 'PATCH', { isEnabled: true });
  await until('Re-enabled key restores authentication', async () =>
    (await api(gateway, '/v1/models', 'GET', undefined, authorization)).status === 200);
  const emptyGroup = await adminApi('/virtual-key-groups', 'POST', { groupName: 'CI empty group', initialBalance: 0 });
  const emptyKey = await adminApi('/virtual-keys', 'POST', { keyName: 'CI empty key', virtualKeyGroupId: emptyGroup.id });
  assert.equal((await chat(request, { Authorization: `Bearer ${emptyKey.virtualKey}` })).status, 402);
  for (const limit of ['rateLimitRpm', 'rateLimitRpd']) {
    const limited = await adminApi('/virtual-keys', 'POST', { keyName: `CI ${limit}`, virtualKeyGroupId: group.id, [limit]: 1 });
    const headers = { Authorization: `Bearer ${limited.virtualKey}` };
    assert.equal((await chat(request, headers)).status, 200);
    const rejected = await chat(request, headers);
    assert.equal(rejected.status, 429);
    assert.ok(rejected.headers.get('retry-after'));
    report.checks.push(`${limit} rejects excess requests`);
  }
  await billed(4);
  assert.equal((await api(gateway, '/v1/models', 'GET', undefined, authorization)).status, 200);
  assert.ok(docker(['exec', `${run}-redis`, 'redis-cli', '--scan', '--pattern', 'vkey:*']),
    'A successful authenticated read must write a real Redis cache entry');
  report.checks.push('Authentication state persisted in Redis without EF navigation cycles');
  await until('Model discovery reads the seeded route', async () =>
    (await api(gateway, '/v1/models', 'GET', undefined, authorization)).data.data?.some(model => model.id === 'ci-chat'));
  await adminApi('/model-provider-mappings/bulk/disable', 'POST', [mapping.id]);
  await until('Admin mutation invalidates Gateway discovery cache', async () =>
    !(await api(gateway, '/v1/models', 'GET', undefined, authorization)).data.data?.some(model => model.id === 'ci-chat'));
  let requestsDuringInvalidation = 0;
  await until('Disabled routing is rejected after invalidation', async () => {
    const disabled = await chat();
    if (disabled.status === 200) requestsDuringInvalidation++;
    return disabled.status === 404;
  }, 10_000);
  await adminApi('/model-provider-mappings/bulk/enable', 'POST', [mapping.id]);
  await until('Route recovers after cache invalidation', async () => (await chat()).status === 200, 10_000);
  await billed(5 + requestsDuringInvalidation);
  // The browser extension uses these same real hosts before teardown.
  if (process.env.CI_BROWSER_MODULE) {
    const browser = await import(pathToFileURL(resolve(process.env.CI_BROWSER_MODULE)).href);
    await browser.smoke({ start, admin, gateway, env, master, group, key, output, report });
  }
  for (const service of ['admin', 'gateway', ...(images.webadmin ? ['webadmin'] : [])]) {
    await until(`${service} packaged Docker healthcheck`, () => JSON.parse(docker(['inspect', `${run}-${service}`]))[0].State.Health.Status === 'healthy');
  }
  report.succeeded = true;
} finally {
  for (const name of containers.reverse()) {
    try {
      const logs = spawnSync('docker', ['logs', name], { encoding: 'utf8', timeout: 15_000 });
      writeFileSync(`${output}/${name.slice(run.length + 1)}.log`, (logs.stdout ?? '') + (logs.stderr ?? ''));
    } catch { /* retain other evidence */ }
    try { docker(['rm', '--force', name]); } catch { /* only this run's named containers */ }
  }
  try { docker(['network', 'rm', run]); } catch { /* network may not have been created */ }
  writeFileSync(`${output}/evidence.json`, JSON.stringify(report, null, 2));
}

