import { appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { parseTag, validatedRun, requireAggregate } from './release-policy.mjs';

const policy = parseTag(process.env.GITHUB_REF_NAME);
const sha = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
execFileSync('git', ['fetch', 'origin', 'master', '--no-tags']);
execFileSync('git', ['merge-base', '--is-ancestor', sha, 'origin/master']);
const base = `${process.env.GITHUB_API_URL}/repos/${process.env.GITHUB_REPOSITORY}`;
async function get(path) {
  const response = await fetch(`${base}/${path}`, {
    headers: { Authorization: `Bearer ${process.env.GH_TOKEN}`, Accept: 'application/vnd.github+json' },
    signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok) throw new Error(`GitHub validation lookup ${response.status}: ${path}`);
  return response.json();
}

const deadline = Date.now() + 10 * 60_000;
let run;
while (!run) {
  const response = await get(`actions/workflows/ci.yml/runs?event=push&branch=master&head_sha=${sha}&per_page=100`);
  run = validatedRun(response.workflow_runs, sha);
  if (run) break;
  if (Date.now() >= deadline) throw new Error(`Timed out waiting for successful master CI (CI required) on ${sha}`);
  console.log(`Waiting for master CI / CI required on ${sha}; missing or pending validation cannot pass.`);
  await new Promise(resolve => setTimeout(resolve, 30_000));
}
const jobs = [];
for (let page = 1; ; page++) {
  const response = await get(`actions/runs/${run.id}/attempts/${run.run_attempt}/jobs?per_page=100&page=${page}`);
  jobs.push(...response.jobs);
  if (response.jobs.length < 100) break;
}
requireAggregate(jobs);
console.log(`Validated master commit ${sha} using ${run.html_url}`);
appendFileSync(process.env.GITHUB_OUTPUT, Object.entries({ ...policy, commit_sha: sha,
  build_timestamp: new Date().toISOString(), validation_run: run.html_url })
  .map(([key, value]) => `${key}=${value}\n`).join(''));
