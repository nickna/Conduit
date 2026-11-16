import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { validateArchiveConfig, validateRegistryBinding, validateReport, validateOwned } from './packaged-image-evidence.mjs';
import { parseWorkflow } from './workflow-policy.mjs';

const digest = value => `sha256:${createHash('sha256').update(value).digest('hex')}`;
const bytes = Buffer.from(JSON.stringify({ os: 'linux', architecture: 'arm64', rootfs: { diff_ids: [digest('rootfs')] } }));
const configDigest = digest(bytes);
const image = { Id: configDigest, Os: 'linux', Architecture: 'arm64', RootFS: { Layers: [digest('rootfs')] } };
const outputs = { imageid: configDigest, metadata: JSON.stringify({ 'containerimage.config.digest': configDigest }) };

test('archive binding supports classic config IDs and containerd manifest IDs without conflating them', () => {
  for (const Id of [configDigest, digest('manifest')])
    assert.equal(validateArchiveConfig({ ...image, Id }, bytes, 'linux/arm64', outputs).configDigest, configDigest);
});
for (const [name, mutate] of [
  ['wrong native platform', (i, o) => i.Architecture = 'amd64'],
  ['different archive rootfs', (i, o) => i.RootFS.Layers = [digest('substituted')]],
  ['different Buildx config', (i, o) => o.metadata = JSON.stringify({ 'containerimage.config.digest': digest('other') })],
  ['different loaded image', (i, o) => o.imageid = digest('other')],
]) test(`archive binding rejects ${name}`, () => {
  const i = structuredClone(image), o = structuredClone(outputs); mutate(i, o);
  assert.throws(() => validateArchiveConfig(i, bytes, 'linux/arm64', o));
});

const manifest = Buffer.from(JSON.stringify({ schemaVersion: 2, config: { digest: configDigest }, layers: [{ digest: digest('layer') }] }));
test('registry binding verifies raw manifest and config bytes against the archived config', () => {
  assert.equal(validateRegistryBinding(manifest, digest(manifest), bytes, { configDigest }).config.digest, configDigest);
  assert.throws(() => validateRegistryBinding(manifest, digest('tampered'), bytes, { configDigest }));
  assert.throws(() => validateRegistryBinding(manifest, digest(manifest), Buffer.from('{}'), { configDigest }));
  assert.throws(() => validateRegistryBinding(manifest, digest(manifest), bytes, { configDigest: digest('other') }));
});

test('raw scanner reference and config identity must both match, without rewriting a report', () => {
  const binding = { configDigest, scanReference: `127.0.0.1:1234/ci/admin@${digest(manifest)}` };
  const report = { ArtifactName: binding.scanReference, Metadata: { ImageID: configDigest }, Results: [] };
  validateReport(report, binding);
  for (const bad of [ { ...report, ArtifactName: 'image.tar' }, { ...report, Metadata: { ImageID: digest('other') } },
    { ...report, ArtifactName: binding.scanReference.replace('1234', '4321') }, { ...report, Results: undefined } ])
    assert.throws(() => validateReport(bad, binding));
});

test('cleanup requires exact ID, name, image and owner before removing a container', () => {
  const resource = { id: 'container-id', name: 'owned-registry', imageId: digest('registry') };
  const actual = { Id: resource.id, Name: `/${resource.name}`, Image: resource.imageId,
    Config: { Labels: { 'conduit.ci.image-evidence.owner': 'owner' } } };
  validateOwned(resource, actual, 'owner');
  for (const mutate of [a => a.Id = 'other', a => a.Name = '/user-registry', a => a.Image = digest('other'),
    a => a.Config.Labels['conduit.ci.image-evidence.owner'] = 'other']) {
    const bad = structuredClone(actual); mutate(bad); assert.throws(() => validateOwned(resource, bad, 'owner'));
  }
});

test('both native matrix legs export before tests, remotely scan all three images, enforce policy, and retain cleanup/archives on failure', () => {
  const job = parseWorkflow(readFileSync('.github/workflows/ci.yml', 'utf8')).jobs.docker;
  const steps = job.steps;
  const prepare = steps.findIndex(s => s.run === 'node scripts/ci/packaged-image-evidence.mjs prepare');
  const boot = steps.findIndex(s => s.run === 'node scripts/ci/container-smoke.mjs');
  assert.ok(prepare > 0 && prepare < boot);
  const scans = steps.filter(s => s.uses?.startsWith('aquasecurity/trivy-action@'));
  assert.equal(scans.length, 3);
  assert.deepEqual(scans.map(s => s.with['image-ref']), ['admin', 'http', 'webadmin'].map(s => `\${{ steps.image-evidence.outputs.${s} }}`));
  for (const scan of scans) {
    assert.equal(scan.uses, 'aquasecurity/trivy-action@ed142fd0673e97e23eac54620cfb913e5ce36c25');
    assert.equal(scan.with.version, 'v0.75.0');
    assert.equal(scan.env.TRIVY_IMAGE_SRC, 'remote');
    assert.equal(scan.env.TRIVY_INSECURE, undefined, 'Loopback HTTP must not disable TLS validation for database registries');
    assert.equal(scan.if, "${{ always() && steps.image-evidence.outcome == 'success' }}");
    assert.equal(scan['continue-on-error'], undefined);
  }
  assert.ok(steps.some(s => s.run?.includes('security-check.mjs image artifacts/image-evidence/security/admin.json artifacts/image-evidence/security/http.json artifacts/image-evidence/security/webadmin.json') && !s['continue-on-error']));
  assert.ok(steps.some(s => s.run === 'node scripts/ci/packaged-image-evidence.mjs cleanup' && s.if === 'always()'));
  assert.ok(steps.some(s => s.if === 'always()' && s.with?.name === 'packaged-image-archives-${{ matrix.arch }}' && s.with.path === 'artifacts/image-evidence/archives'));
});
