import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const gitBash = path.join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe');
const bash = process.platform === 'win32' && existsSync(gitBash) ? gitBash : 'bash';

function runLegacyGenerator(status, args = []) {
  const directory = mkdtempSync(path.join(tmpdir(), 'conduit-legacy-openapi-'));
  try {
    const scripts = path.join(directory, 'repository with spaces', 'tools', 'openapi');
    const bin = path.join(directory, 'stub executables');
    const unrelatedCwd = path.join(directory, 'unrelated directory');
    const capture = path.join(directory, 'npm-arguments');
    mkdirSync(scripts, { recursive: true });
    mkdirSync(bin);
    mkdirSync(unrelatedCwd);
    const entrypoint = path.join(scripts, 'generate-openapi-from-build.sh');
    copyFileSync(path.join(scriptsDirectory, 'generate-openapi-from-build.sh'), entrypoint);
    writeFileSync(path.join(bin, 'npm'), `#!/usr/bin/env bash
cwd="$(pwd -W 2>/dev/null || pwd)"
printf '%s\\0' "$cwd" "$@" > "$LEGACY_NPM_LOG"
exit "$LEGACY_NPM_STATUS"
`, { mode: 0o755 });

    // Let Bash resolve its own PATH syntax, including Git Bash's /c/... paths.
    const result = spawnSync(bash, [
      '-c',
      'export PATH="$(cd "$1" && pwd):$PATH"; export LEGACY_NPM_LOG="$2"; export LEGACY_NPM_STATUS="$3"; exec bash "$4" "${@:5}"',
      '--', bin, capture, String(status), entrypoint, ...args,
    ], { cwd: unrelatedCwd, encoding: 'utf8' });
    assert.ifError(result.error);
    assert.ok(existsSync(capture), `npm was not invoked: ${result.stderr}`);
    const [cwd, ...npmArgs] = readFileSync(capture, 'utf8').split('\0').slice(0, -1);
    assert.equal(realpathSync.native(cwd), realpathSync.native(scripts));
    assert.deepEqual(npmArgs, ['run', 'generate:offline', '--', ...args]);
    return result;
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

test('legacy entry point delegates to offline generation from any working directory', () => {
  const result = runLegacyGenerator(0, ['argument with spaces']);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stderr, /deprecated.*generate:offline/);
});

test('legacy entry point propagates generation failures without claiming success', () => {
  const result = runLegacyGenerator(23);
  assert.equal(result.status, 23, result.stderr);
  assert.doesNotMatch(result.stdout, /success/i);
});
