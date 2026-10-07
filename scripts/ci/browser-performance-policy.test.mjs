import { test } from 'node:test';
import assert from 'node:assert/strict';
import { browserMethod, summarizeSamples, validateBrowserBaseline, compareBrowserBaseline } from './browser-performance-policy.mjs';
const samplesMs = [70, 20, 50, 40, 30, 60, 10];
const reference = { schemaVersion: 1, method: browserMethod, samplesMs, summary: summarizeSamples(samplesMs),
  succeeded: true, source: { sha: 'a'.repeat(40), runUrl: 'https://github.com/nickna/Conduit/actions/runs/1' },
  images: Object.fromEntries(['admin', 'http', 'webadmin'].map(service => [service, { id: `sha256:${'a'.repeat(64)}` }])),
  engine: 'controlled-chromium', environment: { platform: 'linux', architecture: 'x64', cpuCount: 4, cpuModel: 'fixture', runnerImage: 'fixture' } };

test('controlled browser distribution uses every sample and rejects absent or invalid timing', () => {
  assert.deepEqual(summarizeSamples(samplesMs), { medianMs: 40, p95Ms: 70, minimumMs: 10, maximumMs: 70 });
  assert.throws(() => summarizeSamples(samplesMs.slice(1)));
  assert.throws(() => summarizeSamples([NaN, ...samplesMs.slice(1)]));
});
test('browser reference requires measured method, fixture, source and exact image identities', () => {
  validateBrowserBaseline(reference);
  assert.throws(() => validateBrowserBaseline({ ...reference, source: { sha: 'invented', runUrl: 'local' } }));
  assert.throws(() => validateBrowserBaseline({ ...reference, summary: { medianMs: 0 } }));
});
test('browser comparison reports compatible distributions without machine-speed correctness thresholds', () => {
  assert.equal(compareBrowserBaseline(null, reference).status, 'calibration');
  assert.equal(compareBrowserBaseline(reference, reference).medianRatio, 1);
  assert.equal(compareBrowserBaseline(reference, { ...reference, engine: 'other' }).status, 'different-environment');
  assert.equal(compareBrowserBaseline(reference, { ...reference, environment: { ...reference.environment, cpuCount: 2 } }).status, 'different-environment');
});
