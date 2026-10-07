import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import { barrierStep, schedulingMode, compareScheduling } from './scheduling-policy.mjs';

mkdirSync('artifacts/ci-timings', { recursive: true });
const args = process.argv.slice(2), compare = args[0] === '--compare';
const ids = compare ? args.slice(1) : args;
if (compare) assert.equal(ids.length, 2, 'Provide before and after run IDs');
const reports = [];
for (const id of ids) {
  assert.match(id, /^\d+$/);
  const pages = JSON.parse(execFileSync('gh', ['api', '--paginate', '--slurp', `repos/nickna/Conduit/actions/runs/${id}/jobs`],
    { encoding: 'utf8', timeout: 60_000 }));
  const jobs = pages.flatMap(page => page.jobs);
  const metadata = JSON.parse(execFileSync('gh', ['api', `repos/nickna/Conduit/actions/runs/${id}`],
    { encoding: 'utf8', timeout: 60_000 }));
  assert.ok(jobs.length && jobs.every(job => job.status === 'completed'), 'Measure completed runs only');
  const seconds = job => (Date.parse(job.completed_at) - Date.parse(job.started_at)) / 1000;
  const report = { run: id, url: `https://github.com/nickna/Conduit/actions/runs/${id}`,
    sha: metadata.head_sha, event: metadata.event,
    mode: jobs.some(job => job.steps?.some(step => step.name === barrierStep)) ? schedulingMode(jobs) : 'legacy',
    wallSeconds: (Math.max(...jobs.map(job => Date.parse(job.completed_at))) - Math.min(...jobs.map(job => Date.parse(job.started_at)))) / 1000,
    runnerMinutes: Math.round(jobs.reduce((sum, job) => sum + seconds(job), 0) / 60 * 100) / 100,
    jobs: jobs.map(job => ({ name: job.name, conclusion: job.conclusion, seconds: seconds(job) })) };
  writeFileSync(`artifacts/ci-timings/${id}.json`, JSON.stringify(report, null, 2));
  reports.push(report);
  console.log(JSON.stringify(report));
}
if (compare) {
  const comparison = compareScheduling(...reports);
  writeFileSync('artifacts/ci-timings/paired-comparison.json', JSON.stringify(comparison, null, 2));
  console.log(JSON.stringify(comparison));
}
