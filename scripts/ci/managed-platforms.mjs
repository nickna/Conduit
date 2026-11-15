// Managed Linux image qualification. Promotion keeps one immutable OCI index per
// service; platform records and exact-child execution are separate evidence.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { appendFileSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { imageSet, services } from './image-promotion.mjs';

export const platforms = ['linux/amd64', 'linux/arm64'];
const digestPattern = /^sha256:[a-f0-9]{64}$/;
const imageName = service => `ghcr.io/nickna/conduit-${service}`;
function validateAttestation(descriptor, childDigest) {
  assert.equal(descriptor.platform?.os, 'unknown', 'Attestation OS must be unknown');
  assert.equal(descriptor.platform?.architecture, 'unknown', 'Attestation architecture must be unknown');
  assert.equal(descriptor.annotations?.['vnd.docker.reference.type'], 'attestation-manifest', 'Unexpected non-runtime manifest');
  assert.equal(descriptor.annotations?.['vnd.docker.reference.digest'], childDigest, 'Attestation must bind the exact runtime child');
  assert.ok(digestPattern.test(descriptor.digest), 'Invalid attestation digest');
}

export function platformInventory(records) {
  assert.equal(records.length, services.length * platforms.length, 'Require every managed service/platform candidate');
  for (const service of services) {
    for (const platform of platforms) {
      assert.equal(records.filter(r => r.service === service && r.platform === platform).length, 1,
        `Require exactly one ${service} ${platform} candidate`);
    }
  }
  const { version, sha } = records[0];
  for (const record of records) {
    assert.ok(record.image === imageName(record.service) && digestPattern.test(record.digest), 'Invalid child identity');
    assert.ok(digestPattern.test(record.buildDigest), 'Missing immutable build output identity');
    assert.equal(record.version, version, 'Platform versions differ');
    assert.equal(record.sha, sha, 'Platform source revisions differ');
    for (const attestation of record.attestations ?? []) validateAttestation(attestation, record.digest);
  }
  // Reuse the existing release version/source/repository validation contract.
  imageSet(services.map(service => records.find(r => r.service === service)));
  for (const service of services) {
    assert.equal(new Set(records.filter(r => r.service === service).map(r => r.digest)).size, platforms.length,
      `${service} must have distinct architecture children`);
  }
  return records;
}

export function selectBuildChild(manifest, platform, buildDigest) {
  assert.ok(platforms.includes(platform) && digestPattern.test(buildDigest), 'Unsupported build platform/identity');
  if (!manifest.manifests) return buildDigest;
  const runnable = manifest.manifests.filter(m => m.platform?.os !== 'unknown');
  assert.equal(runnable.length, 1, 'A native build must contain exactly one runnable platform');
  const [os, architecture] = platform.split('/');
  const selected = runnable[0];
  assert.equal(selected.platform?.os, os, 'Build OS differs');
  assert.equal(selected.platform?.architecture, architecture, 'Build architecture differs');
  assert.ok(digestPattern.test(selected.digest), 'Invalid child digest');
  for (const attestation of manifest.manifests.filter(m => m.platform?.os === 'unknown')) {
    validateAttestation(attestation, selected.digest);
  }
  return selected.digest;
}

export function validateIndex(manifest, records) {
  const attestations = records.flatMap(r => r.attestations ?? []);
  assert.equal(manifest.manifests?.length, platforms.length + attestations.length,
    'Managed index must contain exactly the qualified children and their original attestations');
  for (const record of records) {
    const [os, architecture] = record.platform.split('/');
    const matches = manifest.manifests.filter(m => m.platform?.os === os && m.platform?.architecture === architecture);
    assert.equal(matches.length, 1, `Missing/duplicate index platform ${record.platform}`);
    assert.equal(matches[0].digest, record.digest, 'Index child differs from qualified native build');
    for (const attestation of record.attestations ?? []) {
      const matches = manifest.manifests.filter(m => m.digest === attestation.digest);
      assert.equal(matches.length, 1, 'Missing/duplicate original build attestation');
      validateAttestation(matches[0], record.digest);
    }
  }
}

export function validateConfig(config, record) {
  const [os, architecture] = record.platform.split('/');
  assert.equal(config.Os, os, 'Image config OS differs');
  assert.equal(config.Architecture, architecture, 'Image config architecture differs');
  assert.equal(config.Config?.Labels?.['org.opencontainers.image.version'], record.version, 'Image version label differs');
  assert.equal(config.Config?.Labels?.['org.opencontainers.image.revision'], record.sha, 'Image source label differs');
}

