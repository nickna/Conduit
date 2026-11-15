import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { platformInventory, selectBuildChild, validateIndex, validateConfig, platforms, runCommand } from './managed-platforms.mjs';
import { services } from './image-promotion.mjs';

const sha = 'a'.repeat(40);
const digest = i => `sha256:${String(i).padStart(64, '0')}`;
const records = () => services.flatMap((service, i) => platforms.map((platform, p) => ({ service, platform,
  image: `ghcr.io/nickna/conduit-${service}`, digest: digest(i * 2 + p + 1), buildDigest: digest(i * 2 + p + 11), version: '3.0.0', sha })));
const index = children => ({ manifests: children.map(r => ({ digest: r.digest,
  platform: { os: 'linux', architecture: r.platform.split('/')[1] } })).concat(children.flatMap(r => r.attestations ?? [])) });
const attestation = (identity, childDigest) => ({ digest: identity, platform: { os: 'unknown', architecture: 'unknown' },
  annotations: { 'vnd.docker.reference.type': 'attestation-manifest', 'vnd.docker.reference.digest': childDigest } });

test('requires six coherent managed native builds before assembling three indexes', () => {
  assert.equal(platformInventory(records()).length, 6);
  for (const mutate of [r => r.pop(), r => r.push(r[0]), r => r[1] = r[0],
    r => r[1].sha = 'b'.repeat(40), r => r[1].version = '3.0.1', r => r[1].image = 'attacker/image',
    r => r[1].platform = 'linux/s390x', r => r[1].digest = r[0].digest, r => delete r[0].buildDigest]) {
    const bad = records(); mutate(bad); assert.throws(() => platformInventory(bad));
  }
});

test('native build output resolves its exact child despite Buildx attestations', () => {
  const child = records()[1];
  assert.equal(selectBuildChild(index([child]), child.platform, child.buildDigest), child.digest);
  const attested = index([child]); attested.manifests.push(attestation(digest(99), child.digest));
  assert.equal(selectBuildChild(attested, child.platform, child.buildDigest), child.digest);
  assert.equal(selectBuildChild({}, child.platform, child.buildDigest), child.buildDigest);
  assert.throws(() => selectBuildChild(index([records()[0]]), 'linux/arm64', child.buildDigest));
  assert.throws(() => selectBuildChild(index(records().slice(0, 2)), 'linux/arm64', child.buildDigest));
  attested.manifests[1].annotations['vnd.docker.reference.digest'] = digest(98);
  assert.throws(() => selectBuildChild(attested, child.platform, child.buildDigest), /bind the exact/);
});

test('index membership preserves all original child-bound provenance descriptors', () => {
  const children = records().slice(0, 2).map((r, i) => ({ ...r, attestations: [attestation(digest(90 + i), r.digest)] }));
  validateIndex(index(children), children);
  const missing = index(children); missing.manifests.pop();
  assert.throws(() => validateIndex(missing, children), /original attestations/);
  const changed = index(children); changed.manifests[2].annotations['vnd.docker.reference.digest'] = digest(99);
  assert.throws(() => validateIndex(changed, children), /bind the exact/);
});

test('index membership rejects missing, duplicate, substituted and extra children', () => {
  const children = records().slice(0, 2);
  validateIndex(index(children), children);
  for (const mutate of [m => m.manifests.pop(), m => m.manifests.push(m.manifests[0]),
    m => m.manifests[1] = m.manifests[0], m => m.manifests[1].digest = digest(99),
    m => m.manifests[1].platform.architecture = 's390x']) {
    const bad = index(children); mutate(bad); assert.throws(() => validateIndex(bad, children));
  }
});

test('actual image config must match architecture and release/source labels', () => {
  const record = records()[1];
  const config = { Os: 'linux', Architecture: 'arm64', Config: { Labels: {
    'org.opencontainers.image.version': record.version, 'org.opencontainers.image.revision': record.sha } } };
  validateConfig(config, record);
  for (const mutate of [c => c.Architecture = 'amd64', c => c.Os = 'windows',
    c => c.Config.Labels['org.opencontainers.image.version'] = '3.0.1', c => delete c.Config.Labels['org.opencontainers.image.revision']]) {
    const bad = structuredClone(config); mutate(bad); assert.throws(() => validateConfig(bad, record));
  }
});

test('record/assemble/select preserves three canonical indexes and exact per-platform child outputs', () => {
  const directory = mkdtempSync(join(tmpdir(), 'conduit-managed-platforms-'));
  const previous = Object.fromEntries(['GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_OUTPUT'].map(k => [k, process.env[k]]));
  const manifests = new Map(), digests = new Map();
  const inspected = [];
  const transport = {
    manifest: ref => structuredClone(manifests.get(ref)),
    digest: ref => digests.get(ref),
    inspectConfig(record) {
      inspected.push(record.platform);
      return { platform: record.platform, image: `${record.image}@${record.digest}`, architecture: record.platform.split('/')[1] };
    },
    createIndex(ref, children) {
      const identity = digest(50 + digests.size);
      digests.set(ref, identity);
      manifests.set(`${children[0].image}@${identity}`, index(children));
    },
  };
  try {
    process.env.GITHUB_RUN_ID = '123'; process.env.GITHUB_RUN_ATTEMPT = '2';
    delete process.env.GITHUB_OUTPUT;
    const input = join(directory, 'children'), output = join(directory, 'indexes');
    mkdirSync(input);
    for (const [i, record] of records().entries()) {
      record.attestations = [attestation(digest(90 + i), record.digest)];
      manifests.set(`${record.image}@${record.buildDigest}`, index([record]));
      runCommand(['record', record.service, record.platform, record.image, record.buildDigest,
        record.version, record.sha, join(input, `${record.service}-${record.platform.split('/')[1]}.json`)], transport);
    }
    runCommand(['assemble', input, output, '3.0.0-candidate-123-2'], transport);
    for (const platform of platforms) {
      const canonical = join(directory, `image-set-${platform.split('/')[1]}.json`);
      const evidence = join(directory, `selection-${platform.split('/')[1]}.json`);
      const githubOutput = join(directory, `outputs-${platform.split('/')[1]}.txt`);
      process.env.GITHUB_OUTPUT = githubOutput;
      runCommand(['select', output, platform, canonical, evidence], transport);
      const set = JSON.parse(readFileSync(canonical, 'utf8'));
      assert.equal(set.images.length, 3);
      assert.ok(set.images.every(r => [...digests.values()].includes(r.digest)), 'Promotion must retain index identities');
      const selected = JSON.parse(readFileSync(evidence, 'utf8'));
      assert.ok(selected.selected.every(r => r.platform === platform));
      const lines = readFileSync(githubOutput, 'utf8').trim().split('\n');
      assert.deepEqual(lines.sort(), records().filter(r => r.platform === platform).map(r => `${r.service}=${r.image}@${r.digest}`).sort());
    }
    assert.equal(inspected.length, 12);
    const canonicalPath = join(output, 'records', 'admin.json');
    const changed = JSON.parse(readFileSync(canonicalPath, 'utf8'));
    changed.digest = digest(99); writeFileSync(canonicalPath, JSON.stringify(changed));
    assert.throws(() => runCommand(['select', output, 'linux/arm64', join(directory, 'bad.json'), join(directory, 'bad-proof.json')], transport), /bindings/);
  } finally {
    for (const [key, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
    rmSync(directory, { recursive: true, force: true });
  }
});
