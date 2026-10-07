# CI and release validation

`CI required` is the sole required merge context for master and dev. CI invokes
migration, PostgreSQL 16/17 lock, and CodeQL validation as reusable workflows, so
their results belong to the same run and exact revision. It runs on every PR and
branch push, including documentation changes; no path-filtered required context
can remain pending. Failed, cancelled, missing, and skipped required jobs fail
the aggregate. Its job inventory is `scripts/ci/required-jobs.json`.

`scripts/ci/configure-protection.ps1 -Apply` installs the reviewed policy in
`scripts/ci/branch-protection.json` on both branches. The policy requires an
up-to-date PR with a successful GitHub Actions aggregate, resolved conversations,
and no direct push, administrator bypass, force push, or deletion. A PR is
required; additional human approvals are optional for this single-maintainer
repository. No bypass actors are configured. Read the effective settings by
running the script without `-Apply`.

Run `node --test scripts/ci/*.test.mjs` to check the validation policy, including
deliberately failed, cancelled, skipped, and missing results. Live workflow and
protection verification is recorded with the implementation PR.

Releases accept `vMAJOR.MINOR.PATCH` and SemVer prerelease suffixes (beta channel).
Build metadata is intentionally unsupported because `+` is invalid in Docker tags.
Before building candidates or accessing production, the tagged commit must be an
ancestor of current master and have a completed successful **master push** CI run
for that exact SHA, including `CI required`. PR merge revisions and dev runs do
not qualify. Missing/pending runs wait at most ten minutes; failed, cancelled,
skipped, or timed-out validation fails immediately. GitHub API errors fail closed.
Production's environment policy permits only `v*` tags; ancestry/version/CI policy
is enforced by the release job, with no added human approval requirement.

The native SDK drift was already corrected to `10.0.401-noble` before this branch.
The fast `native-sdk.mjs` guard now rejects incompatible pins on every PR, including
the original `10.0.302` regression. Update global.json's minimum stable SDK first,
then both native Docker SDK pins to that version or a newer patch/feature band of
the same major/minor. Keep stable `latestFeature` roll-forward. Setup-dotnet selects
the latest stable 10.0 SDK; global.json enforces the minimum on runners and in images.
Native linking and both image builds run on master pushes, weekly/manual runs, and
PRs changing native Dockerfiles, SDK/build policy, native scripts, or that workflow.
Analyzer checks remain required for every PR. Native images remain canaries.

The main test project owns self-contained tests irrespective of class names. Its
ordinary selector excludes only explicitly owned `ReleaseMigration` and
`TimingSensitive` traits. Release migrations belong to the mandatory migration
workflow. Billing audit (8), refund (7), and temporary-master-key (4) methods run
in the main lane; `functional-inventory.json` and `verify-functional-results.ps1`
fail when any promised suite disappears from executed results. These tests use
SQLite/TestServer and need neither provider credentials nor developer services.
