import { test } from 'node:test';
import assert from 'node:assert/strict';
import { npmFindings, nugetFindings, imageFindings, enforce } from './security-policy.mjs';
const vulnerable = { scope: 'WebAdmin', package: 'fixture', version: '1.0.0', advisory: 'GHSA-fixture', severity: 'high' };
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
test('malformed or unavailable scans never become clean reports', () => {
  assert.throws(() => npmFindings({ error: {} }, {}, 'fixture'));
  assert.throws(() => nugetFindings({ projects: [] }));
  assert.throws(() => imageFindings({ ArtifactName: 'image:mutable', Results: [] }));
});
test('real scanner shapes preserve vulnerable dependency identities', () => {
  const npm = npmFindings({ auditReportVersion: 2, vulnerabilities: { fixture: { name: 'fixture', nodes: ['node_modules/fixture'],
    via: [{ url: 'GHSA-fixture', severity: 'high' }] } } }, { packages: { 'node_modules/fixture': { version: '1.0.0' } } }, 'WebAdmin');
  assert.deepEqual(npm[0], vulnerable);
  const nuget = nugetFindings({ version: 1, projects: [{ frameworks: [{ transitivePackages: [{ id: 'SSH.NET', resolvedVersion: '2023.0.0',
    vulnerabilities: [{ severity: 'High', advisoryurl: 'https://github.com/advisories/GHSA-q939-rpr3-3284' }] }] }] }] });
  assert.throws(() => enforce(nuget, []), /SSH.NET/);
  const image = imageFindings({ ArtifactName: `image@sha256:${'a'.repeat(64)}`, Results: [{ Vulnerabilities: [{ PkgName: 'fixture',
    InstalledVersion: '1.0.0', VulnerabilityID: 'CVE-fixture', Severity: 'CRITICAL' }] }] }, 'release-image');
  assert.throws(() => enforce(image, [], 'image'), /CVE-fixture/);
});
