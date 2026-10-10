import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
import { mkdirSync, writeFileSync } from 'node:fs';
import { warningKeys, enforceWarnings } from './redocly-warning-policy.mjs';
const directory = dirname(fileURLToPath(import.meta.url));
if (process.argv.length > 2) throw new Error('lint:openapi does not accept baseline or suppression arguments');
const root = resolve(directory, '../..'), output = resolve(root, 'artifacts/openapi-lint');
mkdirSync(output, { recursive: true });
const require = createRequire(import.meta.url);
const cli = resolve(dirname(require.resolve('@redocly/cli/package.json')), 'bin/cli.js');
const reports = [];
for (const [service, file] of [['admin', 'Services/ConduitLLM.Admin/openapi-admin.json'], ['gateway', 'Services/ConduitLLM.Gateway/openapi-gateway.json']]) {
  let text;
  // max-problems controls output, never exit status. Capture the complete report;
  // warning-policy rejects every warning, including warnings from future rules.
  try { text = execFileSync(process.execPath, [cli, 'lint', '--format=json', '--max-problems=1000000', '--config', resolve(root, 'redocly.yaml'), resolve(root, file)],
    { encoding: 'utf8', timeout: 120_000, stdio: ['ignore', 'pipe', 'pipe'] }); }
  catch (error) { if (error.stdout) writeFileSync(`${output}/${service}.json`, error.stdout); throw error; }
  writeFileSync(`${output}/${service}.json`, text);
  const report = JSON.parse(text);
  if (report.totals?.ignored !== (service === 'admin' ? 1 : 0))
    throw new Error(`Unexpected ignored Redocly findings for ${service}`);
  reports.push(report);
}
console.log(enforceWarnings(warningKeys(reports)));
