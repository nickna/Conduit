import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { warningKeys, enforceWarnings } from '../../tools/openapi/redocly-warning-policy.mjs';
const warning = { ruleId: 'operation-summary', severity: 'warn', message: 'Missing summary',
  location: [{ source: { ref: '/contract/api.json' }, pointer: '#/paths/~1test/get' }] };
const report = { totals: { errors: 0, warnings: 1 }, problems: [warning] };
test('a newly introduced Redocly warning fails even when the CLI reports zero errors', () => {
  const keys = warningKeys([report]); assert.throws(() => enforceWarnings(keys), /warnings are not allowed/);
  assert.deepEqual(enforceWarnings([]), { warnings: 0 });
  const changed = warningKeys([{ ...report, problems: [{ ...warning, ruleId: 'new-warning-type' }] }]);
  assert.throws(() => enforceWarnings(changed), /warnings are not allowed/);
});

test('malformed warning totals, severities and locations fail closed', () => {
  for (const totals of [{}, { errors: -1, warnings: 0 }, { errors: 0, warnings: -1 },
    { errors: 0, warnings: '1' }, { errors: 0, warnings: 1.5 }])
    assert.throws(() => warningKeys([{ ...report, totals }]), /malformed/);
  for (const problem of [{ ...warning, severity: 'unexpected' },
    { ...warning, message: '' }, { ...warning, location: [{ source: {}, pointer: '#/test' }] },
    { ...warning, location: [{ source: { ref: 'api.json' } }] }])
    assert.throws(() => warningKeys([{ ...report, problems: [problem] }]));
});

test('the only lint exception is the compatibility endpoint that always returns 501', () => {
  const ignore = readFileSync(new URL('../../.redocly.lint-ignore.yaml', import.meta.url), 'utf8')
    .split(/\r?\n/).map(line => line.trim()).filter(line => line && !line.startsWith('#'));
  assert.deepEqual(ignore, [
    'Services/ConduitLLM.Admin/openapi-admin.json:',
    'operation-2xx-response:',
    "- '#/paths/~1v1~1admin~1media-retention-policies~1{groupId}~1cleanup-jobs/post/responses'",
  ]);
  const contract = JSON.parse(readFileSync(new URL('../../Services/ConduitLLM.Admin/openapi-admin.json', import.meta.url)));
  const operation = contract.paths['/v1/admin/media-retention-policies/{groupId}/cleanup-jobs'].post;
  assert(operation.responses['501']);
  assert(!Object.keys(operation.responses).some(status => /^2\d\d$/.test(status)));
  assert.match(operation.description, /not implemented/);
});
test('warning identities normalize Windows paths and errors never pass as debt', () => {
  assert.deepEqual(warningKeys([report]), warningKeys([{ ...report, problems: [{ ...warning,
    location: [{ source: { ref: 'C:\\contract\\api.json' }, pointer: '#/paths/~1test/get' }] }] }]));
  assert.throws(() => warningKeys([{ ...report, totals: { errors: 1 } }]));
  assert.throws(() => warningKeys([{}]));
  assert.throws(() => warningKeys([{ ...report, totals: { errors: 0, warnings: 100 } }]), /Truncated/);
});

test('semantic debt is limited to the two established task cleanup field names', () => {
  const allowlist = JSON.parse(readFileSync(new URL('../../tools/openapi/openapi-ratchet-allowlist.json', import.meta.url)));
  assert.deepEqual(allowlist.rules, {
    'generic-success-schema': [],
    'unsupported-media-type': [],
    'missing-4xx-response': [],
    'property-casing': [
      'admin|#/components/schemas/TaskCleanupResponseDto/properties/cleaned_up',
      'admin|#/components/schemas/TaskCleanupResponseDto/properties/older_than_hours',
    ],
    'admin-route-style': [],
    'operation-summary': [],
  });
});
