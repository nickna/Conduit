# Refreshing scoped npm overrides

Issue [#1486](https://github.com/nickna/Conduit/issues/1486) tracks a successful
`npm install --package-lock-only` that leaves a newly overridden nested dependency
unchanged. Reproduced on Windows with Node 22.23.2 and both npm 10.9.8 and npm 12.2.0
on 2026-10-07. Updating npm alone does not fix this reproduction.

## Reduced upstream reproduction

From the repository root:

```powershell
pwsh scripts/test/reproduce-npm-scoped-override.ps1
```

The script creates a fresh ignored directory under `artifacts/npm-override-repro`,
with only these development dependencies:

```json
{
  "@istanbuljs/load-nyc-config": "1.1.0",
  "js-yaml": "4.3.2"
}
```

It generates a lockfile without installing packages, then adds
`"overrides": { "@istanbuljs/load-nyc-config": { "js-yaml": "4.3.2" } }`
and repeats the successful lockfile-only command. The existing hoisted js-yaml 4
is necessary to reproduce the stale nested branch; a loader-only example refreshes
correctly with npm 10.9.8. No node_modules or hidden lockfile is present.

Expected: the loader resolves js-yaml 4.3.2 and its obsolete branch is pruned.
Actual: the nested js-yaml 3.15.2 and sprintf-js 1.0.3 remain despite exit code zero.
`reproduction.json` records the runtime versions, retained versions and whether
the bug reproduced; both command logs and the original lockfile are retained.
The files and commands are suitable for an upstream report without application
code or credentials. To test another locally downloaded npm CLI, pass
`-NpmCli <absolute-path-to-npm/bin/npm-cli.js>`; the script leaves the global npm
installation unchanged. A future fixed npm produces `reproduced: false`.

## Verified refresh for WebAdmin's coverage parser

The repository's scoped override is already applied. Use this procedure only when
reapplying it to a stale lockfile, and review the resulting diff. Do not regenerate
the entire lockfile: unrelated dependencies must retain their versions and integrity.

From `WebAdmin`, first run the lockfile check to demonstrate the inconsistency:

```powershell
node scripts/check-dependency-overrides.mjs
```

Invalidate only the known obsolete nested parser record. The command refuses to
modify a different version or a lockfile lacking the maintained hoisted parser:

```powershell
node --input-type=module -e 'import fs from "node:fs"; const file="package-lock.json"; const lock=JSON.parse(fs.readFileSync(file,"utf8")); const stale="node_modules/@istanbuljs/load-nyc-config/node_modules/js-yaml"; if(lock.packages[stale]?.version!=="3.15.2" || lock.packages["node_modules/js-yaml"]?.version!=="4.3.2") throw new Error("Unexpected graph; review before refreshing"); delete lock.packages[stale]; fs.writeFileSync(file,JSON.stringify(lock,null,2)+"\n");'
npm install --package-lock-only --ignore-scripts --no-audit --no-fund
node scripts/check-dependency-overrides.mjs
npm ci --ignore-scripts --no-audit --no-fund
npm run check:dependency-overrides
npm ls js-yaml --all
node --test scripts/coverage-config.test.mjs scripts/dependency-overrides.test.mjs
```

npm reconciles the missing node against the override and prunes the orphaned
argparse 1 / sprintf-js / esprima branch. The check resolves js-yaml from every
locked coverage loader's own ancestors, so a correct hoisted copy cannot conceal
a stale nested one. It also rejects leftover sprintf-js records. The installed
check resolves the actual parser from the loader after clean `npm ci`; the loader
fixtures exercise YAML and JSON configuration behavior.

Validation against WebAdmin's historical pre-override lockfile at `25e0d72b`
removed exactly the nested js-yaml and argparse records plus sprintf-js and
esprima. Every retained package record was unchanged, and the result matched the
current checked-in package graph exactly.

Run WebAdmin lint, type checks, API boundary checks, hygiene and full Jest coverage,
then all three npm audits (`WebAdmin`, `tools/openapi`, `tools/ci`) before committing.
A successful lockfile-only command or a correct root parser version is insufficient
evidence that the scoped override took effect. CI checks the lockfile before audits
and the installed consumer in WebAdmin correctness.
