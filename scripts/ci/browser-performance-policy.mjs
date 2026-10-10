import assert from 'node:assert/strict';

export const browserMethod = { id: 'authenticated-virtualkeys-ready-v1', warmups: 2, samples: 7,
  viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1, locale: 'en-US', timezoneId: 'UTC',
  fixtureKeys: ['CI business key', 'CI empty key', 'CI rateLimitRpm', 'CI rateLimitRpd'] };

export function summarizeSamples(samples) {
  assert.equal(samples.length, browserMethod.samples, 'Require every controlled browser sample');
  assert.ok(samples.every(value => Number.isFinite(value) && value > 0), 'Navigation samples must be positive finite milliseconds');
  const sorted = [...samples].sort((a, b) => a - b);
  return { medianMs: sorted[Math.floor(sorted.length / 2)], p95Ms: sorted[Math.ceil(sorted.length * 0.95) - 1],
    minimumMs: sorted[0], maximumMs: sorted.at(-1) };
}

export function validateBrowserBaseline(report) {
  assert.equal(report.schemaVersion, 1);
  assert.deepEqual(report.method, browserMethod, 'Benchmark method or fixture changed; establish a new reference');
  assert.deepEqual(report.summary, summarizeSamples(report.samplesMs));
  assert.equal(report.succeeded, true);
  assert.match(report.source.sha, /^[a-f0-9]{40}$/);
  assert.match(report.source.runUrl, /^https:\/\/github\.com\/nickna\/Conduit\/actions\/runs\/\d+$/);
  for (const service of ['admin', 'http', 'webadmin']) assert.match(report.images[service].id, /^sha256:[a-f0-9]{64}$/);
}

export function compareBrowserBaseline(reference, measured) {
  if (!reference) return { status: 'calibration', reason: 'Retain this controlled run as the initial reference.' };
  validateBrowserBaseline(reference);
  const fields = ['platform', 'architecture', 'cpuCount', 'cpuModel', 'runnerImage'];
  if (reference.engine !== measured.engine || fields.some(field => reference.environment[field] !== measured.environment[field]))
    return { status: 'different-environment', reference: reference.source.runUrl,
      reason: 'Browser or hosted-runner environment differs; raw distributions remain reported without a misleading ratio.' };
  return { status: 'comparable-inputs', reference: reference.source.runUrl,
    medianRatio: measured.summary.medianMs / reference.summary.medianMs,
    p95Ratio: measured.summary.p95Ms / reference.summary.p95Ms };
}
