import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateJest } from './jest-evidence.mjs';
test('Jest must retain the executed suite with no skipped correctness tests', () => {
  const passing = { numPassedTests: 461, numFailedTests: 0, numPendingTests: 0 };
  validateJest(passing);
  for (const invalid of [{ ...passing, numPassedTests: 0 }, { ...passing, numPassedTests: 460 },
    { ...passing, numFailedTests: 1 }, { ...passing, numPendingTests: 1 }]) {
    assert.throws(() => validateJest(invalid));
  }
});
