import { test } from 'node:test';
import assert from 'node:assert/strict';
import { predecessorJobs, predecessorsReady, schedulingMode, compareScheduling, barrierStep, repeatedStep } from './scheduling-policy.mjs';

test('measurement barrier waits for every predecessor and rejects failed or skipped proof', () => {
  const jobs = predecessorJobs.map(name => ({ name, status: 'completed', conclusion: 'success' }));
  assert.equal(predecessorsReady(jobs), true);
  assert.equal(predecessorsReady(jobs.slice(1)), false);
  assert.equal(predecessorsReady([{ ...jobs[0], status: 'in_progress' }, ...jobs.slice(1)]), false);
  for (const conclusion of ['failure', 'cancelled', 'skipped'])
    assert.throws(() => predecessorsReady([{ ...jobs[0], conclusion }, ...jobs.slice(1)]));
});

test('scheduling comparison requires completed controls in both PostgreSQL lanes', () => {
  const jobs = [{ name: 'Packaged application business flows', steps: [{ name: barrierStep, conclusion: 'success' }] },
    ...['16', '17'].map(version => ({ name: `Distributed lock validation / PostgreSQL ${version}`, steps: [{ name: repeatedStep, conclusion: 'success' }] }))];
  assert.equal(schedulingMode(jobs), 'previous-scheduling');
  jobs.forEach(job => job.steps[0].conclusion = 'skipped');
  assert.equal(schedulingMode(jobs), 'current-scheduling');
  jobs[0].steps[0].conclusion = 'success';
  assert.throws(() => schedulingMode(jobs));
});

test('timing comparison rejects different source, inventories and unsuccessful runs', () => {
  const run = { sha: 'same', event: 'workflow_dispatch', wallSeconds: 10, runnerMinutes: 2,
    jobs: [{ name: 'Packaged application business flows', seconds: 5, conclusion: 'success' }] };
  const before = { ...run, mode: 'previous-scheduling' }, after = { ...run, mode: 'current-scheduling' };
  assert.equal(compareScheduling(before, after).removedDuplicateCases, 466);
  assert.throws(() => compareScheduling(before, { ...after, sha: 'other' }));
  assert.throws(() => compareScheduling(before, { ...after, event: 'pull_request' }));
  assert.throws(() => compareScheduling(before, { ...after, jobs: [] }));
});
