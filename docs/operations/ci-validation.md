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
