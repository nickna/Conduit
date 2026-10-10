import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync, writeFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { imageSet, preflight, promote } from './image-promotion.mjs';

const [command, input, output] = process.argv.slice(2);
const read = path => JSON.parse(readFileSync(path, 'utf8'));
const save = (path, value) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, JSON.stringify(value, null, 2) + '\n'); };
function docker(args) { return execFileSync('docker', args, { encoding: 'utf8', timeout: 180_000, stdio: ['ignore', 'pipe', 'pipe'] }).trim(); }
const registry = {
  async digest(ref) {
    try { return JSON.parse(docker(['buildx', 'imagetools', 'inspect', ref, '--format', '{{json .Manifest}}'])).digest; }
    catch (error) {
      if (/manifest unknown|not found|no such manifest/i.test(error.stderr?.toString() ?? '')) return null;
      throw error; // Authentication, network and registry errors are never treated as missing tags.
    }
  },
  async labels(ref) {
    docker(['pull', ref]);
    return JSON.parse(docker(['image', 'inspect', ref, '--format', '{{json .Config.Labels}}'])) ?? {};
  },
  async copy(source, target) { docker(['buildx', 'imagetools', 'create', '--prefer-index=false', '--tag', target, source]); }
};
if (command === 'record') {
  const [service, image, digest, version, sha] = process.argv.slice(3);
  save(`artifacts/candidates/${service}.json`, { service, image, digest, version, sha });
} else if (command === 'assemble') {
  const set = imageSet(readdirSync(input).filter(p => p.endsWith('.json')).map(p => read(join(input, p))));
  save(output, set);
  if (process.env.GITHUB_OUTPUT) for (const image of set.images)
    appendFileSync(process.env.GITHUB_OUTPUT, `${image.service}=${image.image}@${image.digest}\n`);
} else if (command === 'preflight') {
  save(output, await preflight(read(input), registry));
} else if (command === 'migrated') {
  const ledger = read(input); ledger.migrationSucceeded = true; save(input, ledger);
} else if (command === 'promote' || command === 'recover') {
  await promote(read(input), registry, ledger => save(input, ledger));
} else throw new Error('Expected record, assemble, preflight, migrated, promote or recover');
