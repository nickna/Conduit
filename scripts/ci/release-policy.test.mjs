import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseTag, validatedRun, requireAggregate } from './release-policy.mjs';

test('stable and prerelease channels', () => {
  assert.deepEqual(parseTag('v3.1.0'), { version: '3.1.0', channel: 'latest', prerelease: false });
  assert.equal(parseTag('v3.1.0-beta.1').channel, 'beta');
});
for (const tag of ['vfoo', 'v01.2.3', 'v1.2', 'v1.2.3-', 'v1.2.3-01', 'v1.2.3+build', 'v1.2.3\nmalicious=value']) {
  test(`rejects invalid or unsupported tag ${JSON.stringify(tag)}`, () => assert.throws(() => parseTag(tag)));
}
const successful = { id: 1, run_attempt: 1, head_sha: 'abc', head_branch: 'master', event: 'push', status: 'completed', conclusion: 'success' };
test('only exact master push validation is accepted', () => {
  assert.equal(validatedRun([successful], 'abc'), successful);
  assert.equal(validatedRun([successful], 'other'), null);
  assert.equal(validatedRun([{ ...successful, event: 'pull_request' }], 'abc'), null);
  assert.equal(validatedRun([{ ...successful, head_branch: 'dev' }], 'abc'), null);
});
test('missing and pending checks wait; failed/cancelled/skipped checks fail', () => {
  assert.equal(validatedRun([], 'abc'), null);
  assert.equal(validatedRun([{ ...successful, status: 'in_progress' }], 'abc'), null);
  for (const conclusion of ['failure', 'cancelled', 'skipped', 'timed_out', null]) {
    assert.throws(() => validatedRun([{ ...successful, conclusion }], 'abc'));
  }
});
test('an older success cannot hide a more recent unsuccessful run', () => {
  assert.throws(() => validatedRun([successful, { ...successful, id: 2, conclusion: 'failure' }], 'abc'));
});
test('a completed workflow must include the successful aggregate', () => {
  requireAggregate([{ name: 'CI required', conclusion: 'success' }]);
  assert.throws(() => requireAggregate([]));
  assert.throws(() => requireAggregate([{ name: 'CI required', conclusion: 'skipped' }]));
});
