// Verify the Docker CLI primitive used by promotion against an isolated registry.
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import assert from 'node:assert/strict';
const directory = resolve('artifacts/registry-copy'); mkdirSync(directory, { recursive: true });
const name = `conduit-registry-proof-${process.pid}-${Date.now()}`;
const run = args => execFileSync('docker', args, { encoding: 'utf8', timeout: 180_000 }).trim();
let source, target;
const evidence = {};
try {
  run(['run', '--detach', '--name', name, '--publish', process.platform === 'win32' ? '0:5000' : '127.0.0.1::5000', 'registry:3']);
  const address = JSON.parse(run(['inspect', name]))[0].NetworkSettings.Ports['5000/tcp'][0];
  const repository = `127.0.0.1:${address.HostPort}/promotion-proof`;
  source = `${repository}:candidate`; target = `${repository}:official`;
  const until = Date.now() + 30_000;
  while (!(await fetch(`http://127.0.0.1:${address.HostPort}/v2/`).then(r => r.ok).catch(() => false))) {
    if (Date.now() > until) throw new Error('Registry readiness timed out');
    await new Promise(r => setTimeout(r, 100));
  }
  writeFileSync(`${directory}/Dockerfile`, 'FROM scratch\nLABEL org.opencontainers.image.version="1.0.0"\n');
  run(['build', '--tag', source, directory]); run(['push', source]);
  const digest = ref => JSON.parse(run(['buildx', 'imagetools', 'inspect', ref, '--format', '{{json .Manifest}}'])).digest;
  evidence.candidate = digest(source);
  run(['buildx', 'imagetools', 'create', '--prefer-index=false', '--tag', target, `${repository}@${evidence.candidate}`]);
  evidence.official = digest(target);
  assert.match(evidence.candidate, /^sha256:[a-f0-9]{64}$/);
  assert.equal(evidence.official, evidence.candidate);
  evidence.succeeded = true;
} finally {
  const logs = spawnSync('docker', ['logs', name], { encoding: 'utf8', timeout: 15_000 });
  writeFileSync(`${directory}/registry.log`, (logs.stdout ?? '') + (logs.stderr ?? ''));
  spawnSync('docker', ['rm', '--force', name], { timeout: 15_000 });
  if (source) spawnSync('docker', ['image', 'rm', source, target], { timeout: 15_000 });
  writeFileSync(`${directory}/evidence.json`, JSON.stringify(evidence, null, 2));
}
