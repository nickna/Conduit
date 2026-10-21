import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { parseWorkflow } from './workflow-policy.mjs';

const workflow = parseWorkflow(readFileSync('.github/workflows/native-aot.yml', 'utf8'));
const paths = workflow.on.pull_request.paths;

// The workflow uses only literal paths, * (one path segment), and ** (any depth).
// Keep this matcher restricted to that subset so a new pattern cannot silently
// make the impact regression accept an unsupported interpretation.
function matches(pattern, path) {
  assert.match(pattern, /^[\w./*\-]+$/, `Unsupported NativeAOT path pattern: ${pattern}`);
  const expression = pattern.split(/(\*\*\/|\*\*|\*)/).map(part => {
    if (part === '**/') return '(?:.*/)?';
    if (part === '**') return '.*';
    if (part === '*') return '[^/]*';
    return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }).join('');
  return new RegExp(`^${expression}$`).test(path);
}

function routesNative(path) { return paths.some(pattern => matches(pattern, path)); }

test('native artifact, tool, test, and policy inputs trigger NativeAOT validation', () => {
  for (const path of [
    'global.json',
    'NuGet.Config',
    'Directory.Build.props',
    'Directory.Build.targets',
    'Directory.Packages.props',
    'Conduit.slnx',
    '.dockerignore',
    'Shared/ConduitLLM.Configuration/ConduitLLM.Configuration.csproj',
    'Shared/ConduitLLM.Configuration/Messaging/Wolverine/Wolverine.NativeAot.rd.xml',
    'Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj',
    'Services/ConduitLLM.Gateway/Program.cs',
    'Services/ConduitLLM.Admin/Dockerfile.native',
    'Services/ConduitLLM.Gateway/Dockerfile.native',
    'Services/ConduitLLM.Admin/NativeAot.rd.xml',
    'Services/ConduitLLM.Gateway/NativeAot.rd.xml',
    'Tests/Directory.Build.props',
    'Tests/ConduitLLM.GatewayNativeAotTests/Program.cs',
    'Tests/ConduitLLM.Tests/Architecture/ConfigurationContractBoundaryTests.cs',
    'Tests/ConduitLLM.PersistenceAotTests/Program.cs',
    'Tests/ConduitLLM.IntegrationTests/ConduitLLM.IntegrationTests.csproj',
    'Tests/ConduitLLM.IntegrationTests/Tests/ProviderPersistenceParityTests.cs',
    'tools/ConduitLLM.HealthProbe/Program.cs',
    'tools/ConduitLLM.MediaDispatchProbe/ConduitLLM.MediaDispatchProbe.csproj',
    'tools/ConduitLLM.Migrator/ConduitLLM.Migrator.csproj',
    'tools/WolverineSmokePublisher/WolverineSmokePublisher.csproj',
    'scripts/aot/publish-native.ps1',
    'scripts/architecture/dependency-boundaries.targets',
    'scripts/architecture/dependency_graph.py',
    'scripts/architecture/project-edges.json',
    'scripts/aot/linker-warning-baseline.json',
    'scripts/db/providers/provider-config.json',
    'scripts/db/providers/openrouter-models.json',
    'scripts/ci/run-tests.ps1',
    'scripts/ci/test-evidence.py',
    'scripts/test/wolverine-two-host-smoke.ps1',
    'deploy/native-canary/promotion-policy.json',
    '.github/actions/run-tests/action.yml',
    '.github/workflows/native-aot.yml',
  ]) {
    assert.ok(existsSync(path), `Stale NativeAOT input fixture: ${path}`);
    assert.ok(routesNative(path), `${path} must trigger NativeAOT validation`);
  }
});

test('unrelated frontend and documentation changes keep NativeAOT selective', () => {
  for (const path of ['WebAdmin/package.json', 'WebAdmin/app/page.tsx', 'docs/operations/native-aot-audit.md', 'README.md']) {
    assert.equal(routesNative(path), false, `${path} should not trigger NativeAOT validation`);
  }
});

test('native glob matching handles nested source paths and root-only inputs', () => {
  assert.ok(matches('Services/**/*.cs', 'Services/ConduitLLM.Admin/Endpoints/HealthEndpoints.cs'));
  assert.ok(matches('Services/**/*.cs', 'Services/Program.cs'));
  assert.equal(matches('Directory.*', 'WebAdmin/Directory.Build.props'), false);
  assert.equal(matches('scripts/db/providers/*.json', 'scripts/db/providers/nested/catalog.json'), false);
});
