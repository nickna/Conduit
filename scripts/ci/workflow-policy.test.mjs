import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
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
  ['per-tag production lock', w => w['release.yml'].jobs.production.concurrency.group = '${{ github.ref }}'],
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
test('actionlint rejects invalid Actions expressions and undefined job references', () => {
  const binary = process.env.ACTIONLINT ?? resolve(`artifacts/tools/actionlint/bin/actionlint${process.platform === 'win32' ? '.exe' : ''}`);
  const base = 'name: Fixture\non: push\njobs:\n  proof:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo okay\n';
  for (const invalid of [base.replace('echo okay', '${{ unknown.value }}'), base.replace('    runs-on:', '    needs: nonexistent\n    runs-on:')])
    assert.throws(() => execFileSync(binary, ['-shellcheck=', '-pyflakes=', '-stdin-filename', 'fixture.yml', '-'], { input: invalid, stdio: ['pipe', 'pipe', 'pipe'] }));
});
