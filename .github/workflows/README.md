# GitHub Actions Workflows

This repository uses a simplified, industry-standard CI/CD pipeline.

## Active Workflows

### 1. CI (`ci.yml`)
**Triggers:** Push to `master` or `dev`, Pull requests to `master`

**What it does:**
- Validates code builds and tests pass (.NET and WebAdmin lint/type-check)
- Runs the analyzer-only NativeAOT warning ratchet on every PR without native linking
- Builds all three Docker images for validation only — **never pushes** (this still
  catches Dockerfile / production-build breakage, notably WebAdmin's `next build`,
  which runs nowhere else in CI)

CI publishes nothing. Docker images are published from a `v*` tag by the Release workflow.

### 2. NativeAOT (`native-aot.yml`)

**Triggers:** Push to `master`, weekly schedule, manual dispatch

**What it does:**
- Publishes Admin and Gateway as `linux-x64` NativeAOT executables
- Launches each executable through the infrastructure-free OpenAPI path
- Builds both parallel NativeAOT runtime images and verifies that they are non-root,
  directly executable, health checked, and free of build/debug content
- Retains publish duration, executable size, OpenAPI readiness, peak working set,
  exact publish/smoke logs, runtime artifacts, and separately packaged symbols

Native smoke failures are recorded but do not prevent artifact retention while the
later NativeAOT phases burn down the known runtime incompatibilities.

### 3. Release (`release.yml`)
**Triggers:** Push of a tag matching `v*` (cut from `master`)

Two channels, decided by the tag name — a tag is a **pre-release** iff its name
contains a hyphen (SemVer rule):

| Tag | Channel | Docker | GitHub Release |
|---|---|---|---|
| `v3.0.0` | stable | `:3.0.0` + `:latest` | Latest |
| `v3.0.0-beta.1` | beta | `:3.0.0-beta.1` + `:beta` | Pre-release |

**What it does:**
- Creates a GitHub Release with auto-generated notes (pre-release for beta tags;
  only a stable tag becomes the repo's "Latest")
- Builds and pushes the three versioned Docker images plus the channel tag
  (`:latest` / `:beta`)
- Independently builds `linux-x64` Admin and Gateway NativeAOT canary candidates with
  SBOM/provenance, a blocking critical-vulnerability scan, and separate short-lived
  symbol artifacts. These candidates are deliberately excluded from JIT promotion.
The Git tag drives the GitHub release and Docker image versions.

Native candidate promotion is governed by
`docs/operations/native-aot-canary.md` and remains blocked by ADR 0006. No native
candidate receives a version, `latest`, or `beta` tag from this workflow.

### 4. CodeQL (`codeql-analysis.yml`)
**Triggers:** Push to `master` or `dev`, Weekly schedule, Manual dispatch

**What it does:**
- Scans for security vulnerabilities
- Results appear in Security tab
- Non-blocking, informational only

### 5. Close dev issues (`close-dev-issues.yml`)
**Triggers:** Push to `dev`, Manual dispatch

GitHub only honours `Closes #N` for PRs merged into the **default** branch (`master`).
Every PR here targets `dev`, so those references never fire and completed issues sit
open until someone closes them by hand. This workflow closes them.

**What it does:**
- Finds every merged PR in the pushed commit range
- Reads closing keywords (`close[sd]`, `fix(e[sd])`, `resolve[sd]`) from each PR's title
  and body, including the comma/`and`-separated list form used here
  (`Closes #1205, #1206, #1207.`) that GitHub only ever honours for the first number
- Closes each referenced open issue with a comment pointing at the PR and merge commit

**What it deliberately does not close:**
- References without a closing keyword — `Advances #1182`, `Completes epic #1204`,
  `part of #800`. Partial progress stays open; write a closing keyword only when the
  issue is actually done.
- Issues already closed, PR numbers, and numbers inside words (`prefixes #99`)

**Backfilling:** run it manually with a PR number to process an older merged PR.
`dry_run` defaults to **true** on manual runs — it lists what it would close without
closing anything. Set it to false to act.

## Release Process

Releases are cut from `master` by pushing a version tag.

1. **Bump the version** in `Directory.Build.props`, commit, and merge to `master`.
2. **Stable release:**
   ```bash
   git tag v3.0.0
   git push origin v3.0.0
   ```
   Publishes Docker `:3.0.0` + `:latest`.
3. **Beta release** — any pre-release suffix (a `-…`):
   ```bash
   git tag v3.0.0-beta.1
   git push origin v3.0.0-beta.1
   ```
   Publishes Docker `:3.0.0-beta.1` + `:beta`.
   A beta never moves `:latest`.

## Artifact Locations

- **Docker Images:** https://github.com/users/nickna/packages
- **Security Results:** https://github.com/nickna/Conduit/security/code-scanning

## Design Principles

1. **YAGNI (You Ain't Gonna Need It):** Only essential workflows
2. **DRY (Don't Repeat Yourself):** No duplicate logic across workflows
3. **Industry Standard:** Using official actions, no custom parsing
4. **Simple:** ~300 lines total vs previous 2,187 lines

## Required Secrets

- `GITHUB_TOKEN`: Automatically provided by GitHub Actions

## Archived Workflows

Old workflows are archived in `.github/workflows/archive-2024-08/` for reference.
These were replaced due to:
- Overcomplexity (2,187 lines of YAML)
- Custom SARIF parsing that broke with format changes
- Duplicate logic across multiple workflows
- Manual security gating that failed silently
