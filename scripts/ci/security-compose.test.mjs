import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const composeFile = fileURLToPath(new URL('../../docker-compose.yml', import.meta.url));
const exampleFile = new URL('../../.env.example', import.meta.url);
const defaults = {
  CONDUIT_ADMIN_RATE_LIMITING_ENABLED: 'true',
  CONDUIT_ADMIN_RATE_LIMIT_MAX_REQUESTS: '100',
  CONDUIT_ADMIN_RATE_LIMIT_WINDOW_SECONDS: '60',
  CONDUIT_ADMIN_IP_FILTERING_ENABLED: 'false',
  CONDUIT_ADMIN_IP_BANNING_ENABLED: 'false',
  CONDUIT_ADMIN_MAX_FAILED_AUTH_ATTEMPTS: '5',
  CONDUIT_ADMIN_AUTH_BAN_DURATION_MINUTES: '30',
  CONDUIT_CORE_RATE_LIMITING_ENABLED: 'true',
  CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS: '1000',
  CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS: '60',
  CONDUIT_CORE_IP_FILTERING_ENABLED: 'false',
  CONDUIT_CORE_SECURITY_HEADERS_ENABLED: 'true',
};
const canonical = {
  CONDUIT_ADMIN_RATE_LIMITING_ENABLED: 'false',
  CONDUIT_ADMIN_RATE_LIMIT_MAX_REQUESTS: '17',
  CONDUIT_ADMIN_RATE_LIMIT_WINDOW_SECONDS: '19',
  CONDUIT_ADMIN_IP_FILTERING_ENABLED: 'true',
  CONDUIT_ADMIN_IP_BANNING_ENABLED: 'true',
  CONDUIT_ADMIN_MAX_FAILED_AUTH_ATTEMPTS: '3',
  CONDUIT_ADMIN_AUTH_BAN_DURATION_MINUTES: '23',
  CONDUIT_CORE_RATE_LIMITING_ENABLED: 'false',
  CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS: '211',
  CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS: '29',
  CONDUIT_CORE_IP_FILTERING_ENABLED: 'true',
  CONDUIT_CORE_SECURITY_HEADERS_ENABLED: 'false',
};
const aliases = {
  CONDUIT_ADMIN_RATE_LIMITING_MAX_REQUESTS: '37',
  CONDUIT_ADMIN_RATE_LIMITING_WINDOW_SECONDS: '41',
  CONDUIT_ADMIN_FAILED_AUTH_MAX_ATTEMPTS: '7',
  CONDUIT_ADMIN_FAILED_AUTH_BAN_DURATION_MINUTES: '43',
};
const securityKeys = new Set([...Object.keys(defaults), ...Object.keys(aliases)]);
const legacyExpected = {
  ...defaults,
  CONDUIT_ADMIN_RATE_LIMIT_MAX_REQUESTS: '37',
  CONDUIT_ADMIN_RATE_LIMIT_WINDOW_SECONDS: '41',
  CONDUIT_ADMIN_MAX_FAILED_AUTH_ATTEMPTS: '7',
  CONDUIT_ADMIN_AUTH_BAN_DURATION_MINUTES: '43',
};

