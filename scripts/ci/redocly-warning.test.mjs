import { test } from 'node:test';
import assert from 'node:assert/strict';
import { warningKeys, enforceWarnings } from '../../tools/openapi/redocly-warning-policy.mjs';
const warning = { ruleId: 'operation-summary', severity: 'warn', message: 'Missing summary',
  location: [{ source: { ref: '/contract/api.json' }, pointer: '#/paths/~1test/get' }] };
const report = { totals: { errors: 0, warnings: 1 }, problems: [warning] };
test('a newly introduced Redocly warning fails even when the CLI reports zero errors', () => {
  const keys = warningKeys([report]); assert.throws(() => enforceWarnings(keys, []), /New Redocly/);
  assert.equal(enforceWarnings(keys, keys).knownWarnings, 1);
  const changed = warningKeys([{ ...report, problems: [{ ...warning, ruleId: 'new-warning-type' }] }]);
  assert.throws(() => enforceWarnings(changed, keys), /New Redocly/);
});
test('warning identities normalize Windows paths and errors never pass as debt', () => {
  assert.deepEqual(warningKeys([report]), warningKeys([{ ...report, problems: [{ ...warning,
    location: [{ source: { ref: 'C:\\contract\\api.json' }, pointer: '#/paths/~1test/get' }] }] }]));
  assert.throws(() => warningKeys([{ ...report, totals: { errors: 1 } }]));
  assert.throws(() => warningKeys([{}]));
  assert.throws(() => warningKeys([{ ...report, totals: { errors: 0, warnings: 100 } }]), /Truncated/);
});