const read = path => JSON.parse(readFileSync(path, 'utf8'));
const save = (path, value) => {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(value, null, 2) + '\n');
};
function docker(args) {
  return execFileSync('docker', args, { encoding: 'utf8', timeout: 180_000, stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
const manifest = ref => JSON.parse(docker(['buildx', 'imagetools', 'inspect', ref, '--raw']));
const digest = ref => JSON.parse(docker(['buildx', 'imagetools', 'inspect', ref, '--format', '{{json .Manifest}}'])).digest;
function inspectConfig(record) {
  const ref = `${record.image}@${record.digest}`;
  docker(['pull', '--platform', record.platform, ref]);
  const config = JSON.parse(docker(['image', 'inspect', ref]))[0];
  validateConfig(config, record);
  return { platform: record.platform, image: ref, imageId: config.Id, os: config.Os, architecture: config.Architecture };
}

const registry = { manifest, digest, inspectConfig, createIndex(ref, children) {
  // Combine original build indexes so their provenance/SBOM descriptors survive.
  docker(['buildx', 'imagetools', 'create', '--tag', ref, ...children.map(r => `${r.image}@${r.buildDigest}`)]);
} };

export function runCommand([command, ...args], transport = registry) {
  if (command === 'record') {
    const [service, platform, image, buildDigest, version, sha, output] = args;
    assert.ok(services.includes(service) && platforms.includes(platform), 'Unsupported service/platform');
    assert.equal(image, imageName(service), 'Unexpected candidate repository');
    const buildManifest = transport.manifest(`${image}@${buildDigest}`);
    const childDigest = selectBuildChild(buildManifest, platform, buildDigest);
    const attestations = buildManifest.manifests?.filter(m => m.platform?.os === 'unknown') ?? [];
    const record = { service, platform, image, digest: childDigest, buildDigest, attestations, version, sha };
    imageSet(services.map(s => ({ ...record, service: s, image: imageName(s) })));
    const config = transport.inspectConfig(record);
    save(output, { ...record, config });
  } else if (command === 'assemble') {
    const [input, output, tag] = args;
    const records = platformInventory(readdirSync(input).filter(p => p.endsWith('.json')).map(p => read(join(input, p))));
    assert.equal(tag, `${records[0].version}-candidate-${process.env.GITHUB_RUN_ID}-${process.env.GITHUB_RUN_ATTEMPT}`,
      'Index tag must be bound to this release attempt');
    const indexes = [];
    for (const service of services) {
      const children = records.filter(r => r.service === service);
      const image = imageName(service), ref = `${image}:${tag}`;
      transport.createIndex(ref, children);
      const indexDigest = transport.digest(ref);
      assert.ok(digestPattern.test(indexDigest), 'Registry did not return an index digest');
      validateIndex(transport.manifest(`${image}@${indexDigest}`), children);
      const record = { service, image, digest: indexDigest, version: records[0].version, sha: records[0].sha };
      indexes.push(record);
      save(join(output, 'records', `${service}.json`), record);
    }
    imageSet(indexes);
    save(join(output, 'platform-bindings.json'), { schemaVersion: 1, records, indexes });
  } else if (command === 'select') {
    const [input, platform, output, evidence] = args;
    assert.ok(platforms.includes(platform), 'Unsupported validation platform');
    const bindings = read(join(input, 'platform-bindings.json'));
    const records = platformInventory(bindings.records);
    const set = imageSet(readdirSync(join(input, 'records')).filter(p => p.endsWith('.json')).map(p => read(join(input, 'records', p))));
    assert.deepEqual(set, imageSet(bindings.indexes), 'Canonical index set differs from its platform bindings');
    const selected = [];
    for (const index of set.images) {
      const children = records.filter(r => r.service === index.service);
      assert.equal(index.version, children[0].version, 'Index version differs from children');
      assert.equal(index.sha, children[0].sha, 'Index source differs from children');
      validateIndex(transport.manifest(`${index.image}@${index.digest}`), children);
      const child = children.find(r => r.platform === platform);
      selected.push({ service: index.service, index: `${index.image}@${index.digest}`, ...transport.inspectConfig(child) });
      if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `${index.service}=${child.image}@${child.digest}\n`);
    }
    save(output, set);
    save(evidence, { schemaVersion: 1, platform, selected });
  } else throw new Error('Expected record, assemble or select');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) runCommand(process.argv.slice(2));
