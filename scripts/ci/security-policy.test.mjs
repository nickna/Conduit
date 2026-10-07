import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { npmFindings, nugetFindings, imageFindings, enforce } from './security-policy.mjs';
const vulnerable = { scope: 'WebAdmin', package: 'fixture', version: '1.0.0', advisory: 'GHSA-fixture', severity: 'high' };
const approval = { ...vulnerable, owner: '@nickna', reason: 'Fixture approval only', expires: '2027-03-01',
  approval: 'https://github.com/nickna/Conduit/issues/1484' };

test('exception expiry must be a real calendar date independently of its valid approval URL', () => {
  for (const expires of ['2027-02-31', '2027-02-29', '2100-02-29', '2028-02-30', '2027-04-31',
    '2027-00-15', '2027-13-01', '2027-01-00', '2027-01-32', '2027-2-01', '2027-02-01T00:00:00Z'])
    assert.throws(() => enforce([vulnerable], [{ ...approval, expires }], 'dependency', new Date('2026-10-07')), /exception/, expires);
  for (const expires of ['2027-02-28', '2028-02-29', '2000-02-29'])
    assert.equal(enforce([vulnerable], [{ ...approval, expires }], 'dependency', new Date('1999-01-01')).exceptions, 1, expires);
});

test('expiry includes the entire UTC calendar day and closes at the next midnight', () => {
  assert.equal(enforce([vulnerable], [approval], 'dependency', new Date('2027-03-01T23:59:59.999Z')).exceptions, 1);
  assert.throws(() => enforce([], [approval], 'dependency', new Date('2027-03-02T00:00:00.000Z')), /exception/);
  for (const now of [new Date('invalid'), null, '2026-10-07'])
    assert.throws(() => enforce([], [approval], 'dependency', now), /evaluation time/);
});

test('approval URLs require exact repository issue or PR paths and supported comment anchors', () => {
  const root = 'https://github.com/nickna/Conduit';
  for (const path of ['/issues/1484', '/pull/1484', '/issues/1484#issuecomment-6030023231',
    '/pull/1484#issuecomment-6030023231', '/pull/1484#discussion_r123', '/pull/1484#pullrequestreview-123']) {
    assert.equal(enforce([vulnerable], [{ ...approval, approval: root + path }], 'dependency', new Date('2026-10-07')).exceptions, 1, path);
  }
  for (const url of [root + '/issues/1467.evil', root + '/issues/1467/suffix', root + '/pull/1484evil',
    root + '/issues/1484/', root + '/issues/0', root + '/issues/01484', root + '/issues/1484?approval=true',
    root + '/issues/1484#', root + '/issues/1484#arbitrary', root + '/issues/1484#issuecomment-0',
    root + '/issues/1484#issuecomment-123junk', root + '/issues/1484#discussion_r123',
    root + '/issues/1484#pullrequestreview-123', root + '/issues/1484/../1484', root + '/issues/%31%34%38%34',
    'https://github.com.evil/nickna/Conduit/issues/1484', 'https://user@github.com/nickna/Conduit/issues/1484',
    'https://github.com:443/nickna/Conduit/issues/1484', 'https://github.com/other/Conduit/issues/1484',
    'http://github.com/nickna/Conduit/issues/1484', root + '/issues/1484\n', ' ' + root + '/issues/1484', 'not a URL']) {
    assert.throws(() => enforce([vulnerable], [{ ...approval, approval: url }], 'dependency', new Date('2026-10-07')), /exception/, url);
  }
});
test('a deliberately vulnerable dependency blocks; moderate findings remain reported', () => {
  assert.throws(() => enforce([vulnerable], []), /blocked/);
  assert.equal(enforce([{ ...vulnerable, severity: 'moderate' }], []).checked, 1);
});
test('critical images block even without an available fix; dependencies and images have explicit severity policies', () => {
  assert.throws(() => enforce([{ ...vulnerable, severity: 'critical' }], [], 'image'), /blocked/);
  assert.equal(enforce([vulnerable], [], 'image').checked, 1);
});
test('exceptions apply only to the exact identity and expire closed', () => {
  const exception = { ...vulnerable, owner: '@nickna', reason: 'Fixture approval only', expires: '2026-10-31', approval: 'https://github.com/nickna/Conduit/issues/1443' };
  assert.equal(enforce([vulnerable], [exception], 'dependency', new Date('2026-10-07')).exceptions, 1);
  assert.throws(() => enforce([{ ...vulnerable, version: '1.0.1' }], [exception], 'dependency', new Date('2026-10-07')), /blocked/);
  assert.throws(() => enforce([], [exception], 'dependency', new Date('2026-11-01')), /exception/);
  for (const field of ['owner', 'reason', 'approval', 'expires'])
    assert.throws(() => enforce([], [{ ...exception, [field]: '' }]), /exception/);
});

