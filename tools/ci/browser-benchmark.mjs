import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { cpus } from 'node:os';
import assert from 'node:assert/strict';
import { browserMethod, summarizeSamples, compareBrowserBaseline } from '../../scripts/ci/browser-performance-policy.mjs';

export async function benchmark({ page, url, browser, images, output }) {
  const samplesMs = [];
  for (let iteration = 0; iteration < browserMethod.warmups + browserMethod.samples; iteration++) {
    const response = await page.goto(url, { waitUntil: 'load', timeout: 30_000 });
    assert.equal(response.status(), 200);
    await page.getByRole('heading', { name: 'Virtual Keys', exact: true }).waitFor({ timeout: 30_000 });
    await Promise.all(browserMethod.fixtureKeys.map(name => page.getByText(name, { exact: true }).waitFor({ timeout: 30_000 })));
    const readyMs = await page.evaluate(() => performance.now());
    if (iteration >= browserMethod.warmups) samplesMs.push(readyMs);
  }
  const result = { schemaVersion: 1, method: browserMethod, samplesMs, summary: summarizeSamples(samplesMs),
    metric: 'navigation start to all four real fixture keys rendered; milliseconds', engine: browser.version(),
    environment: { platform: process.platform, architecture: process.arch, cpuCount: cpus().length,
      cpuModel: cpus()[0]?.model ?? 'unknown', runnerImage: process.env.ImageVersion ?? null },
    source: { sha: process.env.GITHUB_SHA ?? null,
      runUrl: process.env.GITHUB_RUN_ID ? `https://github.com/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}` : null },
    images, succeeded: true };
  const path = new URL('../../scripts/ci/browser-performance-baseline.json', import.meta.url);
  const reference = existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : null;
  result.comparison = compareBrowserBaseline(reference, result);
  writeFileSync(`${output}/browser-benchmark.json`, JSON.stringify(result, null, 2));
  console.log(`Controlled browser benchmark: median ${result.summary.medianMs.toFixed(1)} ms, p95 ${result.summary.p95Ms.toFixed(1)} ms (${result.comparison.status}).`);
  return result;
}
