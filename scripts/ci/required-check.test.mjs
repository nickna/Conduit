import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateJobs } from './required-check.mjs';
import { readFileSync } from 'node:fs';
import { createPlan } from './change-impact.mjs';

test('accepts only successful required jobs', () => {
  assert.doesNotThrow(() => validateJobs(['tests', 'images'], { tests: { result: 'success' }, images: { result: 'success' } }));
});
for (const result of ['failure', 'cancelled', 'skipped', 'pending', undefined]) {
  test(`rejects a ${result ?? 'missing'} required job`, () => {
    assert.throws(() => validateJobs(['tests'], result ? { tests: { result } } : {}), /tests:/);
  });
}
test('rejects an empty inventory', () => assert.throws(() => validateJobs([], {}), /empty/));

const inventory = JSON.parse(readFileSync(new URL('./required-jobs.json', import.meta.url), 'utf8'));
const plan = files => createPlan({ event: 'pull_request', base: 'a'.repeat(40), head: 'b'.repeat(40), files }, inventory);
const results = selection => Object.fromEntries(inventory.map(id => [id, { result: selection.jobs[id] ? 'success' : 'skipped' }]));

test('accepts explicitly excluded skipped jobs and successful selected jobs', () => {
  for (const files of [['README.md'], ['WebAdmin/package.json'], ['Services/Admin.cs']]) {
    const selection = plan(files);
    assert.doesNotThrow(() => validateJobs(inventory, results(selection), selection, 'pull_request'));
  }
});

test('successful extra validation is acceptable when the plan did not require it', () => {
  const selection = plan(['README.md']);
  const needs = results(selection);
  needs.validate.result = 'success';
  assert.doesNotThrow(() => validateJobs(inventory, needs, selection, 'pull_request'));
});

for (const result of ['failure', 'cancelled', 'skipped', 'pending', undefined]) {
  test(`a selected ${result ?? 'missing'} job still blocks selective CI`, () => {
    const selection = plan(['WebAdmin/package.json']);
    const needs = results(selection);
    if (result) needs.webadmin.result = result; else delete needs.webadmin;
    assert.throws(() => validateJobs(inventory, needs, selection, 'pull_request'), /webadmin:/);
  });
}

for (const result of ['failure', 'cancelled', 'pending', undefined]) {
  test(`an excluded ${result ?? 'missing'} job cannot be silently accepted`, () => {
    const selection = plan(['README.md']);
    const needs = results(selection);
    if (result) needs.validate.result = result; else delete needs.validate;
    assert.throws(() => validateJobs(inventory, needs, selection, 'pull_request'), /validate:/);
  });
}

test('selector failure and an invalid plan cannot turn missing validation into success', () => {
  const selection = plan(['README.md']);
  const needs = results(selection);
  needs.select.result = 'failure';
  assert.throws(() => validateJobs(inventory, needs, selection, 'pull_request'), /select:/);
  needs.select.result = 'success';
  selection.jobs.dependencies = false;
  assert.throws(() => validateJobs(inventory, needs, selection, 'pull_request'), /decision/);
  assert.throws(() => validateJobs(inventory, needs, null, 'pull_request'), /plan/);
});

test('a selective PR success cannot satisfy full push or manual validation', () => {
  const selection = plan(['WebAdmin/package.json']);
  for (const event of ['push', 'workflow_dispatch']) {
    assert.throws(() => validateJobs(inventory, results(selection), selection, event), /event/);
    assert.throws(() => validateJobs(inventory, results(selection), { ...selection, event }, event), /changed inputs/);
    const full = createPlan({ event }, inventory);
    const needs = results(full);
    needs.validate.result = 'skipped';
    assert.throws(() => validateJobs(inventory, needs, full, event), /validate:/);
  }
});
