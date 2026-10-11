import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const [image, service, artifactDirectory] = process.argv.slice(2);
assert.ok(process.argv.length >= 4 && process.argv.length <= 5 && image &&
  image.length <= 4096 && !image.startsWith('-') && !/[\x00-\x1f\x7f]/.test(image) &&
  ['admin', 'http', 'gateway'].includes(service),
  'Usage: native-container-smoke.mjs IMAGE admin|http|gateway [ARTIFACT_DIRECTORY]');

const script = resolve(dirname(fileURLToPath(import.meta.url)), 'native-image-entrypoint.ps1');
const args = ['-NoLogo', '-NoProfile', '-File', script, '-Image', image, '-Service', service];
if (artifactDirectory !== undefined) args.push('-ArtifactDirectory', resolve(artifactDirectory));
// The driver bounds its Docker commands and independently cleans owned containers.
// Let it finish cleanup before this adapter propagates its final exit status.
const result = spawnSync('pwsh', args, { stdio: 'inherit', windowsHide: true, shell: false });
if (result.error) console.error('Native image entrypoint driver could not start.');
process.exitCode = !result.error && result.status === 0 ? 0 : 1;
