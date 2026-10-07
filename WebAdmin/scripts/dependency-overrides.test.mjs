import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { checkDependencyOverrides, checkInstalledCoverageParser } from './check-dependency-overrides.mjs';

const loader = 'node_modules/@istanbuljs/load-nyc-config';
const manifest = { overrides: { '@istanbuljs/load-nyc-config': { 'js-yaml': '4.3.2' } } };
function lock() { return { lockfileVersion: 3, packages: {
  [loader]: { version: '1.1.0', dependencies: { 'js-yaml': '^3.13.1' } },
  'node_modules/js-yaml': { version: '4.3.2' }
} }; }

test('a hoisted maintained parser does not hide a stale nested override', () => {
  const stale = lock();
  stale.packages[`${loader}/node_modules/js-yaml`] = { version: '3.15.2' };
  assert.throws(() => checkDependencyOverrides(manifest, stale), /Stale scoped override.*3\.15\.2.*4\.3\.2/);
  delete stale.packages[`${loader}/node_modules/js-yaml`];
  assert.deepEqual(checkDependencyOverrides(manifest, stale), { consumers: 1, parser: '4.3.2' });
});

test('every nested loader must resolve the overridden parser from its own ancestors', () => {
  const nested = lock();
  nested.packages[`node_modules/parent/${loader}`] = nested.packages[loader];
  nested.packages['node_modules/parent/node_modules/js-yaml'] = { version: '3.15.2' };
  assert.throws(() => checkDependencyOverrides(manifest, nested), /Stale scoped override/);
  nested.packages['node_modules/parent/node_modules/js-yaml'].version = '4.3.2';
  assert.equal(checkDependencyOverrides(manifest, nested).consumers, 2);
});

test('missing parser/loader, malformed locks and unpinned overrides fail closed', () => {
  const missing = lock();
  delete missing.packages['node_modules/js-yaml'];
  assert.throws(() => checkDependencyOverrides(manifest, missing), /Missing locked js-yaml/);
  for (const invalid of [{}, { lockfileVersion: 2, packages: {} }, { lockfileVersion: 3, packages: {} }])
    assert.throws(() => checkDependencyOverrides(manifest, invalid));
  for (const invalid of [{}, { overrides: { '@istanbuljs/load-nyc-config': { 'js-yaml': '^4.3.2' } } }])
    assert.throws(() => checkDependencyOverrides(invalid, lock()), /exact coverage parser override/);
});

test('an orphaned or nested vulnerable sprintf-js lock entry still requires pruning', () => {
  for (const path of ['node_modules/sprintf-js', 'node_modules/parent/node_modules/sprintf-js']) {
    const orphaned = lock();
    orphaned.packages[path] = { version: '1.0.3' };
    assert.throws(() => checkDependencyOverrides(manifest, orphaned), /Obsolete vulnerable coverage dependency/);
  }
});

test('a clean lock does not excuse a stale parser in the installed consumer graph', t => {
  const directory = mkdtempSync(join(tmpdir(), 'conduit-overrides-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const consumer = join(directory, loader);
  const parser = join(consumer, 'node_modules/js-yaml');
  mkdirSync(parser, { recursive: true });
  writeFileSync(join(directory, 'package.json'), '{}');
  writeFileSync(join(consumer, 'package.json'), JSON.stringify({ name: '@istanbuljs/load-nyc-config', main: 'index.js' }));
  writeFileSync(join(consumer, 'index.js'), '');
  writeFileSync(join(parser, 'package.json'), JSON.stringify({ name: 'js-yaml', version: '3.15.2' }));
  assert.equal(checkDependencyOverrides(manifest, lock()).parser, '4.3.2');
  assert.throws(() => checkInstalledCoverageParser(directory, '4.3.2'), /Installed coverage parser is 3\.15\.2/);
});
