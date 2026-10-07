import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateJobs } from './required-check.mjs';

test('accepts only successful required jobs', () => {
  assert.doesNotThrow(() => validateJobs(['tests', 'images'], { tests: { result: 'success' }, images: { result: 'success' } }));
});
for (const result of ['failure', 'cancelled', 'skipped', 'pending', undefined]) {
  test(`rejects a ${result ?? 'missing'} required job`, () => {
    assert.throws(() => validateJobs(['tests'], result ? { tests: { result } } : {}), /tests:/);
  });
}
test('rejects an empty inventory', () => assert.throws(() => validateJobs([], {}), /empty/));
