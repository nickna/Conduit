import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { parseWorkflow, validateWorkflows } from './workflow-policy.mjs';
const inventory = JSON.parse(readFileSync('scripts/ci/required-jobs.json', 'utf8'));
function workflows() { return Object.fromEntries(readdirSync('.github/workflows').filter(f => /\.ya?ml$/.test(f))
  .map(f => [f, parseWorkflow(readFileSync(`.github/workflows/${f}`, 'utf8'))])); }
test('real workflows and cancellable validation jobs satisfy the policy without release warnings', () => {
  assert.equal(validateWorkflows(workflows(), inventory).requiredJobs, inventory.length);
});
for (const [name, mutate] of [
  ['missing required job', w => w['ci.yml'].jobs.required.needs.pop()],
  ['docs-only path filtering', w => w['ci.yml'].on.pull_request['paths-ignore'] = ['docs/**']],
  ['skipped aggregate', w => w['ci.yml'].jobs.required.if = '${{ success() }}'],
  ['default packaged checkout credentials', w => delete w['ci.yml'].jobs.docker.steps[0].with],
  ['persisted packaged checkout credentials', w => w['ci.yml'].jobs.docker.steps[0].with['persist-credentials'] = true],
  ['default timing experiment', w => w['ci.yml'].on.workflow_dispatch.inputs.measure_previous_scheduling.default = true],
  ['PR timing experiment', w => w['ci.yml'].jobs['distributed-lock'].with.repeat_policy_tests = '${{ true }}'],
  ['per-tag production lock', w => w['release.yml'].jobs.production.concurrency.group = '${{ github.ref }}'],
  ['pending release replacement', w => delete w['release.yml'].jobs.production.concurrency.queue],
  ['invalid queue value', w => w['ci.yml'].concurrency.queue = 'unlimited'],
  ['queue cancellation conflict', w => w['ci.yml'].concurrency.queue = 'max'],
  ['invalid job queue value', w => w['ci.yml'].jobs.webadmin.concurrency = { group: 'fixture', queue: 'unlimited' }],
  ['job queue cancellation conflict', w => w['ci.yml'].jobs.webadmin.concurrency = { group: 'fixture', queue: 'max', 'cancel-in-progress': true }],
  ['workflow queue without explicit non-cancellation', w => { w['ci.yml'].concurrency.queue = 'max'; delete w['ci.yml'].concurrency['cancel-in-progress']; }],
  ['job queue without explicit non-cancellation', w => w['ci.yml'].jobs.webadmin.concurrency = { group: 'fixture', queue: 'max' }],
  ['non-string workflow queue', w => w['ci.yml'].concurrency.queue = true],
  ['non-string job queue', w => w['ci.yml'].jobs.webadmin.concurrency = { group: 'fixture', queue: 100 }],
  ['production bypass', w => w['release.yml'].jobs.production.if = '${{ always() }}'],
  ['unvalidated production', w => w['release.yml'].jobs.production.needs = ['metadata', 'docker']],
  ['missing scan', w => w['release.yml'].jobs.candidates.steps.splice(w['release.yml'].jobs.candidates.steps.findIndex(s => s.uses?.startsWith('aquasecurity')), 1)],
  ['reusable cancellation collision', w => w['migration-validation.yml'].concurrency.group = '${{ github.workflow }}-${{ github.ref }}']]) {
  test(`rejects ${name}`, () => { const w = workflows(); mutate(w); assert.throws(() => validateWorkflows(w, inventory)); });
}
test('malformed YAML and duplicate keys fail parsing', () => {
  assert.throws(() => parseWorkflow('jobs: [unterminated'));
  assert.throws(() => parseWorkflow('name: first\nname: second\n'));
});
const actionlintConfiguration = JSON.parse(readFileSync('scripts/ci/actionlint-config.json', 'utf8'));
const binary = process.env.ACTIONLINT ?? resolve(`artifacts/tools/actionlint/bin/actionlint${process.platform === 'win32' ? '.exe' : ''}`);
const base = 'name: Fixture\non: push\njobs:\n  proof:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo okay\n';
const queue = 'concurrency:\n  group: fixture\n  cancel-in-progress: false\n  queue: max\n';
function lint(source, compatibility = true) {
  const ignore = compatibility && actionlintConfiguration.queueCompatibilityDiagnostic
    ? ['-ignore', actionlintConfiguration.queueCompatibilityDiagnostic] : [];
  const result = spawnSync(binary, ['-shellcheck=', '-pyflakes=', ...ignore, '-stdin-filename', 'fixture.yml', '-'],
    { input: source, encoding: 'utf8', timeout: 10_000 });
  assert.ifError(result.error);
  assert.equal(result.signal, null, 'actionlint must finish normally');
  assert.ok([0, 1].includes(result.status), `Unexpected actionlint exit: ${result.status}\n${result.stderr}`);
  return { status: result.status, diagnostics: result.stdout + result.stderr };
}

test('actionlint executes successfully on a valid workflow before negative checks', () => {
  assert.equal(lint(base).status, 0);
});

test('the shared queue compatibility rule accepts workflow and job queues only while needed', () => {
  for (const queueValue of ['max', 'single']) {
    const concurrency = queue.replace('queue: max', `queue: ${queueValue}`);
    for (const source of [base.replace('jobs:\n', concurrency + 'jobs:\n'),
      base.replace('    runs-on:', concurrency.trimEnd().split('\n').map(line => `    ${line}`).join('\n') + '\n    runs-on:')]) {
      const unfiltered = lint(source, false);
      if (actionlintConfiguration.queueCompatibilityDiagnostic) {
        assert.equal(unfiltered.status, 1, 'Remove the queue compatibility rule when actionlint supports it');
        assert.match(unfiltered.diagnostics, /unexpected key "queue" for "concurrency" section/);
      } else assert.equal(unfiltered.status, 0);
      assert.equal(lint(source).status, 0);
    }
  }
});

test('queue compatibility never hides other syntax, expression, or job-reference diagnostics', () => {
  const queued = base.replace('jobs:\n', queue + 'jobs:\n');
  for (const [invalid, diagnostic] of [
    [queued.replace('queue: max', 'queue: max\n  typo: value'), /unexpected key "typo"/],
    [queued.replace('echo okay', '${{ unknown.value }}'), /\[expression\]/],
    [queued.replace('    runs-on:', '    needs: nonexistent\n    runs-on:'), /\[job-needs\]/]]) {
    const result = lint(invalid);
    assert.equal(result.status, 1);
    assert.match(result.diagnostics, diagnostic);
  }
});
