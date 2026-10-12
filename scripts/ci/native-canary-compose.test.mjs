import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { test } from 'node:test';

const { parseDocument } = createRequire(new URL('../../tools/ci/package.json', import.meta.url))('yaml');

test('all canary cohorts check the migrated schema before reporting readiness', () => {
  const source = readFileSync(new URL('../../deploy/native-canary/compose.yml', import.meta.url), 'utf8');
  const document = parseDocument(source, { merge: true, uniqueKeys: true });
  assert.deepEqual(document.errors, [], 'canary compose must be valid YAML with unique keys');
  const compose = document.toJS();
  assert.deepEqual(Object.keys(compose.services).sort(), [
    'admin-jit', 'admin-native', 'gateway-jit', 'gateway-native',
  ]);

  for (const [name, service] of Object.entries(compose.services)) {
    assert.equal(service.environment.CONDUIT_MIGRATION_MODE, 'Wait',
      `${name} must check schema readiness while migrations remain release-owned`);
    assert.match(service.environment.DATABASE_URL, /^\$\{DATABASE_URL:\?/u,
      `${name} must require the migrated canary database`);
    assert.match(service.environment.REDIS_URL, /^\$\{REDIS_URL:\?/u,
      `${name} must require the shared canary Redis instance`);
    assert.equal(service.environment.ConduitLLM__Messaging__Wolverine__AutoProvision, 'false',
      `${name} must leave infrastructure provisioning to the release`);
  }
});
