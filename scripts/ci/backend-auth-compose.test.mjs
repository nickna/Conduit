import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const backendKey = 'CONDUIT_API_TO_API_BACKEND_AUTH_KEY';
const grafanaKey = 'CONDUIT_GRAFANA_ADMIN_PASSWORD';
const composeFile = fileURLToPath(new URL('../../docker-compose.yml', import.meta.url));
const developmentFile = fileURLToPath(new URL('../../docker-compose.dev.yml', import.meta.url));
const { parseDocument } = createRequire(new URL('../../tools/ci/package.json', import.meta.url))('yaml');

function assertCompose(condition, result, message) {
  if (condition) return;
  // Emit fixed diagnostic categories, never raw stderr/stdout or environment values.
  const versionResult = spawnSync('docker', ['compose', 'version', '--short'], {
    encoding: 'utf8', windowsHide: true, timeout: 10_000,
  });
  const version = /^v?\d+\.\d+\.\d+(?:[-+][\w.-]+)?$/u.test(versionResult.stdout?.trim() ?? '')
    ? versionResult.stdout.trim() : 'unavailable';
  const stderr = result.stderr ?? '';
  const reason = /additional propert(?:y|ies).*initial_sync.*not allowed/iu.test(stderr)
    ? 'unsupported develop.watch.initial_sync property'
    : stderr.includes(backendKey) && stderr.includes('strong shared backend secret')
      ? 'required backend secret rejected'
      : /(?:unknown command|not a docker command)/iu.test(stderr)
        ? 'Compose command unavailable'
        : 'configuration or process failure; credential-bearing output suppressed';
  assert.fail(`${message} (Compose ${version}, exit ${result.status ?? 'unavailable'}: ${reason})`);
}

function resolveCompose(development, secret, check) {
  const parent = resolve(tmpdir());
  const owned = mkdtempSync(join(parent, 'conduit-backend-auth-compose-'));
  try {
    const envFile = join(owned, 'fixture.env');
    const values = { [grafanaKey]: randomBytes(24).toString('hex') };
    if (secret !== undefined) values[backendKey] = secret;
    writeFileSync(envFile, Object.entries(values)
      .map(([key, value]) => `${key}='${value}'`).join('\n') + '\n', { mode: 0o600 });
    const environment = Object.fromEntries(Object.entries(process.env).filter(([key]) =>
      !/^COMPOSE_/iu.test(key) && ![backendKey, grafanaKey].includes(key.toUpperCase())));
    const files = ['-f', composeFile, ...(development ? ['-f', developmentFile] : [])];
    const result = spawnSync('docker', [
      // JSON rendering omits legacy watch extensions; YAML retains their runtime settings.
      'compose', '--env-file', envFile, ...files, 'config', '--format', 'yaml',
    ], {
      cwd: owned, env: environment, encoding: 'utf8', windowsHide: true,
      timeout: 30_000, maxBuffer: 10 * 1024 * 1024,
    });
    // Resolved Compose output includes credentials. Only boolean assertions may inspect it.
    assertCompose(!result.error, result, 'Docker Compose config must be available');
    check(result);
  } finally {
    const target = resolve(owned);
    assert.ok(dirname(target) === parent && basename(target).startsWith('conduit-backend-auth-compose-'),
      'cleanup must remain inside the exact owned temporary directory');
    rmSync(target, { recursive: true, force: true });
  }
}

for (const development of [false, true]) {
  const mode = development ? 'development' : 'base';
  test(`${mode} Compose preserves the configured backend secret in every backend client and service`, () => {
    const secret = randomBytes(24).toString('hex');
    resolveCompose(development, secret, result => {
      assertCompose(result.status === 0, result, `${mode}: Docker Compose config must succeed`);
      const parsed = parseDocument(result.stdout, { uniqueKeys: true });
      assert.ok(parsed.errors.length === 0, `${mode}: Docker Compose config must return valid YAML`);
      const document = parsed.toJS();
      for (const name of ['api', 'admin', 'webadmin']) {
        assert.ok(document?.services?.[name]?.environment?.[backendKey] === secret,
          `${mode}: ${name} must receive the exact configured backend secret`);
      }
      for (const [name, service] of Object.entries(document.services)) {
        if (['api', 'admin', 'webadmin'].includes(name)) continue;
        assert.ok(!Object.hasOwn(service.environment ?? {}, backendKey),
          `${mode}: ${name} must not receive the backend secret`);
      }
      if (development) {
        const sync = document.services.webadmin.develop?.watch?.find(trigger => trigger.action === 'sync');
        assert.ok(sync?.initial_sync === true || sync?.['x-initialSync'] === true,
          'development: Compose Watch must retain initial synchronization');
      }
    });
  });

  for (const [label, value] of [['missing', undefined], ['empty', '']]) {
    test(`${mode} Compose rejects a ${label} backend secret before starting services`, () => {
      resolveCompose(development, value, result => {
        assertCompose(result.status !== null && result.status !== 0, result,
          `${mode}: a ${label} backend secret must fail configuration`);
        assertCompose(result.stderr.includes(backendKey) && result.stderr.includes('strong shared backend secret'), result,
          `${mode}: configuration failure must explain the required backend secret`);
      });
    });
  }
}
