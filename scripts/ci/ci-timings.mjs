import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';

mkdirSync('artifacts/ci-timings', { recursive: true });
for (const id of process.argv.slice(2)) {
  assert.match(id, /^\d+$/);
  const pages = JSON.parse(execFileSync('gh', ['api', '--paginate', '--slurp', `repos/nickna/Conduit/actions/runs/${id}/jobs`],
    { encoding: 'utf8', timeout: 60_000 }));
  const jobs = pages.flatMap(page => page.jobs);
  assert.ok(jobs.length && jobs.every(job => job.status === 'completed'), 'Measure completed runs only');
  const seconds = job => (Date.parse(job.completed_at) - Date.parse(job.started_at)) / 1000;
  const report = { run: id, url: `https://github.com/nickna/Conduit/actions/runs/${id}`,
    wallSeconds: (Math.max(...jobs.map(job => Date.parse(job.completed_at))) - Math.min(...jobs.map(job => Date.parse(job.started_at)))) / 1000,
    runnerMinutes: Math.round(jobs.reduce((sum, job) => sum + seconds(job), 0) / 60 * 100) / 100,
    jobs: jobs.map(job => ({ name: job.name, conclusion: job.conclusion, seconds: seconds(job) })) };
  writeFileSync(`artifacts/ci-timings/${id}.json`, JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
}
