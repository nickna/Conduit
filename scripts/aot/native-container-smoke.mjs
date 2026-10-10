import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import assert from 'node:assert/strict';

const [image, service] = process.argv.slice(2);
assert.ok(image && ['admin', 'http'].includes(service), 'Usage: native-container-smoke.mjs IMAGE admin|http');
const output = resolve(`artifacts/native-containers/${service}`);
mkdirSync(output, { recursive: true });
const docker = args => execFileSync('docker', args, { encoding: 'utf8', timeout: 120_000 }).trim();
const identity = JSON.parse(docker(['image', 'inspect', image]))[0];
let container;
const evidence = { image, id: identity.Id, digests: identity.RepoDigests, user: identity.Config.User };
try {
  // Exercise the actual non-root entrypoint and packaged libraries, without a host SDK.
  container = docker(['create', '--env', 'CONDUIT_OPENAPI_GENERATION=true',
    '--env', 'CONDUIT_OPENAPI_OUTPUT=/tmp/packaged-openapi.json', image]);
  docker(['start', container]);
  assert.equal(docker(['wait', container]), '0', 'Native packaged entrypoint must exit successfully');
  docker(['cp', `${container}:/tmp/packaged-openapi.json`, `${output}/openapi.json`]);
  const contract = JSON.parse(readFileSync(`${output}/openapi.json`));
  assert.ok(contract.openapi && Object.keys(contract.paths ?? {}).length > 0, 'Packaged entrypoint must write a real contract');
  evidence.paths = Object.keys(contract.paths).length;
  evidence.succeeded = true;
} catch (error) {
  evidence.error = error.message;
  throw error;
} finally {
  if (container) {
    const logs = spawnSync('docker', ['logs', container], { encoding: 'utf8', timeout: 15_000 });
    writeFileSync(`${output}/container.log`, (logs.stdout ?? '') + (logs.stderr ?? ''));
    docker(['rm', '--force', container]);
  }
  writeFileSync(`${output}/evidence.json`, JSON.stringify(evidence, null, 2));
}
