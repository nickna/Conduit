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
The packaged-build checkout disables credential persistence; dependency installation
and Docker builds need no Git credentials. Its scheduling lookup receives an
explicit read-only Actions token only for that step.

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
ordinary selector excludes only explicitly owned `ReleaseMigration` traits. Release migrations belong to the mandatory migration
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

The dedicated `tools/ci/browser-benchmark.mjs` measurement runs in the packaged
browser lane with Chromium from the locked Playwright installation. It uses an
isolated database with four named keys, the local deterministic provider, a
1280x720 viewport, device scale 1, en-US locale and UTC. Two warmup navigations
precede seven measured navigations. Each sample measures navigation start until
all four real key rows are rendered; median, p95 and the complete sample set are
retained in `browser-benchmark.json`, alongside browser/CPU/runner-image identity
and the three exact application image IDs. It runs independently of Jest.

`scripts/ci/browser-performance-baseline.json` records the measured CI reference.
Compatible browser and runner inputs produce reported median/p95 ratios. Changed
browser, CPU or runner-image identities report an explicit incomparable environment
and both raw distributions remain available. Hosted-runner contention still varies;
these measurements establish a repeatable baseline, with no machine-speed pass/fail
threshold. Update the reference only from a successful controlled report using
`node scripts/ci/record-browser-baseline.mjs PATH_TO_REPORT`; its method, all seven
samples, source run/SHA and exact image identities must validate. Retain the prior
reference in Git and explain the update in its PR.
The initial reference is the successful packaged job in
[run 37569962592](https://github.com/nickna/Conduit/actions/runs/37569962592):
median 468.5 ms, p95 528.6 ms, Chromium 153.0.8010.12 on the recorded four-vCPU
Linux runner. The p95 uses nearest rank and is the largest of seven samples;
it does not estimate population tail latency. That run's separate backend job
exposed the Redis test startup assumption tracked in #1474; this reference proves
the successful controlled browser job only.
Relative test globs fix Windows `.codex` worktree discovery (#1457).
The four FusionCache contract classes share an isolated xUnit collection inside
the required main suite. This keeps real Redis read/reconnect budgets independent
of unrelated host-startup/JIT contention without excluding cases or retrying value
assertions. Healthy L2 checks establish readiness and use the supported one-second
read budget; disconnect fixtures retain 250 ms. The mapping contract also outlives
L1 explicitly and reports any swallowed cache failure in its assertion (#1474).

Every required .NET lane uses the shared run-tests action: TRX, method identities,
discovered/executed/passed/failed/skipped counts, execution logs, and requested
coverage are uploaded for 30 days even on failure. Empty execution and unexpected
skips fail. Core's measured minimum is 3,753 executed cases, including the retained
cache-hit regression added in #1472; its three intentional
skips are two unsupported concurrent tracker tests and the local tokenizer baseline emitter. Billing invariants allow
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


Packaged business and S3 proof (#1437)

The required Docker job loads the production Admin/Gateway images, migrates a fresh PostgreSQL database, and runs authenticated public-API flows against Redis and a deterministic local OpenAI-compatible provider. It verifies exact asynchronous charges, non-billable malformed/unknown-model/provider errors, recovery, RPM/RPD limits, depleted balance, key disable/enable, routing and cross-host discovery invalidation. No paid credentials are used. All containers belong to a unique run and are removed after logs and image identities are retained.

`scripts/ci/critical-path-map.json` assigns each of the 30 legacy facts to a required replacement or a justified manual lane. Its regression fails when any fact loses ownership. The legacy external harness is retired (#1464): all 30 facts are explicitly skipped, including missing-provider and timeout-documentation cases. It must not be run as manual acceptance evidence; the five manual entries require deployment-specific fault procedures that assert the fault actually occurred. Paid-provider checks require explicit operator opt-in, a dedicated account/key with a hard provider spending ceiling, a request/time budget, and an isolated scheduled/manual deployment. They must never inherit production credentials from a PR.

The three permanently skipped AWS mocking cases have been replaced by Component=S3Storage against isolated, digest-pinned Adobe S3Mock: actual small video upload, complete stream bytes/bounds, and partial stream bytes/bounds. Missing emulator infrastructure fails the lane; it cannot report a skip. Unit multipart/short-read checks remain.

The packaged flow exposed Admin's missing capability-service registration (#1462) and cyclic EF graphs preventing Redis virtual-key writes (#1463). Those fixes are covered by real endpoint execution and a reflection-disabled serialization regression. The separate catalog importer retry transaction bug is tracked in #1459.

Packaged browser and native entrypoints (#1438)

CI starts the production WebAdmin image alongside the same Admin/Gateway candidates, then runs locked Chromium against real HTTP endpoints. An ephemeral RSA public key is supplied through Clerk's supported jwtKey option (`CLERK_JWT_KEY`); the test signs short-lived isolated session JWTs. It verifies unauthenticated redirect, rejection of a forged signature and non-admin role, authenticated key listing and creation dialog, browser-to-Admin single-use credentials, and session removal. This tests middleware/session boundaries without paid credentials or a production authentication bypass; Clerk's hosted sign-in UI remains an identity-provider acceptance concern.

All three exact image identities, Docker health statuses, application logs, screenshot, Chromium version/navigation timings, and browser trace are retained. Timings are measured evidence, with no invented jsdom millisecond threshold. Production health is public and independent of Clerk; its IPv4 probe matches the server listener (#1460). Production ephemeral issuance cannot return the permanent key (#1461). The real browser exposed and now covers Admin's unregistered collection-wrapper metadata (#1465).

Native image verification now executes the actual non-root container entrypoint and packaged libraries, exports a real contract into the container's writable temporary directory, and retains identity/logs/contracts. Local images exported 191 Admin and 34 Gateway paths successfully. This complements the existing normal native process protocol/infrastructure and JIT parity lanes; native images remain canary-only.

CI ownership and scheduling (#1440)

The main suite now explicitly proves execution of the 233 lock/caller/policy cases across 22 classes previously repeated in both PostgreSQL jobs. A missing class fails that proof. The PostgreSQL 16/17 matrix retains all 18 real infrastructure integration cases per version and both JIT/native production lock probes. Release-migration ownership is separate from main correctness. Distinct instrumented CodeQL builds, RDG checks, static codegen drift, runtime dependency guards and native protocol proofs remain.

WebAdmin correctness, contracts/official SDK clients, backend correctness and packaged-image builds start independently. Docker no longer waits for the serialized backend/WebAdmin chain. Required jobs have explicit bounds. Locked npm caches are scoped to their own dependency files; production artifacts are rebuilt from the tested source instead of sharing mutable build directories across security/runtime modes.

`scripts/ci/ci-timings.mjs RUN_ID...` records actual GitHub job wall seconds and summed runner minutes (job elapsed time, not rounded billing) in artifacts. Historical runs 324/329 are retained as the audited baseline; the new graph adds required migrations, security, storage and packaged/browser execution, so total cost comparisons must disclose that expanded proof inventory. Final branch-run measurements are recorded after GitHub execution.

For comparable scheduling evidence, manually dispatch CI twice on the same source
revision. Set `measure_previous_scheduling=true` for the before run, then false for
the after run after the first completes. Before-mode waits for Validate, WebAdmin
and contracts before starting packaged builds and repeats the 233 main-owned
policy cases in each PostgreSQL job. Both modes retain the complete identical
required proof inventory, PostgreSQL/native probes and security gates. The input
defaults false and applies only to manual dispatch; ordinary PR/master/dev behavior
stays parallel and deduplicated. The barrier fails on unsuccessful predecessors and
has a 25-minute bound. Each repeated selection retains actual execution evidence.

Run `node scripts/ci/ci-timings.mjs --compare BEFORE_ID AFTER_ID`. It rejects mixed
controls, different source SHAs, unsuccessful runs or different job inventories,
then reports overall wall time, runner minutes and packaged-lane elapsed time.
These paired runs control source, proof inventory, runner class and workflow
configuration. Public-runner placement/cache/CPU contention still vary; report
that uncertainty and the CodeQL critical path alongside any observed reduction.
The before-mode barrier polls every 15 seconds, adding up to one polling interval
of scheduling uncertainty; this is a controlled comparison of those two audited
costs, not a reconstruction of every historical workflow change.

Immutable release promotion and recovery (#1441)

Release builds publish uniquely named candidate tags for discovery and retain their immutable digests. The complete three-image manifest feeds actual packaged business/Chromium execution, production preflight, the Admin migrator, and promotion. OCI version/revision labels are verified. Version tags are immutable; final version/channel digests must exactly match the tested manifest. The isolated registry proof exercises Docker's digest-preserving copy primitive in CI.

The production job uses the fixed `conduit-production-release` concurrency group with cancellation disabled. Its lock covers preflight, migration, all tag updates and GitHub Release creation across tags and both channels. If a newer version reaches production first, an older version fails before migration. Otherwise the older version completes and the newer one advances it. The production job uses `queue: max`, retaining up to 100 pending jobs instead of replacing the previous pending release. GitHub queues by when jobs begin waiting, so build completion order can differ from version order; preflight still prevents backward promotion. The documented queue limit is an explicit operational bound. See [GitHub concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency). Both completion orders leave the newest completed version on its own channel. Beta never changes latest. Native candidates remain independent canaries.

Registries cannot atomically update tags across three repositories. Deployments should consume the verified `image-set.json` attached to a successfully created GitHub Release and deploy its digests as one set, rather than react to intermediate channel tag changes. Each write is journaled before execution and verified afterward. A failed promotion restores and verifies all previous channel digests; immutable version tags may remain and are safe to retry. A failed first-ever promotion cannot safely delete a tag's shared manifest: it retains `recovery-required` and the exact intended set instead.

Recovery procedure:

1. Download `production-promotion-ledger` from the failed run; preserve the original artifact and registry logs. Inspect `state`, `failure`, `rollbackFailure`, `migrationSucceeded`, previous digests and every write event. No release is successful without all final digests verified.
2. For a verified rollback, rerun the failed production job in GitHub. It acquires the same lock, repeats idempotent migrations and reuses the validated candidates. No candidate rebuild or mutable tag substitution is allowed.
3. For partial bootstrap/failed rollback, a repository operator must stop new production dispatches and wait for the running production job to finish. In a trusted checkout of the failed release SHA, authenticate to GHCR and run `node scripts/ci/release-images.mjs recover PATH_TO_LEDGER`. Recovery requires recorded migration success, valid candidate labels and unchanged old/desired channel identities. An intervening release makes the ledger stale and recovery fails. Retain the resulting ledger; verify all three digests and rerun the production job to create the GitHub Release before resuming dispatches. Never run manual recovery concurrently with Actions promotion.
4. Existing legacy channels without version labels, mixed versions or missing members fail preflight. An operator must map each legacy digest to verified source/version evidence and repair/adopt a complete labeled set under the same maintenance procedure; the workflow never guesses version order from tag timestamps.

Fault-injection regressions cover every partial channel write, rollback failure, bootstrap recovery, stale-ledger rejection, immutable version conflicts, missing candidates, digest mismatch, required migration success and stable/beta separation. Database migrations cannot be rolled back by retagging images; migration compatibility remains the release owner's responsibility.

Workflow validation and hygiene (#1444)

The required source guard runs checksum-pinned actionlint 1.7.12 for real YAML, Actions schema, expressions and references, then parses YAML for the protected gate, all-change master/dev triggers, time bounds, reusable concurrency, release ordering and digest-based scans. Negative fixtures cover malformed YAML, duplicate keys, invalid expressions/references, missing aggregate dependencies, docs-only filtering, skipped gates and unsafe production serialization. Routine cancellable test jobs are valid.

WebAdmin runs Stylelint and Knip in its required job. Both have zero findings; the 219 CSS findings tracked in #1468 are resolved and `css-warning-budget.json` is empty, so any new finding fails. Malformed configuration/CSS also fails, and reports survive failure. Literal palette values belong in foundation token declarations, with a scoped, explained hex-color exception; usages consume design tokens. Mantine's public component selectors retain scoped naming exceptions. The declaration-spacing policy lets `order/properties-order` enforce separation between groups without conflicting with the inherited declaration rule (#1480). The real Mantine theme regression checks that global/chat variable references are defined, including the corrected custom palettes and dark reasoning token (#1479). CSS changes require rendering checks in light/dark themes and relevant interaction states; budget changes require an explained code review.

Redocly currently reports 379 warnings (325 Admin, 54 Gateway). `--max-problems` only truncates output; it cannot enforce warning failure. The wrapper requests the complete JSON inventory, rejects truncated/malformed reports and errors, and fails on any new rule/location/message identity outside `redocly-warning-baseline.json`. Its negative regression introduces a warning with zero CLI errors and proves failure. Existing custom OpenAPI ratchets remain: 1,004 known violations, including 727 missing-4xx, 272 summaries, three media types and two property casing cases; no allowance was silently removed. #1088 owns remaining warning cleanup and eventual zero debt.

Measured GitHub execution after the scheduling change (run 37562579173): all 18 executed jobs passed; 3,752 main cases passed with three documented skips, 461 Jest cases passed, all three S3 operations and packaged Chromium passed. Wall time was 605 seconds versus historical 761/885 seconds; summed runner time was 56.2 minutes versus 29.05/29.77. This is not a controlled speedup claim: the new required graph adds migrations, both CodeQL languages, both PostgreSQL variants, S3 and packaged browser proof. Backend Validate decreased to 512 seconds from 604/739; CodeQL C# (596 seconds) now dominates wall time. Final PR-run measurements supersede this intermediate measurement when available.


Dependency and candidate security (#1443)

Every master/dev PR and push runs explicit NuGet (including transitive) and all three locked npm audits as a required job. High/critical dependency findings block; lower severities remain reported. Release scans use Trivy 0.75.0 against the exact three JIT/WebAdmin candidate digests and both independent native digests. Critical image findings block, including unfixed ones; high/lower findings remain visible. Source CodeQL is independently required for both target branches. Reports and normalized findings survive policy failure.

There is one repository-owner-approved temporary exception: WebAdmin/braces/3.0.3/GHSA-vfj7-8cjw-p6xm, owned by @nickna, expiring 2026-11-06 and recorded in #1467. `security-exceptions.json` accepts only exact scope/package/version/advisory identities with an owner, reason, unexpired date and linked approval record. An expired or malformed exception fails closed. An exception suppresses only its exact identity and remains counted in output. Owners must approve exceptions before they are added; no wildcard, package-wide or silent unfixed suppression is used. This exception also requires `developmentOnly: true`: each affected lockfile node must explicitly carry `dev: true`, so moving the same identity into production or losing its classification blocks the dependency gate.

The production WebAdmin Docker build runs `WebAdmin/scripts/check-runtime-dependencies.mjs` against the final `/app` tree as the runtime user. It rejects any nested package manifest named `braces`, independent of version or advisory availability, and fails on absent standalone output or malformed manifests. Regression fixtures verify both policy classification and shipped-file rejection. The verified image contains 74 package manifests and no braces package. Registry/advisory rechecking still finds braces 3.0.3 as latest with no published patch; its current roots are Next ESLint and Stylelint through micromatch (Jest no longer reaches braces). #1467 remains open for an upstream fix or compatible tooling replacement and exception removal; these guards enforce the existing approval's conditions.

Rechecked on 2026-10-07: the npm registry still publishes 3.0.3, the advisory has no first patched version, and upstream fixes [#78](https://github.com/micromatch/braces/pull/78) and [#79](https://github.com/micromatch/braces/pull/79) are unmerged. The exception links directly to the [owner's approval comment](https://github.com/nickna/Conduit/issues/1467#issuecomment-6030023231). A regression loads the actual exception file and proves that different scopes, packages, versions, advisories, production dependencies and release images remain blocked, and that expiry fails even with no findings. This verification does not extend the approval or its expiry.

The separate sprintf-js finding in #1477 is removed from WebAdmin's dependency graph. A targeted `@istanbuljs/load-nyc-config` override selects the existing maintained js-yaml 4.3.2 parser, removing its legacy js-yaml 3 / argparse 1 / sprintf-js branch. The published loader uses the retained `load()` API; the override does not change other consumers. `WebAdmin/scripts/coverage-config.test.mjs` exercises the actual loader with YAML/JSON configuration inheritance, conventional scalar/list/merge values, and rejected executable YAML tags. These fixtures run in WebAdmin correctness alongside its full Jest coverage suite. Remove the override when a compatible published parent release adopts a maintained parser; upstream's loader migration is not yet released. No additional security exception is used.

Verify both the lockfile graph and the actual loader's parser after applying overrides. npm 10.9.8 can report a successful lockfile-only refresh while retaining the old branch despite the new override (#1486). For #1477 the obsolete nodes were removed, npm reconciled/pruned the lockfile, and a clean install proved the loader resolves js-yaml 4.3.2. All three fresh audits report only the approved braces finding.

Compatible npm updates remove the initial critical findings and patch contract-tool advisories. The obsolete redundant rational-order CSS configuration and its legacy Stylelint tree are removed; existing explicit property ordering remains. A targeted compatible brace-expansion override resolves an outdated exact transitive pin. Test-only SSH.NET is pinned to 2026.0.0 to fix both published SCP advisories. Dependency automation configuration and first-batch triage remain #1063's scope.

The negative policy proof scanned an actual Node 18.0.0 Debian fixture by immutable digest: 17 critical findings were rejected. The exact patched production WebAdmin image scan passed the critical gate with no exceptions. All 461 Jest tests, type checks, deterministic contract generation and the packaged business/authenticated Chromium flows pass after updates. The parser regressions also prove introduced vulnerable dependencies, malformed/unavailable scans, narrow exceptions and expiry fail as specified.

Actionlint 1.7.12 predates the documented `concurrency.queue` key (upstream [#657](https://github.com/rhysd/actionlint/issues/657), support [PR #654](https://github.com/rhysd/actionlint/pull/654); #746 was closed as a duplicate). The validator and executable regression fixtures share the pinned version and exact unsupported-key diagnostic in `scripts/ci/actionlint-config.json`. Parsed policy validates all workflow/job queue values and rejects cancellation conflicts, and requires `queue: max` on production. Fixtures first prove the executable accepts a valid workflow, then require normal completion and the specific expected diagnostic for invalid workflows. They verify both queue values at workflow and job scope, and show that other syntax, expression and reference errors still block with the suppression enabled. Missing executables and process-start failures cannot count as successful negative tests.

The upstream recheck on 2026-10-07 still finds latest release 1.7.12 and an unmerged support PR. #1469 remains open: remove `queueCompatibilityDiagnostic` from the configuration only after updating the version and reviewed archive checksums to a supporting published release. The fixtures fail if an ignored queue is accepted without suppression, making stale compatibility visible on upgrade; after removal they require native queue support.