test('development-only exceptions cannot suppress the same identity in production dependencies', () => {
  const exception = { ...vulnerable, developmentOnly: true, owner: '@nickna', reason: 'Development tooling only',
    expires: '2026-10-31', approval: 'https://github.com/nickna/Conduit/issues/1467' };
  const now = new Date('2026-10-07');
  const development = { ...vulnerable, developmentOnly: true };
  assert.equal(enforce([development], [exception], 'dependency', now).exceptions, 1);
  for (const production of [vulnerable, { ...vulnerable, developmentOnly: false }])
    assert.throws(() => enforce([production], [exception], 'dependency', now), /blocked/);
  assert.throws(() => enforce([development, { ...development, developmentOnly: false }], [exception], 'dependency', now), /blocked/);
  for (const developmentOnly of ['true', 1, null, undefined])
    assert.throws(() => enforce([], [{ ...exception, developmentOnly }], 'dependency', now), /exception/);
});

test('npm development-only status requires an explicit lockfile dev flag for each affected node', () => {
  const nodes = ['node_modules/fixture', 'node_modules/parent/node_modules/fixture'];
  const audit = { auditReportVersion: 2, vulnerabilities: { fixture: { name: 'fixture', nodes,
    via: [{ url: 'GHSA-fixture', severity: 'high' }] } } };
  const lock = { packages: { [nodes[0]]: { version: '1.0.0', dev: true }, [nodes[1]]: { version: '1.0.0' } } };
  const findings = npmFindings(audit, lock, 'WebAdmin');
  assert.deepEqual(findings.map(finding => finding.developmentOnly), [true, false]);
});

test('the recorded braces approval suppresses only its authorized development identity until expiry', () => {
  const exceptions = JSON.parse(readFileSync(new URL('./security-exceptions.json', import.meta.url), 'utf8'));
  const approved = { scope: 'WebAdmin', package: 'braces', version: '3.0.3',
    advisory: 'https://github.com/advisories/GHSA-vfj7-8cjw-p6xm', severity: 'high', developmentOnly: true };
  const now = new Date('2026-10-07');
  assert.equal(enforce([approved], exceptions, 'dependency', now).exceptions, 1);
  for (const change of [{ scope: 'tools/ci' }, { package: 'other' }, { version: '3.0.4' },
    { advisory: 'https://github.com/advisories/GHSA-other' }, { developmentOnly: false }])
    assert.throws(() => enforce([{ ...approved, ...change }], exceptions, 'dependency', now), /blocked/);
  assert.throws(() => enforce([{ ...approved, scope: 'release-image', severity: 'critical' }], exceptions, 'image', now), /blocked/);
  assert.throws(() => enforce([], exceptions, 'dependency', new Date('2026-11-07')), /exception/);
});
test('malformed or unavailable scans never become clean reports', () => {
  assert.throws(() => npmFindings({ error: {} }, {}, 'fixture'));
  assert.throws(() => nugetFindings({ projects: [] }));
  assert.throws(() => imageFindings({ ArtifactName: 'image:mutable', Results: [] }));
});
test('real scanner shapes preserve vulnerable dependency identities', () => {
  const npm = npmFindings({ auditReportVersion: 2, vulnerabilities: { fixture: { name: 'fixture', nodes: ['node_modules/fixture'],
    via: [{ url: 'GHSA-fixture', severity: 'high' }] } } }, { packages: { 'node_modules/fixture': { version: '1.0.0' } } }, 'WebAdmin');
  assert.deepEqual(npm[0], { ...vulnerable, developmentOnly: false });
  const nuget = nugetFindings({ version: 1, projects: [{ frameworks: [{ transitivePackages: [{ id: 'SSH.NET', resolvedVersion: '2023.0.0',
    vulnerabilities: [{ severity: 'High', advisoryurl: 'https://github.com/advisories/GHSA-q939-rpr3-3284' }] }] }] }] });
  assert.throws(() => enforce(nuget, []), error => error.message.includes('"package":"SSH.NET"'));
  const image = imageFindings({ ArtifactName: `image@sha256:${'a'.repeat(64)}`, Results: [{ Vulnerabilities: [{ PkgName: 'fixture',
    InstalledVersion: '1.0.0', VulnerabilityID: 'CVE-fixture', Severity: 'CRITICAL' }] }] }, 'release-image');
  assert.throws(() => enforce(image, [], 'image'), /CVE-fixture/);
});
