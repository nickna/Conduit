import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { warningKeys, enforceWarnings } from './redocly-warning-policy.mjs';

const require = createRequire(import.meta.url);
const cli = resolve(dirname(require.resolve('@redocly/cli/package.json')), 'bin/cli.js');

test('real Redocly warnings fail policy despite a successful CLI exit; truncation also fails', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'conduit-redocly-test-'));
  try {
    const spec = join(fixture, 'openapi.json');
    const config = join(fixture, 'redocly.yaml');
    writeFileSync(spec, JSON.stringify({
      openapi: '3.1.1',
      info: { title: 'Warning regression', version: '1', license: { name: 'MIT', identifier: 'MIT' } },
      servers: [{ url: '/' }], tags: [{ name: 'Widgets', description: 'Widget operations.' }],
      paths: { '/widgets': { get: {
        operationId: 'Widgets_List', tags: ['Widgets'], security: [],
        responses: { 200: { description: 'Widget list' }, 404: { description: 'Not found' } },
        // Deliberately missing summary: exactly one warning and no errors.
      } } },
    }));
    writeFileSync(config, 'extends: [recommended-strict]\nrules:\n  operation-summary: warn\n');
    const lint = maximum => JSON.parse(execFileSync(process.execPath,
      [cli, 'lint', spec, '--config', config, '--format=json', `--max-problems=${maximum}`],
      { encoding: 'utf8', timeout: 30_000, stdio: ['ignore', 'pipe', 'pipe'] }));
    const report = lint(1_000_000);
    assert.equal(report.totals.errors, 0);
    assert.equal(report.totals.warnings, 1);
    assert.throws(() => enforceWarnings(warningKeys([report])), /warnings are not allowed/);
    assert.throws(() => warningKeys([lint(0)]), /Truncated/);
  } finally {
    // fixture is an absolute directory returned by mkdtemp under the OS temp root.
    assert(fixture.startsWith(join(tmpdir(), 'conduit-redocly-test-')));
    rmSync(fixture, { recursive: true, force: true });
  }
});
