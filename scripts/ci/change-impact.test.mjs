import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { classifyPath, comparisonFromEvent, createPlan, parseChangedFiles, planFromEvent, preparePlanOutput, readChangedFiles, validateInventory, validatePlan } from './change-impact.mjs';

const inventory = JSON.parse(readFileSync(new URL('./required-jobs.json', import.meta.url), 'utf8'));
const base = 'a'.repeat(40), head = 'b'.repeat(40);
const plan = files => createPlan({ event: 'pull_request', base, head, files }, inventory);
const selected = result => inventory.filter(id => result.jobs[id]).sort();

test('documentation-only PRs retain the selector, CI guards and fresh dependency audits', () => {
  const result = plan(['docs/operations/ci-validation.md', 'README.md', 'AGENTS.md']);
  assert.equal(result.mode, 'docs');
  assert.deepEqual(selected(result), ['dependencies', 'select', 'source-ignore-guard']);
  assert.deepEqual(result.codeqlLanguages, []);
});

test('frontend PRs retain complete frontend coverage, JS security and production cross-service tests', () => {
  const result = plan(['WebAdmin/src/app/settings/page.tsx', 'WebAdmin/package-lock.json', 'docs/api.md']);
  assert.equal(result.mode, 'frontend');
  assert.deepEqual(selected(result), ['codeql', 'dependencies', 'docker', 'select', 'source-ignore-guard', 'webadmin']);
  assert.deepEqual(result.codeqlLanguages, ['javascript']);
});

for (const path of [
  'Services/ConduitLLM.Admin/Endpoints/PricingEndpoints.cs',
  'Services/ConduitLLM.Gateway/Program.cs', 'Shared/ConduitLLM.Core/OpenApi/Contract.cs',
  'Shared/ConduitLLM.Configuration/Migrations/NewMigration.cs', 'Tests/ConduitLLM.Tests/TestBase.cs',
  'Tests/ConduitLLM.IntegrationTests/Tests/FunctionProviderRetryTransportTests.cs',
  'Shared/ConduitLLM.Core/Extensions/SharedHttpClientExtensions.cs',
  'Shared/ConduitLLM.Functions/Providers/FunctionClientBase.cs',
  'Shared/ConduitLLM.Functions/Providers/Exa/ExaClient.Search.cs',
  'Shared/ConduitLLM.Functions/Providers/Tavily/TavilyClient.Search.cs',
  'Tests/Directory.Build.props', 'scripts/db/providers/provider-config.json',
  'scripts/ci/change-impact.mjs', 'scripts/ci/required-jobs.json', 'scripts/ci/coverage-baseline.json',
  'tools/openapi/package-lock.json', 'tools/ci/browser-smoke.mjs', 'tools/ConduitLLM.HealthProbe/Program.cs',
  '.github/workflows/ci.yml', '.github/actions/run-tests/action.yml', '.dockerignore', 'NuGet.Config',
  'Directory.Build.props', 'Directory.Packages.props', 'global.json', 'Conduit.slnx',
  '.config/dotnet-tools.json', 'WebAdmin/src/generated/admin-api.ts', 'WebAdmin/src/generated/gateway-api.ts',
  'docs/example.cs', 'docs/scripts/build.mjs', 'unknown/new-component.txt',
]) {
  test(`${path} conservatively selects full validation`, () => {
    const result = plan([path, 'WebAdmin/src/component.tsx']);
    assert.equal(result.mode, 'full');
    assert.deepEqual(selected(result), [...inventory].sort());
    assert.deepEqual(result.codeqlLanguages, ['csharp', 'javascript']);
    assert.doesNotThrow(() => validatePlan(inventory, result, 'pull_request'));
  });
}

for (const event of ['push', 'workflow_dispatch', 'schedule', 'merge_group', 'unknown']) {
  test(`${event} always requires full validation even with frontend-only files`, () => {
    const result = createPlan({ event, files: ['WebAdmin/src/page.tsx'] }, inventory);
    assert.equal(result.mode, 'full');
    assert.deepEqual(selected(result), [...inventory].sort());
  });
}

test('empty or unavailable PR comparisons select full validation', () => {
  assert.equal(plan([]).mode, 'full');
  assert.equal(createPlan({ event: 'pull_request', files: ['README.md'] }, inventory).mode, 'full');
  assert.equal(planFromEvent('pull_request', {}, inventory).mode, 'full');
  assert.throws(() => comparisonFromEvent({ pull_request: { base: { sha: '--help' }, head: { sha: head } } }), /revision/);
  assert.throws(() => comparisonFromEvent({ pull_request: { base: { sha: [base] }, head: { sha: head } } }), /revision/);
});

