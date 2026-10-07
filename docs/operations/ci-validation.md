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

Migration verification runs for every CI revision, including tooling/dependency-only
and documentation changes. Required mode never substitutes filesystem enumeration
for EF's migration inventory (#1455). EF inventory and pending-model subprocesses
have 30-second limits with process-tree termination; nonzero exits, missing tools,
empty inventory, and timeouts fail. Pending-model output and migration TRX are
retained even on failure. The complete workflow is bounded to 30 minutes. A
configured PostgreSQL database must be reachable; only local runs with no database
configured may skip. CI explicitly promises the infrastructure.

All active workflows default to `contents: read`, as do repository Actions tokens.
Workflow tokens cannot approve PR reviews. CodeQL alone receives security-event
write access; candidate/promotion jobs receive package write; the release job
receives content write; merged-dev issue automation receives issue write and PR
read. These intentional exceptions are scoped to their jobs/workflow.

External actions use reviewed immutable revisions in `action-versions.json`, with
human-readable version comments. Checkout/setup/cache/artifact/Docker actions now
use their Node 24 releases, and CodeQL uses v4. Resolve updates from upstream tag
commits, inspect action runtime metadata, update the inventory and workflow pins
together, then run the pin regression test and actionlint. Group action updates
under #1063's dependency automation ownership; archived workflows are inactive.

All 23 formerly excluded `TimingSensitive` methods now run in ordinary CI. Ten
calculate supplied durations; eleven test streaming metrics with FakeTimeProvider;
two test invalidation dispatch with controlled timers/explicit completion. The
checked-in timing inventory guards every method, including theory discovery.
Liveness waits have generous ten-second bounds and never assert machine speed.
The restored suite also fixes first-token timestamp zero losing an interval (#1458).

MediaGallery's ordinary Jest tests assert rendered cards, virtualization decisions,
updates, and empty states without wall-clock thresholds. The unused all-tests
performance Jest configuration is retired. Actual browser timings belong to the
packaged browser lane; unit/jsdom timings are not rendering-performance evidence.
Relative test globs fix Windows `.codex` worktree discovery (#1457).

Every required .NET lane uses the shared run-tests action: TRX, method identities,
discovered/executed/passed/failed/skipped counts, execution logs, and requested
coverage are uploaded for 30 days even on failure. Empty execution and unexpected
skips fail. Core's measured minimum is 3,752 executed cases; its six intentional
skips are the three S3 replacements pending CI-07, two unsupported concurrent
tracker tests, and the local tokenizer baseline emitter. Billing invariants allow
one baseline-emitter skip. Other required filtered lanes allow zero skips.
WebAdmin retains Jest JSON and coverage; fewer than 461 executed cases, any failure,
or any skipped correctness test fails the gate.

`coverage-baseline.json` records component minima measured October 6, 2026 from
the complete core and Jest suites. .NET uses executable-line coverage; Jest uses
statement coverage. Minima round measured values down to whole percentage points;
there is no unmeasured global threshold. Critical refund, master-key, metrics,
gallery, media guards, and schema conversion components must not regress or vanish.
Job summaries publish measured values and minima. Raise baselines when coverage
improves; reductions require an explained review alongside executable evidence.
The explicit `.runsettings` removes broad Program/Startup/test-name exclusions;
migrations and build/generated outputs remain excluded. Jest excludes generated
contract types, which have separate generation/drift validation. The unused
`coverlet.json` threshold is retired.
