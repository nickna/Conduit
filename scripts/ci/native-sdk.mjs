import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function requireCompatibleSdk(global, dockerfiles) {
  if (global.sdk.rollForward !== 'latestFeature' || global.sdk.allowPrerelease !== false) {
    throw new Error('SDK guard expects the documented stable latestFeature policy');
  }
  const minimum = global.sdk.version.split('.').map(Number);
  for (const [name, content] of Object.entries(dockerfiles)) {
    const match = /^FROM mcr\.microsoft\.com\/dotnet\/sdk:(\d+)\.(\d+)\.(\d+)-noble AS build\s*$/m.exec(content);
    if (!match) throw new Error(`${name}: expected a pinned stable noble SDK build stage`);
    const installed = match.slice(1).map(Number);
    if (installed[0] !== minimum[0] || installed[1] !== minimum[1] || installed[2] < minimum[2]) {
      throw new Error(`${name}: SDK ${installed.join('.')} cannot satisfy global.json ${global.sdk.version}`);
    }
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const files = ['Services/ConduitLLM.Admin/Dockerfile.native', 'Services/ConduitLLM.Gateway/Dockerfile.native'];
  requireCompatibleSdk(JSON.parse(readFileSync('global.json')), Object.fromEntries(files.map(path => [path, readFileSync(path, 'utf8')])));
  console.log('Both native Docker SDK pins satisfy global.json.');
}
