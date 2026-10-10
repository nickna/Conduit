import assert from 'node:assert/strict';

export const predecessorJobs = ['Validate', 'WebAdmin correctness and coverage', 'Contracts and official SDK clients'];
export const barrierStep = 'Controlled previous scheduling barrier';
export const repeatedStep = 'Build repeated-policy selector';

export function predecessorsReady(jobs) {
  return predecessorJobs.map(name => {
    const matches = jobs.filter(job => job.name === name);
    assert.ok(matches.length <= 1, `Ambiguous predecessor: ${name}`);
    const job = matches[0];
    if (!job || job.status !== 'completed') return false;
    assert.equal(job.conclusion, 'success', `Measurement predecessor failed: ${name}`);
    return true;
  }).every(Boolean);
}

export function schedulingMode(jobs) {
  const packaged = jobs.find(job => job.name === 'Packaged application business flows');
  const matrix = jobs.filter(job => job.name.startsWith('Distributed lock validation / PostgreSQL '));
  const flags = [packaged?.steps?.find(step => step.name === barrierStep)?.conclusion,
    ...matrix.map(job => job.steps?.find(step => step.name === repeatedStep)?.conclusion)];
  assert.equal(matrix.length, 2, 'Require both PostgreSQL versions in the comparison');
  if (flags.every(flag => flag === 'success')) return 'previous-scheduling';
  if (flags.every(flag => flag === 'skipped')) return 'current-scheduling';
  throw new Error('Incomplete or mixed scheduling experiment');
}

export function compareScheduling(before, after) {
  assert.equal(before.mode, 'previous-scheduling');
  assert.equal(after.mode, 'current-scheduling');
  assert.equal(before.sha, after.sha, 'Compare identical source revisions');
  assert.ok([before, after].every(run => run.event === 'workflow_dispatch' && run.jobs.every(job => job.conclusion === 'success')),
    'Compare successful manual runs only');
  assert.deepEqual(before.jobs.map(job => job.name).sort(), after.jobs.map(job => job.name).sort(), 'Required proof inventory must match');
  const packagedSeconds = run => run.jobs.find(job => job.name === 'Packaged application business flows').seconds;
  return { sha: before.sha, before: before.url, after: after.url, removedDuplicateCases: 466,
    wallSeconds: { before: before.wallSeconds, after: after.wallSeconds },
    runnerMinutes: { before: before.runnerMinutes, after: after.runnerMinutes },
    packagedSeconds: { before: packagedSeconds(before), after: packagedSeconds(after) } };
}