test('all known job decisions and CodeQL languages are validated', () => {
  assert.doesNotThrow(() => validatePlan(inventory, plan(['WebAdmin/package.json']), 'pull_request', { base, head }));
  for (const mutate of [
    p => delete p.jobs.validate, p => p.jobs.unowned = false,
    p => p.jobs.dependencies = false, p => p.jobs.validate = 'false',
    p => p.jobs.webadmin = false, p => p.codeqlLanguages = [],
    p => p.version = 2, p => p.mode = 'other', p => p.files = ['Services/ConduitLLM.Admin/Program.cs'],
    p => p.files = [], p => p.base = null, p => p.base = [base], p => p.head = [head], p => p.event = 'push', p => p.reasons = [],
  ]) {
    const result = plan(['WebAdmin/package.json']);
    mutate(result);
    assert.throws(() => validatePlan(inventory, result, 'pull_request'));
  }
  assert.throws(() => validatePlan(inventory, plan(['README.md']), 'push'), /event/);
  assert.throws(() => validatePlan(inventory, plan(['README.md']), 'pull_request', { base, head: base }), /revisions/);
  assert.throws(() => validateInventory([...inventory, 'new-test-job']), /ownership/);
  assert.throws(() => validateInventory([...inventory, inventory[0]]), /inventory/);
});

test('NUL diff parsing includes deleted and both renamed paths, preserving unusual names', () => {
  const paths = parseChangedFiles('D\0WebAdmin/old.tsx\0R100\0Services/old.cs\0WebAdmin/new.tsx\0A\0WebAdmin/src/space ü\nname.tsx\0');
  assert.deepEqual(paths, ['Services/old.cs', 'WebAdmin/new.tsx', 'WebAdmin/old.tsx', 'WebAdmin/src/space ü\nname.tsx']);
  assert.equal(plan(paths).mode, 'full');
  assert.equal(plan(parseChangedFiles('R100\0WebAdmin/src/old.ts\0WebAdmin/src/new.ts\0')).mode, 'frontend');
  assert.deepEqual(parseChangedFiles(''), []);
  for (const invalid of ['M\0WebAdmin/file.ts', 'R100\0WebAdmin/file.ts\0', 'U\0WebAdmin/file.ts\0', 'M\0../WebAdmin/file.ts\0']) {
    assert.throws(() => parseChangedFiles(invalid));
  }
});

test('ambiguous or traversal paths cannot select a reduced plan', () => {
  for (const path of ['/WebAdmin/file.ts', 'WebAdmin/../Services/file.cs', 'WebAdmin\\file.ts', 'WebAdmin//file.ts', 'C:/WebAdmin/file.ts']) {
    assert.equal(classifyPath(path), 'unknown');
    assert.equal(plan([path]).mode, 'full');
  }
});

test('large frontend diffs are processed completely rather than using a truncated API page', () => {
  const files = Array.from({ length: 3100 }, (_, i) => `WebAdmin/src/component${i}.tsx`);
  files.push('Services/ConduitLLM.Gateway/Program.cs');
  assert.equal(plan(files).mode, 'full');
  assert.equal(plan(files).files.length, 3101);
});

test('oversized multibyte or escaped plans fall back to full with complete artifact evidence', () => {
  for (const suffix of ['ü'.repeat(150), 'name\n"quoted"'.repeat(30)]) {
    const files = Array.from({ length: 500 }, (_, i) => `WebAdmin/src/${i}-${suffix}.tsx`);
    const original = plan(files);
    assert.equal(original.mode, 'frontend');
    const output = preparePlanOutput(original, inventory);
    assert.equal(output.plan.mode, 'full');
    assert.deepEqual(output.plan.files, []);
    assert.deepEqual(output.evidence.files, original.files);
    assert.equal(output.evidence.mode, 'full');
    assert.ok(Buffer.byteLength(JSON.stringify({ plan: JSON.stringify(output.plan) }), 'utf8') < 32 * 1024);
    assert.deepEqual(output.plan.jobs, output.evidence.jobs);
    const mixed = preparePlanOutput(plan([...files, 'Services/ConduitLLM.Gateway/Program.cs']), inventory);
    assert.equal(mixed.plan.mode, 'full');
    assert.ok(mixed.evidence.files.includes('Services/ConduitLLM.Gateway/Program.cs'));
    assert.ok(Object.values(mixed.plan.jobs).every(value => value === true));
  }
  const small = plan(['WebAdmin/package.json']);
  assert.deepEqual(preparePlanOutput(small, inventory), { plan: small, evidence: small });
});

