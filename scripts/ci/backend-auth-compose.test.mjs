import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const backendKey = 'CONDUIT_API_TO_API_BACKEND_AUTH_KEY';
const grafanaKey = 'CONDUIT_GRAFANA_ADMIN_PASSWORD';
const composeFile = fileURLToPath(new URL('../../docker-compose.yml', import.meta.url));
const developmentFile = fileURLToPath(new URL('../../docker-compose.dev.yml', import.meta.url));

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
      'compose', '--env-file', envFile, ...files, 'config', '--format', 'json',
    ], {
      cwd: owned, env: environment, encoding: 'utf8', windowsHide: true,
      timeout: 30_000, maxBuffer: 10 * 1024 * 1024,
    });
    // Resolved Compose output includes credentials. Only boolean assertions may inspect it.
    assert.ok(!result.error, 'Docker Compose config must be available');
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
      assert.ok(result.status === 0, `${mode}: Docker Compose config must succeed`);
      let document;
      try { document = JSON.parse(result.stdout); }
      catch { assert.fail(`${mode}: Docker Compose config must return valid JSON`); }
      for (const name of ['api', 'admin', 'webadmin']) {
        assert.ok(document?.services?.[name]?.environment?.[backendKey] === secret,
          `${mode}: ${name} must receive the exact configured backend secret`);
      }
      for (const [name, service] of Object.entries(document.services)) {
        if (['api', 'admin', 'webadmin'].includes(name)) continue;
        assert.ok(!Object.hasOwn(service.environment ?? {}, backendKey),
          `${mode}: ${name} must not receive the backend secret`);
      }
    });
  });

  for (const [label, value] of [['missing', undefined], ['empty', '']]) {
    test(`${mode} Compose rejects a ${label} backend secret before starting services`, () => {
      resolveCompose(development, value, result => {
        assert.ok(result.status !== null && result.status !== 0,
          `${mode}: a ${label} backend secret must fail configuration`);
        assert.ok(result.stderr.includes(backendKey) && result.stderr.includes('strong shared backend secret'),
          `${mode}: configuration failure must explain the required backend secret`);
      });
    });
  }
}
