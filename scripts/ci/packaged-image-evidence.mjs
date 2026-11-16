// Ordinary CI evidence only: these are local test images, never release candidates.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { createReadStream, existsSync, mkdirSync, readFileSync, statSync, writeFileSync, appendFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';

export const services = { admin: 'conduit-admin:ci', http: 'conduit-http:ci', webadmin: 'conduit-webadmin:ci' };
const registryImage = 'registry:3@sha256:82f6ff4a7f982960c8aa51617fea3aabc485df6e55cce80d22deb1de36f1bc3c';
const ownerLabel = 'conduit.ci.image-evidence.owner';
const digestPattern = /^sha256:[a-f0-9]{64}$/;
const sha = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
const read = file => JSON.parse(readFileSync(file, 'utf8'));
const save = (file, value) => writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`);
const docker = args => execFileSync('docker', args, { encoding: 'utf8', timeout: 600_000, maxBuffer: 16 * 1024 * 1024 }).trim();
const inspect = id => JSON.parse(docker(['inspect', id]))[0];
const tar = (archive, entry) => execFileSync('tar', ['-xOf', archive, entry], { maxBuffer: 16 * 1024 * 1024 });
async function fileDigest(file) {
  const hash = createHash('sha256');
  for await (const bytes of createReadStream(file)) hash.update(bytes);
  return `sha256:${hash.digest('hex')}`;
}

export function validateArchiveConfig(image, bytes, platform, buildOutputs) {
  const config = JSON.parse(bytes);
  const configDigest = sha(bytes);
  assert.equal(`${image.Os}/${image.Architecture}`, platform, 'Loaded image must match the native runner');
  assert.equal(`${config.os}/${config.architecture}`, platform, 'Archived image platform differs');
  assert.deepEqual(config.rootfs.diff_ids, image.RootFS.Layers, 'Archive and loaded root filesystems differ');
  assert.match(image.Id, digestPattern);
  // Classic Docker reports the config ID; the containerd image store may report
  // the manifest ID. Buildx supplies the config identity in either case.
  const metadata = JSON.parse(buildOutputs.metadata);
  assert.equal(metadata['containerimage.config.digest'], configDigest, 'Buildx and archive configs differ');
  assert.ok([configDigest, image.Id].includes(buildOutputs.imageid), 'Buildx and loaded image identities differ');
  return { configDigest, config };
}

export function validateRegistryBinding(manifestBytes, manifestDigest, configBytes, image) {
  assert.match(manifestDigest, digestPattern);
  assert.equal(sha(manifestBytes), manifestDigest, 'Registry manifest bytes differ from its digest');
  const manifest = JSON.parse(manifestBytes);
  assert.equal(manifest.schemaVersion, 2);
  assert.ok(Array.isArray(manifest.layers) && manifest.layers.length > 0, 'Require a runnable image manifest');
  assert.equal(manifest.config.digest, image.configDigest, 'Registry and archived configs differ');
  assert.equal(sha(configBytes), image.configDigest, 'Registry config bytes differ from the archive');
  return manifest;
}

export function validateReport(report, image) {
  assert.equal(report.ArtifactName, image.scanReference, 'Trivy must report the genuine immutable scan reference');
  assert.equal(report.Metadata.ImageID, image.configDigest, 'Trivy scanned a different image config');
  assert.ok(Array.isArray(report.Results), 'Require a complete Trivy image report');
}

export function validateOwned(resource, actual, owner) {
  assert.equal(actual.Id, resource.id);
  assert.equal(actual.Name, `/${resource.name}`);
  assert.equal(actual.Image, resource.imageId);
  assert.equal(actual.Config.Labels[ownerLabel], owner, 'Refuse cleanup without the exact owner');
}

function scannerEvidence(output) {
  const cache = resolve('.cache/trivy');
  const metadata = {};
  for (const database of ['db', 'java-db']) {
    const file = join(cache, database, 'metadata.json');
    if (existsSync(file)) {
      const bytes = readFileSync(file);
      writeFileSync(join(output, `${database}-metadata.json`), bytes);
      metadata[database] = { sha256: sha(bytes), metadata: JSON.parse(bytes) };
    }
  }
  try {
    metadata.version = execFileSync('trivy', ['--version'], {
      encoding: 'utf8', timeout: 30_000, env: { ...process.env, TRIVY_CACHE_DIR: cache },
    }).trim();
  } catch (error) { metadata.versionError = error.message; }
  save(join(output, 'scanner.json'), metadata);
  return metadata;
}

export async function run(command) {
  const output = resolve('artifacts/image-evidence');
  const stateFile = join(output, 'state.json');
  mkdirSync(output, { recursive: true });
  if (command === 'prepare') {
    assert.equal(process.platform, 'linux', 'The fixture registry uses the native Linux runner loopback');
    assert.equal(existsSync(stateFile), false, 'Do not overwrite an earlier evidence attempt');
    const platform = process.env.CI_CONTAINER_PLATFORM;
    assert.ok(['linux/amd64', 'linux/arm64'].includes(platform));
    assert.equal(`linux/${process.arch === 'x64' ? 'amd64' : process.arch}`, platform);
    const event = process.env.GITHUB_EVENT_PATH ? read(process.env.GITHUB_EVENT_PATH) : {};
    const git = args => execFileSync('git', args, { encoding: 'utf8' }).trim();
    const state = { owner: `ci-image-${randomUUID()}`, startedAt: new Date().toISOString(), platform,
      source: { checkoutSha: git(['rev-parse', 'HEAD']), tree: git(['rev-parse', 'HEAD^{tree}']),
        event: process.env.GITHUB_EVENT_NAME, prHead: event.pull_request?.head.sha, prBase: event.pull_request?.base.sha,
        runId: process.env.GITHUB_RUN_ID, runAttempt: process.env.GITHUB_RUN_ATTEMPT,
        apiBuildArgumentDefaults: { COMMIT_SHA: 'dev', VERSION: '3.0.0', BUILD_TIMESTAMP: 'unknown' },
        scope: 'Ordinary CI synthetic checkout and local images; not release identities. Runtime header values are not observed by this helper.' },
      images: [], tags: [], registry: null, network: null };
    const persist = () => save(stateFile, state);
    persist();
    mkdirSync(join(output, 'archives'), { recursive: true });
    mkdirSync(join(output, 'security'), { recursive: true });
    for (const [service, reference] of Object.entries(services)) {
      const image = JSON.parse(docker(['image', 'inspect', reference]))[0];
      const buildOutputs = JSON.parse(process.env[`BUILD_OUTPUTS_${service.toUpperCase()}`]);
      save(join(output, `${service}.inspect.json`), image);
      save(join(output, `${service}.buildx.json`), buildOutputs);
      const archive = join(output, 'archives', `${service}.tar`);
      docker(['image', 'save', '--output', archive, image.Id]);
      const entries = JSON.parse(tar(archive, 'manifest.json'));
      assert.equal(entries.length, 1, 'Archive must contain exactly one loaded service image');
      assert.match(entries[0].Config, /^(?:blobs\/sha256\/[a-f0-9]{64}|[a-f0-9]{64}\.json)$/);
      const configBytes = tar(archive, entries[0].Config);
      const { configDigest } = validateArchiveConfig(image, configBytes, platform, buildOutputs);
      writeFileSync(join(output, `${service}.config.json`), configBytes);
      state.images.push({ service, reference, imageId: image.Id, configDigest,
        archive: `archives/${service}.tar`, archiveSha256: await fileDigest(archive), archiveBytes: statSync(archive).size });
      assert.equal(JSON.parse(docker(['image', 'inspect', reference]))[0].Id, image.Id, 'Image tag changed during export');
      persist();
    }
    // Docker exports and registry transport can use different compression/media
    // types. The exact config (including every rootfs diff_id) binds both to the
    // same immutable runtime payload; record both identities without relabelling.
    docker(['pull', registryImage]);
    const registryConfig = JSON.parse(docker(['image', 'inspect', registryImage]))[0];
    save(join(output, 'registry-image.inspect.json'), registryConfig);
    const name = `conduit-${state.owner}`;
    state.network = { id: docker(['network', 'create', '--label', `${ownerLabel}=${state.owner}`, name]), name };
    persist();
    const registryName = `${name}-registry`;
    const id = docker(['create', '--name', registryName, '--network', state.network.id,
      '--label', `${ownerLabel}=${state.owner}`, '--cpus', '1', '--memory', '256m',
      '--publish', '127.0.0.1::5000', registryConfig.Id]);
    state.registry = { id, name: registryName, imageId: registryConfig.Id };
    persist();
    docker(['start', id]);
    const binding = inspect(id).NetworkSettings.Ports['5000/tcp'][0];
    assert.equal(binding.HostIp, '127.0.0.1');
    const origin = `http://127.0.0.1:${binding.HostPort}`;
    state.registry.origin = origin;
    persist();
    const deadline = Date.now() + 30_000;
    while (!(await fetch(`${origin}/v2/`, { redirect: 'error' }).then(r => r.ok).catch(() => false))) {
      assert.ok(Date.now() < deadline, 'Registry readiness timed out');
      await new Promise(r => setTimeout(r, 200));
    }
    for (const image of state.images) {
      const repository = `ci/${image.service}`;
      const tag = `${origin.slice('http://'.length)}/${repository}:captured`;
      docker(['tag', image.imageId, tag]);
      state.tags.push({ reference: tag, imageId: image.imageId });
      persist();
      writeFileSync(join(output, `${image.service}.push.log`), docker(['push', tag]));
      const response = await fetch(`${origin}/v2/${repository}/manifests/captured`, {
        redirect: 'error', headers: { Accept: 'application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json' },
      });
      assert.equal(response.status, 200);
      const manifestBytes = Buffer.from(await response.arrayBuffer());
      const digest = response.headers.get('docker-content-digest');
      const config = await fetch(`${origin}/v2/${repository}/blobs/${image.configDigest}`, { redirect: 'error' });
      assert.equal(config.status, 200);
      validateRegistryBinding(manifestBytes, digest, Buffer.from(await config.arrayBuffer()), image);
      writeFileSync(join(output, `${image.service}.registry-manifest.json`), manifestBytes);
      image.registryManifestDigest = digest;
      image.scanReference = `${origin.slice('http://'.length)}/${repository}@${digest}`;
      persist();
      if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `${image.service}=${image.scanReference}\n`);
    }
    state.prepared = true;
    persist();
  } else if (command === 'verify') {
    const state = read(stateFile);
    assert.equal(state.prepared, true);
    const results = [];
    let failure;
    for (const image of state.images) {
      try {
        const reportPath = join(output, 'security', `${image.service}.json`);
        validateReport(read(reportPath), image);
        assert.equal(await fileDigest(join(output, image.archive)), image.archiveSha256, 'Retained archive changed');
        assert.equal(JSON.parse(docker(['image', 'inspect', image.reference]))[0].Id, image.imageId);
        results.push({ service: image.service, passed: true, reportSha256: await fileDigest(reportPath) });
      } catch (error) { results.push({ service: image.service, passed: false, error: error.message }); failure = error; }
    }
    const scanner = scannerEvidence(output);
    let boot;
    try {
      assert.match(scanner.version, /Version:\s*0\.75\.0(?:\s|$)/, 'Require the release-pinned scanner version');
      assert.equal(scanner.db?.metadata.Version, 2, 'Require raw vulnerability DB identity');
      boot = read('artifacts/container-smoke/evidence.json');
      assert.equal(boot.succeeded, true, 'Native packaged business flows must pass for these images');
      assert.equal(boot.hostPlatform, state.platform);
      assert.equal(boot.expectedPlatform, state.platform);
      for (const image of state.images) assert.equal(boot.images[image.service].id, image.imageId, 'Booted and scanned images differ');
    } catch (error) { failure ??= error; }
    save(join(output, 'verification.json'), { images: results, scanner,
      nativeBoot: boot && { succeeded: boot.succeeded, hostPlatform: boot.hostPlatform, checks: boot.checks.length },
      succeeded: !failure, error: failure?.message });
    if (failure) throw failure;
  } else if (command === 'cleanup') {
    scannerEvidence(output);
    if (!existsSync(stateFile)) {
      save(join(output, 'cleanup.json'), { allRemoved: true, resources: [], note: 'Preparation did not create resources.' });
      return;
    }
    const state = read(stateFile);
    const results = [];
    for (const tag of state.tags) {
      try {
        assert.equal(JSON.parse(docker(['image', 'inspect', tag.reference]))[0].Id, tag.imageId);
        docker(['image', 'rm', tag.reference]);
        results.push({ reference: tag.reference, removed: true });
      } catch (error) { results.push({ reference: tag.reference, removed: false, error: error.message }); }
    }
    if (state.registry) {
      try {
        validateOwned(state.registry, inspect(state.registry.id), state.owner);
        const logs = spawnSync('docker', ['logs', state.registry.id], { encoding: 'utf8', timeout: 30_000 });
        writeFileSync(join(output, 'registry.log'), (logs.stdout ?? '') + (logs.stderr ?? ''));
        docker(['rm', '--force', '--volumes', state.registry.id]);
        results.push({ ...state.registry, removed: true });
      } catch (error) { results.push({ ...state.registry, removed: false, error: error.message }); }
    }
    if (state.network) {
      try {
        const actual = JSON.parse(docker(['network', 'inspect', state.network.id]))[0];
        assert.equal(actual.Id, state.network.id);
        assert.equal(actual.Name, state.network.name);
        assert.equal(actual.Labels[ownerLabel], state.owner);
        assert.equal(Object.keys(actual.Containers).length, 0);
        docker(['network', 'rm', state.network.id]);
        results.push({ ...state.network, removed: true });
      } catch (error) { results.push({ ...state.network, removed: false, error: error.message }); }
    }
    const allRemoved = results.every(r => r.removed);
    save(join(output, 'cleanup.json'), { finishedAt: new Date().toISOString(), allRemoved, resources: results });
    assert.ok(allRemoved, 'Owned image-evidence fixture cleanup failed');
  } else throw new Error('Expected prepare, verify, or cleanup');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) await run(process.argv[2]);
