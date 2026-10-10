#!/usr/bin/env node
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

import { generateToDirectory } from './generate-openapi-offline.mjs';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptsDir, '..', '..');
const generatedFiles = [
  'Services/ConduitLLM.Admin/openapi-admin.json',
  'Services/ConduitLLM.Gateway/openapi-gateway.json',
  'WebAdmin/src/generated/admin-api.ts',
  'WebAdmin/src/generated/gateway-api.ts',
];

export function compareGeneratedArtifacts(repositoryRoot, files, first, second) {
  const unstable = [];
  const stale = [];
  for (const file of files) {
    const destination = path.join(repositoryRoot, file);
    const generated = readFileSync(first.get(destination));
    if (!generated.equals(readFileSync(second.get(destination)))) unstable.push(file);
    if (!existsSync(destination) || !generated.equals(readFileSync(destination))) stale.push(file);
  }
  return { unstable, stale };
}

export function verify() {
  const workDirectory = mkdtempSync(path.join(tmpdir(), 'conduit-openapi-determinism-'));
  try {
    const first = generateToDirectory('all', path.join(workDirectory, 'first'));
    const second = generateToDirectory('all', path.join(workDirectory, 'second'));
    const { unstable, stale } = compareGeneratedArtifacts(repoRoot, generatedFiles, first, second);

    if (unstable.length > 0) {
      console.error(`OpenAPI generation is not byte-stable: ${unstable.join(', ')}`);
    }
    if (stale.length > 0) {
      console.error(`Committed OpenAPI artifacts are stale: ${stale.join(', ')}. Run npm run generate:offline.`);
    }
    if (unstable.length > 0 || stale.length > 0) {
      process.exitCode = 1;
    } else {
      console.log('OpenAPI contracts and WebAdmin types match committed files and are byte-identical across two isolated generations.');
    }
  } finally {
    rmSync(workDirectory, { recursive: true, force: true });
  }
}

const invokedDirectly =
  process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (invokedDirectly) verify();
