import { test } from 'node:test';
import assert from 'node:assert/strict';
import { imageSet, preflight, promote, compareVersions, services } from './image-promotion.mjs';

const sha = 'a'.repeat(40);
function set(version = '2.0.0', seed = 'b') {
  return imageSet(services.map((service, i) => ({ service, image: `ghcr.io/nickna/conduit-${service}`,
    version, sha, digest: `sha256:${seed.repeat(63)}${i}` })));
}
class Registry {
  tags = new Map(); labelsByDigest = new Map(); writes = []; failTarget = null; failRollback = false;
  add(images, channel = true) {
    for (const image of images.images) {
      const ref = `${image.image}@${image.digest}`;
      this.labelsByDigest.set(ref, { 'org.opencontainers.image.version': images.version, 'org.opencontainers.image.revision': sha });
      if (channel) this.tags.set(`${image.image}:${images.channel}`, image.digest);
    }
  }
  async digest(ref) { return this.labelsByDigest.has(ref) ? ref.split('@')[1] : this.tags.get(ref) ?? null; }
  async labels(ref) { return this.labelsByDigest.get(ref); }
  async copy(source, target) {
    this.writes.push(target);
    if (this.failTarget === target) { this.failTarget = null; throw new Error('Injected registry write failure'); }
    if (this.failRollback && source.includes(`sha256:${'a'.repeat(63)}`)) throw new Error('Injected rollback failure');
    this.tags.set(target, source.split('@')[1]);
  }
}
async function release(candidate, registry) {
  const ledger = await preflight(candidate, registry); ledger.migrationSucceeded = true;
  return promote(ledger, registry);
}
function assertChannel(registry, expected) {
  for (const image of expected.images) assert.equal(registry.tags.get(`${image.image}:${expected.channel}`), image.digest);
}
test('candidate inventory rejects missing, duplicate, mismatched or mutable identities', () => {
  const records = set().images;
  for (const bad of [records.slice(1), [records[0], records[0], records[2]], records.map(r => ({ ...r, digest: 'latest' })),
    records.map((r, i) => ({ ...r, sha: i ? sha : 'c'.repeat(40) })), records.map(r => ({ ...r, image: 'attacker/image' }))])
    assert.throws(() => imageSet(bad));
});
test('SemVer comparison includes prerelease identifiers and arbitrary size integers', () => {
  for (const [a, b] of [['1.0.0-beta.2', '1.0.0-beta.10'], ['1.0.0-beta', '1.0.0'],
    ['1.0.0-1', '1.0.0-a'], ['1.0.0-a', '1.0.0-a.1'], ['9007199254740992.0.0', '9007199254740993.0.0']]) {
    assert.equal(compareVersions(a, b), -1); assert.equal(compareVersions(b, a), 1);
  }
});
for (const order of [[set('2.0.0', 'b'), set('3.0.0', 'c')], [set('3.0.0', 'c'), set('2.0.0', 'b')]]) {
  test(`queued releases finish at newest version in order ${order.map(s => s.version)}`, async () => {
    const registry = new Registry(); registry.add(set('1.0.0', 'a')); order.forEach(s => registry.add(s, false));
    for (const s of order) {
      if (s.version === '2.0.0' && order[0].version === '3.0.0') await assert.rejects(release(s, registry), /backward/);
      else await release(s, registry);
    }
    assertChannel(registry, set('3.0.0', 'c'));
  });
}
for (const failedService of services) test(`partial promotion at ${failedService} rolls back and can recover`, async () => {
  const registry = new Registry(), old = set('1.0.0', 'a'), desired = set();
  registry.add(old); registry.add(desired, false);
  const ledger = await preflight(desired, registry); ledger.migrationSucceeded = true;
  registry.failTarget = `ghcr.io/nickna/conduit-${failedService}:latest`;
  const journal = [];
  await assert.rejects(promote(ledger, registry, l => journal.push(structuredClone(l))), /Injected/);
  assertChannel(registry, old); assert.equal(ledger.state, 'rolled-back'); assert.ok(journal.length > 3);
  await promote(ledger, registry); assertChannel(registry, desired); assert.equal(ledger.state, 'promoted');
});
test('bootstrap partial promotion retains ledger and finishes without deleting shared manifests', async () => {
  const registry = new Registry(), desired = set(); registry.add(desired, false);
  const ledger = await preflight(desired, registry); ledger.migrationSucceeded = true;
  registry.failTarget = 'ghcr.io/nickna/conduit-http:latest';
  await assert.rejects(promote(ledger, registry)); assert.equal(ledger.state, 'recovery-required');
  await assert.rejects(preflight(desired, registry), /Partial/);
  await promote(ledger, registry); assertChannel(registry, desired);
});
test('failed rollback remains auditable and stale recovery cannot overwrite a later release', async () => {
  const registry = new Registry(), old = set('1.0.0', 'a'), desired = set(); registry.add(old); registry.add(desired, false);
  const ledger = await preflight(desired, registry); ledger.migrationSucceeded = true;
  registry.failTarget = 'ghcr.io/nickna/conduit-http:latest'; registry.failRollback = true;
  await assert.rejects(promote(ledger, registry)); assert.equal(ledger.state, 'recovery-required'); assert.ok(ledger.rollbackFailure);
  registry.failRollback = false; registry.add(set('3.0.0', 'c'));
  await assert.rejects(promote(ledger, registry), /Stale/);
});
test('stable and beta channels remain independent; migration proof is mandatory', async () => {
  const registry = new Registry(), stable = set(), beta = set('3.0.0-beta.1', 'c');
  registry.add(stable); registry.add(beta, false);
  const ledger = await preflight(beta, registry);
  await assert.rejects(promote(ledger, registry), /Migration/); assert.equal(registry.writes.length, 0);
  ledger.migrationSucceeded = true; await promote(ledger, registry);
  assertChannel(registry, stable); assertChannel(registry, beta);
});
test('all candidates and immutable version tags are checked before migration', async () => {
  const registry = new Registry(), desired = set(); registry.add(desired, false);
  registry.labelsByDigest.delete(`${desired.images[2].image}@${desired.images[2].digest}`);
  await assert.rejects(preflight(desired, registry), /missing/); assert.equal(registry.writes.length, 0);
  registry.add(desired, false); registry.tags.set(`${desired.images[1].image}:2.0.0`, `sha256:${'f'.repeat(64)}`);
  await assert.rejects(preflight(desired, registry), /Immutable/); assert.equal(registry.writes.length, 0);
});
test('registry success with the wrong resulting digest fails verification and rolls back', async () => {
  const registry = new Registry(), old = set('1.0.0', 'a'), desired = set(); registry.add(old); registry.add(desired, false);
  const ledger = await preflight(desired, registry); ledger.migrationSucceeded = true;
  const copy = registry.copy.bind(registry); let corrupt = true;
  registry.copy = async (source, target) => {
    await copy(source, target);
    if (corrupt && target.endsWith(':latest')) { corrupt = false; registry.tags.set(target, 'wrong'); }
  };
  await assert.rejects(promote(ledger, registry), /Digest verification/); assertChannel(registry, old);
});
