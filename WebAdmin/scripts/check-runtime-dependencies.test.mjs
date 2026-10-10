import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { checkRuntimeDependencies } from './check-runtime-dependencies.mjs';

function standalone(t) {
  const root = mkdtempSync(join(tmpdir(), 'conduit-runtime-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  writeFileSync(join(root, 'server.js'), '');
  writeFileSync(join(root, 'package.json'), JSON.stringify({ name: 'conduit-webadmin' }));
  return root;
}

test('standalone runtime without braces passes', t => {
  assert.deepEqual(checkRuntimeDependencies(standalone(t)), { packages: 1, braces: 0 });
});

test('nested or renamed braces packages cannot enter the runtime', t => {
  const root = standalone(t);
  const nested = join(root, 'node_modules', 'parent', 'node_modules', 'renamed');
  mkdirSync(nested, { recursive: true });
  writeFileSync(join(nested, 'package.json'), JSON.stringify({ name: 'braces', version: '3.0.3' }));
  assert.throws(() => checkRuntimeDependencies(root), /Development-only braces must not ship/);
});

test('missing runtime output and unreadable manifests fail closed', t => {
  const root = standalone(t);
  for (const manifest of [null, [], 3, { name: 3 }]) {
    writeFileSync(join(root, 'package.json'), JSON.stringify(manifest));
    assert.throws(() => checkRuntimeDependencies(root), /Invalid package manifest/);
  }
  writeFileSync(join(root, 'package.json'), '{broken');
  assert.throws(() => checkRuntimeDependencies(root), SyntaxError);
  rmSync(join(root, 'server.js'));
  assert.throws(() => checkRuntimeDependencies(root), /ENOENT/);
});