test('Git comparison uses the cumulative PR merge-base diff, including rename sources', () => {
  const directory = mkdtempSync(join(tmpdir(), 'conduit-ci-impact-'));
  const git = (...args) => execFileSync('git', args, { cwd: directory, encoding: 'utf8', timeout: 10_000, stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const commit = () => { git('add', '--all'); git('-c', 'user.name=CI Fixture', '-c', 'user.email=ci@example.invalid', 'commit', '-m', 'fixture'); return git('rev-parse', 'HEAD'); };
  try {
    git('init', '--initial-branch=main');
    mkdirSync(join(directory, 'Services'), { recursive: true });
    mkdirSync(join(directory, 'WebAdmin/src'), { recursive: true });
    writeFileSync(join(directory, 'Services/Service.cs'), 'service\n');
    writeFileSync(join(directory, 'README.md'), 'base\n');
    const ancestor = commit();
    git('checkout', '-b', 'pr');
    renameSync(join(directory, 'Services/Service.cs'), join(directory, 'WebAdmin/src/Service.cs'));
    commit();
    writeFileSync(join(directory, 'README.md'), 'PR documentation\n');
    const prHead = commit();
    git('checkout', 'main');
    writeFileSync(join(directory, 'BaseBranchOnly.cs'), 'base branch change\n');
    const prBase = commit();
    const files = readChangedFiles(directory, prBase, prHead);
    assert.deepEqual(files, ['README.md', 'Services/Service.cs', 'WebAdmin/src/Service.cs']);
    const payload = { pull_request: { base: { sha: prBase }, head: { sha: prHead } } };
    assert.equal(planFromEvent('pull_request', payload, inventory, directory).mode, 'full');
    assert.equal(planFromEvent('pull_request', { pull_request: { base: { sha: 'c'.repeat(40) }, head: { sha: prHead } } }, inventory, directory).mode, 'full');
    assert.equal(readChangedFiles(directory, ancestor, prHead).includes('Services/Service.cs'), true);

    // Exercise actual Actions file commands and the aggregate CLI together on a
    // frontend-only PR, including a failed selected job and a stale plan.
    mkdirSync(join(directory, 'WebAdmin/src'), { recursive: true });
    writeFileSync(join(directory, 'WebAdmin/src/Page.tsx'), 'frontend\n');
    const frontendHead = commit();
    const eventPath = join(directory, 'event.json');
    writeFileSync(eventPath, JSON.stringify({ pull_request: { base: { sha: prBase }, head: { sha: frontendHead } } }));
    const outputPath = join(directory, 'output.txt');
    const environment = { ...process.env, GITHUB_EVENT_NAME: 'pull_request', GITHUB_EVENT_PATH: eventPath,
      GITHUB_OUTPUT: outputPath, GITHUB_STEP_SUMMARY: join(directory, 'summary.md') };
    execFileSync(process.execPath, [fileURLToPath(new URL('./change-impact.mjs', import.meta.url))],
      { cwd: directory, env: environment, timeout: 10_000, stdio: ['ignore', 'pipe', 'pipe'] });
    const output = JSON.parse(readFileSync(outputPath, 'utf8').trim().slice('plan='.length));
    assert.equal(output.mode, 'frontend');
    assert.deepEqual(output.files, ['WebAdmin/src/Page.tsx']);
    assert.deepEqual(JSON.parse(readFileSync(join(directory, 'artifacts/ci-selection/plan.json'), 'utf8')), output);
    const needs = Object.fromEntries(inventory.map(id => [id, { result: output.jobs[id] ? 'success' : 'skipped' }]));
    const requiredCommand = fileURLToPath(new URL('./required-check.mjs', import.meta.url));
    const aggregate = selection => execFileSync(process.execPath, [requiredCommand],
      { cwd: directory, env: { ...environment, CI_PLAN: JSON.stringify(selection), CI_NEEDS: JSON.stringify(needs) },
        timeout: 10_000, stdio: ['ignore', 'pipe', 'pipe'] });
    assert.match(aggregate(output).toString(), /All selected validation jobs succeeded/);
    needs.webadmin.result = 'skipped';
    assert.throws(() => aggregate(output), /webadmin: skipped/);
    needs.webadmin.result = 'success';
    assert.throws(() => aggregate({ ...output, head: prHead }), /current PR revisions/);
  } finally {
    // Only remove the uniquely created test checkout inside the OS temp root.
    assert.match(relative(tmpdir(), directory), /^conduit-ci-impact-[^\\/]+$/);
    rmSync(directory, { recursive: true, force: true });
  }
});
