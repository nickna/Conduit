# Dependency update automation

`.github/dependabot.yml` schedules grouped weekly version updates on Mondays,
with every update PR targeting `dev`. NuGet scans the repository root and updates
the central versions in `Directory.Packages.props`. Related EF Core/Npgsql,
OpenTelemetry, ASP.NET Core/Extensions, tokenizer, Wolverine, FusionCache and test
packages travel together. The EF health-check integration belongs to the EF group;
Dependabot uses the first matching group. Other NuGet packages get individual PRs,
with at most ten version-update PRs open for this ecosystem.

npm monitors the active `/WebAdmin`, `/tools/openapi` and `/tools/ci` roots and their
lockfiles. WebAdmin groups Mantine, Next/React, SignalR and remaining development
dependencies, with at most five open PRs. Each tooling root groups its dependencies
and permits at most two open PRs. Retired SDK packages are not monitored. Major
updates are offered for review as well; grouping does not establish compatibility.
No updates are automatically merged or exempted from required validation.

## Activation and first-batch triage

GitHub reads the configuration from the repository's **default branch (`master`)**,
even though version updates target `dev`. Merge the configuration PR to master to
activate it. See [GitHub's configuration rules](https://docs.github.com/en/code-security/concepts/supply-chain-security/about-the-dependabot-yml-file)
and [target-branch behavior](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference#target-branch).
This configuration does not enable the separate repository setting for Dependabot
security updates; existing required NuGet/npm audits remain in force.

Before the first run, synchronize the reviewed master changes into dev through a
PR. At the October 10, 2026 audit, dev was 100 commits behind master and lacked
`tools/ci/package.json` and the current required security/validation policy. A bot
PR must not update that older graph or bypass the checks added on master. All three
npm manifests and lockfiles must exist on dev before expecting all three npm jobs
to succeed. GitHub only scans manifests on the configured target branch.

After merge and branch synchronization:

1. Open **Insights → Dependency graph → Dependabot** and check each update job's
   logs. Use **Check for updates** to request the initial batch, or wait for Monday.
   Missing manifests, registry failures or configuration errors require correction;
   an empty PR list alone does not prove success.
2. List the initial PRs with
   `gh pr list --base dev --author 'app/dependabot' --json number,title,url,statusCheckRollup`.
   Confirm grouped NuGet updates edit central versions and npm PRs include the
   matching lockfile. Record the actual PR URLs and job results on issue #1063.
3. Review upstream release notes, package compatibility and security/compatibility
   overrides. Keep coupled packages aligned and preserve the documented patched
   transitive floors. For WebAdmin, follow the
   [lockfile override procedure](npm-lockfile-overrides.md) and verify the installed
   dependency graph, not only the lockfile.
4. Require a successful `CI required` on the current PR revision, including the
   dependency audits, contract generation and applicable packaged/browser proof.
   If a group fails, diagnose the dependency change, separate incompatible updates
   where necessary and record any deferred major upgrade with its reason. Do not
   weaken a validation or security gate to merge a bot update.
5. Mark #1063 complete only after the configuration is active and the first actual
   batch has opened and been triaged. Opening the configuration PR alone does not
   satisfy the second acceptance criterion.

## GitHub Actions updates

The optional `github-actions` ecosystem is not enabled in this initial configuration.
Dependabot edits workflow pins but does not maintain the repository's reviewed
`scripts/ci/action-versions.json` inventory. A workflow-only SHA update fails the
existing action-pin gate. Keep Actions updates in reviewed maintenance PRs: resolve
upstream tag commits, inspect runtime metadata, update the inventory and all active
workflow references together, then run `pwsh scripts/test/validate-workflows.ps1`
and `node --test scripts/ci/*.test.mjs`. Automated Actions updates should only be
enabled with a companion process that preserves this review and inventory policy.
