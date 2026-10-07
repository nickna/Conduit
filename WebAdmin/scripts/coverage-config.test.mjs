import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Exercise the actual consumer of the targeted js-yaml override in #1477.
const require = createRequire(import.meta.url);
const { loadNycConfig } = require('@istanbuljs/load-nyc-config');
function fixture(t) {
  const directory = mkdtempSync(join(tmpdir(), 'conduit-coverage-config-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  writeFileSync(join(directory, 'package.json'), '{}');
  return directory;
}

test('coverage loader preserves YAML types, glob lists, merge keys and extended configuration', async t => {
  const cwd = fixture(t);
  writeFileSync(join(cwd, 'base.yaml'), 'all: true\ninclude: ["src/**/*.{ts,tsx}"]\nextension: [.ts, .tsx]\nbranches: 82.5\n');
  writeFileSync(join(cwd, '.nycrc.yaml'), 'extends: ./base.yaml\ncheck-coverage: true\nexclude: "src/generated/**"\nthresholds:\n  <<: &thresholds {lines: 85, functions: 80}\n  branches: 75\n');
  const config = await loadNycConfig({ cwd });
  assert.equal(config.all, true);
  assert.equal(config.checkCoverage, true);
  assert.equal(config.branches, 82.5);
  assert.deepEqual(config.include, ['src/**/*.{ts,tsx}']);
  assert.deepEqual(config.extension, ['.ts', '.tsx']);
  assert.deepEqual(config.exclude, ['src/generated/**']);
  assert.deepEqual(config.thresholds, { lines: 85, functions: 80, branches: 75 });
});

test('coverage loader continues loading JSON configuration that extends YAML', async t => {
  const cwd = fixture(t);
  writeFileSync(join(cwd, 'base.yml'), 'all: true\nreporter: [text, json]\n');
  writeFileSync(join(cwd, '.nycrc.json'), JSON.stringify({ extends: './base.yml', 'check-coverage': true, lines: 90 }));
  const config = await loadNycConfig({ cwd });
  assert.equal(config.all, true);
  assert.equal(config.checkCoverage, true);
  assert.equal(config.lines, 90);
  assert.deepEqual(config.reporter, ['text', 'json']);
});

test('coverage YAML refuses executable JavaScript tags', async t => {
  const cwd = fixture(t);
  writeFileSync(join(cwd, '.nycrc.yml'), 'include: !!js/function "function () { return 1; }"\n');
  await assert.rejects(loadNycConfig({ cwd }), /unknown tag/);
});
