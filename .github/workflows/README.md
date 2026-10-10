# GitHub Actions workflows

The protected `CI required` check must succeed on every pull request and master/dev push, including documentation-only changes. Local validation: `npm ci --prefix tools/ci`, then `pwsh scripts/test/validate-workflows.ps1` and `node --test scripts/ci/*.test.mjs`. The validator verifies its actionlint download by SHA-256, validates real Actions syntax/expressions, and parses the required job graph. Archived workflows are inactive.

| Workflow | Active triggers | Executed proof |
|---|---|---|
| CI (`ci.yml`) | master/dev push, master/dev PR, manual | Sixteen required job groups: backend correctness/coverage, functional and deterministic timing inventories, serialization, billing fault/invariants, durable media, SignalR, Wolverine two-host, analyzers, contracts/official SDKs, WebAdmin correctness/CSS/dead code, dependency audits, real S3 storage, packaged production images/Chromium, plus reusable migrations, locks and CodeQL. |
| Migration validation | Called by CI; manual | Real EF inventory and pending-model checks, fresh migrations, repeated/partial migrations and packaged migrator idempotence against isolated PostgreSQL. Missing promised infrastructure/tooling fails. |
| Distributed lock ownership | Called by CI; manual | PostgreSQL 16/17 integration and JIT/native production lock probes. Main CI owns the ordinary 233 lock/caller cases. |
| CodeQL | Called by CI for both master/dev pushes and PRs; weekly; manual master/dev | Required C# and JavaScript analysis. The C# build remains instrumented and separate from normal/native builds. |
| NativeAOT | master push; master/dev PR changing native build/SDK inputs; weekly; manual | Both linux-x64 native publishes; PostgreSQL/Redis/MinIO and authenticated provider, SSE, task, SignalR and media probes; packaged non-root entrypoint execution; image checks and retained logs/metrics/symbols. A smoke failure fails its job; artifacts are still retained. Native remains canary-only. |
| Release | `v*` tag push | Strict SemVer/master ancestry/exact-SHA successful master CI; immutable candidate manifest; three digest-addressed security scans and real packaged/browser proof; serialized production preflight, migration, verified promotion and GitHub Release. Independent native canaries are scanned/executed and never promoted to default channels. |
| Close dev issues | dev push; manual | Closes only explicit closing-keyword references in merged PRs. Manual backfill defaults to dry-run. |

CI publishes no deployment images. Release candidates use unique run/attempt names; official version tags are immutable. Stable versions advance `latest`, prereleases advance `beta`. Production serialization covers migration, all three repositories and GitHub Release creation, with older-channel rejection and a retained recovery ledger. Deploy from the complete verified image-set artifact, using digests. See [CI policy and recovery](../../docs/operations/ci-validation.md) and [native canary policy](../../docs/operations/native-aot-canary.md).

Manual CI's `measure_previous_scheduling` input defaults false. For a paired timing
experiment, run true then false on the identical revision: before-mode restores the
serialized packaged-build start and two repeated 233-case selections while keeping
all required proofs. `scripts/ci/ci-timings.mjs --compare BEFORE_ID AFTER_ID` verifies
the controls and records wall time, runner minutes and packaged-lane elapsed time.
The packaged browser lane also retains a dedicated fixed-fixture benchmark with two
warmups, seven samples, measured reference comparisons and environment identities.

Before cutting a release, merge its source/version update to master and wait for that exact SHA's `CI required` success. Then push a valid tag such as `v3.1.0` or `v3.2.0-beta.1`. The production environment admits version tags and requires `CONDUIT_RELEASE_DATABASE_URL`. A failed preflight, smoke, scan or migration cannot promote official images. Do not trigger a release to test PR changes.

Tests have not moved to archive. Use `scripts/ci/run-tests.ps1 -Project PROJECT -Suite NAME -Filter 'Component=TRAIT'` for retained TRX/counts/logs, with isolated Docker/PostgreSQL/Redis infrastructure where required. Main tests run Debug with matching build configuration; release-migration components have separate required ownership. See the CI policy for exact inventories, intentional skips, coverage minima and paid-provider opt-in rules.

All active workflows default to read-only contents permissions; package/content/security-event writes are limited to the jobs that need them. External actions use reviewed immutable SHAs in `scripts/ci/action-versions.json`. Updates must refresh the inventory and pass validation. [Dependency update automation](../../docs/operations/dependency-updates.md) schedules grouped NuGet/npm PRs to dev, with activation and first-batch triage tracked by #1063. Actions updates retain the reviewed inventory procedure; candidate scans, package audits and CodeQL provide separate evidence.
