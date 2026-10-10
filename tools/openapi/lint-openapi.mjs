import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { warningKeys, enforceWarnings } from './redocly-warning-policy.mjs';
const directory = dirname(fileURLToPath(import.meta.url));
const root = resolve(directory, '../..'), output = resolve(root, 'artifacts/openapi-lint');
mkdirSync(output, { recursive: true });
const require = createRequire(import.meta.url);
const cli = resolve(dirname(require.resolve('@redocly/cli/package.json')), 'bin/cli.js');
const reports = [];
for (const [service, file] of [['admin', 'Services/ConduitLLM.Admin/openapi-admin.json'], ['gateway', 'Services/ConduitLLM.Gateway/openapi-gateway.json']]) {
  let text;
  // max-problems controls report truncation, never exit status. Capture all debt;
  // warning-policy rejects a truncated inventory and supplies the blocking rule.
  try { text = execFileSync(process.execPath, [cli, 'lint', '--format=json', '--max-problems=1000000', '--config', resolve(root, 'redocly.yaml'), resolve(root, file)],
    { encoding: 'utf8', timeout: 120_000, stdio: ['ignore', 'pipe', 'pipe'] }); }
  catch (error) { if (error.stdout) writeFileSync(`${output}/${service}.json`, error.stdout); throw error; }
  writeFileSync(`${output}/${service}.json`, text); reports.push(JSON.parse(text));
}
const keys = warningKeys(reports), baseline = resolve(directory, 'redocly-warning-baseline.json');
if (process.argv.includes('--write-baseline')) writeFileSync(baseline, JSON.stringify(keys, null, 2) + '\n');
console.log(enforceWarnings(keys, JSON.parse(readFileSync(baseline, 'utf8'))));
