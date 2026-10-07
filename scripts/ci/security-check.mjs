import { readFileSync, writeFileSync } from 'node:fs';
import { npmFindings, nugetFindings, imageFindings, enforce } from './security-policy.mjs';
const read = path => JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));
const [mode, ...paths] = process.argv.slice(2);
const exceptions = read('scripts/ci/security-exceptions.json');
let findings;
if (mode === 'dependency') {
  findings = nugetFindings(read('artifacts/security/nuget.json'));
  for (const scope of ['WebAdmin', 'tools/openapi', 'tools/ci'])
    findings.push(...npmFindings(read(`artifacts/security/${scope.replaceAll('/', '-')}.json`), read(`${scope}/package-lock.json`), scope));
} else if (mode === 'image') {
  findings = paths.flatMap(path => imageFindings(read(path), 'release-image'));
} else throw new Error('Expected dependency or image');
writeFileSync(`artifacts/security/${mode}-findings.json`, JSON.stringify(findings, null, 2));
console.log(enforce(findings, exceptions, mode));