function advertisedSecurity() {
  const advertised = {};
  for (const line of readFileSync(exampleFile, 'utf8').split(/\r?\n/u)) {
    const match = /^(CONDUIT_(?:ADMIN|CORE)_[A-Z0-9_]+)=([^#]*)/u.exec(line);
    if (match && securityKeys.has(match[1])) {
      advertised[match[1]] = match[2].trim().replace(/^(['"])(.*)\1$/u, '$2');
    }
  }
  assert.deepEqual(Object.keys(advertised).sort(), Object.keys(defaults).sort(),
    '.env.example must advertise all twelve canonical API security settings');
  for (const key of Object.keys(defaults)) {
    assert.ok(advertised[key] === defaults[key], `${key} advertised default must match the contract`);
  }
  return advertised;
}

function checkResolvedCompose(label, values, expected) {
  const parent = resolve(tmpdir());
  const owned = mkdtempSync(join(parent, 'conduit-security-compose-'));
  const fixtureSecret = randomBytes(24).toString('hex');
  const unrelatedKey = 'CONDUIT_SECURITY_COMPOSE_FIXTURE_SECRET';
  try {
    const envFile = join(owned, 'fixture.env');
    writeFileSync(envFile, Object.entries({
      CONDUIT_GRAFANA_ADMIN_PASSWORD: randomBytes(24).toString('hex'),
      CONDUIT_API_TO_API_BACKEND_AUTH_KEY: randomBytes(24).toString('hex'),
      [unrelatedKey]: fixtureSecret,
      ...values,
    }).map(([key, value]) => `${key}=${value}`).join('\n') + '\n', { mode: 0o600 });
    const childEnvironment = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
      !/^CONDUIT_(?:ADMIN|CORE)_/iu.test(key) && !/^COMPOSE_/iu.test(key) &&
      !['CONDUIT_GRAFANA_ADMIN_PASSWORD', 'CONDUIT_API_TO_API_BACKEND_AUTH_KEY', unrelatedKey].includes(key.toUpperCase())));
    const result = spawnSync('docker', [
      'compose', '--env-file', envFile, '-f', composeFile, 'config', '--format', 'json',
    ], {
      cwd: owned, env: childEnvironment, encoding: 'utf8', windowsHide: true,
      timeout: 30_000, maxBuffer: 10 * 1024 * 1024,
    });
    // Raw Compose output can contain unrelated credentials. Never include it in failures.
    assert.ok(!result.error && result.status === 0,
      `${label}: Docker Compose config must be available and succeed (exit ${result.status ?? 'unavailable'})`);
    let document;
    try { document = JSON.parse(result.stdout); }
    catch { assert.fail(`${label}: Docker Compose config must return valid JSON`); }
    assert.ok(document?.services && typeof document.services === 'object',
      `${label}: resolved services must exist`);
    assert.ok(document.services.admin && document.services.api, `${label}: both API services must exist`);

    for (const [name, service] of Object.entries(document.services)) {
      const environment = service.environment ?? {};
      const intended = Object.keys(defaults).filter(key =>
        (name === 'admin' && key.startsWith('CONDUIT_ADMIN_')) ||
        (name === 'api' && key.startsWith('CONDUIT_CORE_')));
      assert.deepEqual(Object.keys(environment).filter(key => securityKeys.has(key)).sort(),
        intended.sort(), `${label}: ${name} must receive only its canonical API security keys`);
      for (const key of intended) {
        // Boolean assertions prevent even an accidentally forwarded secret being printed.
        assert.ok(environment[key] === expected[key], `${label}: ${name} must resolve ${key} correctly`);
      }
      assert.ok(!Object.hasOwn(environment, unrelatedKey), `${label}: ${name} must not forward the env-file fixture secret`);
      assert.ok(!Object.values(environment).some(value => String(value).includes(fixtureSecret)),
        `${label}: ${name} must not expose the fixture-secret value`);
    }
  } finally {
    const target = resolve(owned);
    assert.ok(dirname(target) === parent && basename(target).startsWith('conduit-security-compose-'),
      'cleanup must remain inside the exact owned temporary directory');
    rmSync(target, { recursive: true, force: true });
  }
}

test('Compose forwards safe security defaults from an otherwise empty env file', () => {
  checkResolvedCompose('defaults', {}, defaults);
});
test('Compose forwards every advertised .env.example security value', () => {
  checkResolvedCompose('advertised values', advertisedSecurity(), defaults);
});
test('Compose forwards nondefault canonical security settings to their intended API', () => {
  checkResolvedCompose('canonical values', canonical, canonical);
});
test('Compose resolves historical Admin numeric aliases into canonical keys', () => {
  checkResolvedCompose('legacy aliases', aliases, legacyExpected);
});
test('canonical Admin values win when historical numeric aliases conflict', () => {
  checkResolvedCompose('canonical precedence', { ...aliases, ...canonical }, canonical);
});
