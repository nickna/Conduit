import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const configuration = JSON.parse(readFileSync('scripts/ci/actionlint-config.json', 'utf8'));
const installerSupported = process.platform === 'win32' || (process.platform === 'linux' && process.arch === 'x64');
const archiveName = version => `actionlint_${version}_${process.platform === 'win32' ? 'windows_amd64.zip' : 'linux_amd64.tar.gz'}`;

test('the actionlint manifest pins exactly the supported versioned archives', () => {
  assert.match(configuration.version, /^\d+\.\d+\.\d+$/);
  assert.deepEqual(Object.keys(configuration.archives).sort(), [
    `actionlint_${configuration.version}_linux_amd64.tar.gz`,
    `actionlint_${configuration.version}_windows_amd64.zip`
  ]);
  for (const checksum of Object.values(configuration.archives)) {
    assert.equal(typeof checksum, 'string');
    assert.match(checksum, /^[a-f0-9]{64}$/);
  }
});

function fixture(mutate, inspect, cachedArchive = false) {
  const root = mkdtempSync(join(tmpdir(), 'conduit-actionlint-config-'));
  try {
    const config = structuredClone(configuration);
    mutate(config);
    mkdirSync(join(root, 'scripts', 'ci'), { recursive: true });
    mkdirSync(join(root, 'scripts', 'test'), { recursive: true });
    writeFileSync(join(root, 'scripts', 'ci', 'actionlint-config.json'), JSON.stringify(config));
    copyFileSync('scripts/test/validate-workflows.ps1', join(root, 'scripts', 'test', 'validate-workflows.ps1'));
    // A regression that reaches download must fail locally without accessing GitHub.
    writeFileSync(join(root, 'invoke-validator.ps1'), `$ErrorActionPreference = 'Stop'
function Invoke-WebRequest {
    param([string]$Uri, [string]$OutFile, [int]$TimeoutSec)
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'download-attempted') -Value $Uri
    throw 'Fixture attempted an actionlint download'
}
try {
    & (Join-Path $PSScriptRoot 'scripts/test/validate-workflows.ps1')
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
`);
    if (cachedArchive) {
      const directory = join(root, 'artifacts', 'tools', 'actionlint');
      mkdirSync(directory, { recursive: true });
      writeFileSync(join(directory, archiveName(config.version)), 'corrupt cached actionlint archive');
    }
    const result = spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', join(root, 'invoke-validator.ps1')],
      { cwd: root, encoding: 'utf8', timeout: 15_000 });
    assert.ifError(result.error);
    assert.equal(result.signal, null, 'The validator must finish normally');
    assert.equal(result.status, 1, `Expected validator rejection:\n${result.stdout}${result.stderr}`);
    inspect({ root, config, diagnostics: result.stdout + result.stderr });
    assert.equal(existsSync(join(root, 'download-attempted')), false, 'The validator must reject the fixture without downloading');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

for (const [name, mutate] of [
  ['missing archive checksum', config => { delete config.archives[archiveName(config.version)]; }],
  ['invalid archive checksum', config => { config.archives[archiveName(config.version)] = 'invalid'; }],
  ['uppercase archive checksum', config => { config.archives[archiveName(config.version)] = 'A'.repeat(64); }],
  ['version changed without matching archive pins', config => { config.version = '9999.0.0'; }]
]) {
  test(`the validator rejects ${name} before creating directories or downloading`, { skip: !installerSupported }, () => {
    fixture(mutate, ({ root, config, diagnostics }) => {
      assert.ok(diagnostics.includes(`Missing or invalid reviewed SHA-256 for actionlint archive: ${archiveName(config.version)}`), diagnostics);
      assert.equal(existsSync(join(root, 'artifacts')), false, 'Invalid archive pins must fail before creating installer directories');
    });
  });
}

test('the validator rejects a corrupt cached archive before extraction', { skip: !installerSupported }, () => {
  fixture(() => {}, ({ root, diagnostics }) => {
    assert.match(diagnostics, /actionlint archive checksum mismatch/);
    assert.deepEqual(readdirSync(join(root, 'artifacts', 'tools', 'actionlint', 'bin')), [], 'A mismatched archive must not be extracted');
  }, true);
});
