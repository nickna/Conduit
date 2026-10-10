# NativeAOT audit and CI baselines

Gateway runtime feature support, exclusions, process gates, and the third-party risk ledger are defined in [gateway-native-aot-feature-matrix.md](gateway-native-aot-feature-matrix.md). A successful publish alone is not a claim that the full JIT data plane is supported.

NativeAOT readiness is measured separately from the normal JIT developer loop. The
production services remain framework-dependent until the persistence and runtime
parity phases are complete.

## Local analyzer audit

From the repository root, run:

```powershell
./scripts/aot/aot-audit.ps1
```

This is the single local audit command. It rebuilds the Admin and Gateway service
graphs with `ConduitAotAudit=true`, enabling AOT and trim analyzers, reflection-disabled
JSON defaults, generated configuration binding, and generated request delegates. It
does not set `PublishAot` or run the native linker.

Results are written to `artifacts/aot-audit/`:

- `diagnostics.json` contains every unique first-party warning with its code,
  project, source file, line, column, and message.
- `summary.md` reports counts by warning code and project.
- `admin.log` and `gateway.log` retain the raw build output.

The checked-in `scripts/aot/warning-baseline.json` is a ratchet grouped by project,
warning code, and source file. Counts may decrease without changing the baseline;
new groups or increased counts fail. After deliberately reviewing a changed warning
inventory, regenerate it with:

```powershell
./scripts/aot/aot-audit.ps1 -UpdateBaseline
```

Do not update the baseline to hide a regression. The later NativeAOT phases should
normally only reduce it.

## Epic #1368 assessment: 2026-10-09

Reviewed [#1368](https://github.com/nickna/Conduit/issues/1368), all seven child
issues, merged NativeAOT work through [#1489](https://github.com/nickna/Conduit/pull/1489),
and commit `e71f8d61556e38ddd0de2eda5f7654c35e6468fd`. The children still have unchecked
criteria describing several already implemented changes. Keep the parent open:
warning cleanliness and the bounded feature probe do not establish full native
workload readiness.

### Verified evidence

- The local .NET SDK 10.0.401 analyzer audit rebuilt both service graphs and
  reported **0** first-party trim/AOT diagnostics. Existing nullable compiler
  warnings are separate from this inventory.
- [Linux NativeAOT run 38024991341](https://github.com/nickna/Conduit/actions/runs/38024991341)
  on the reviewed commit passed both native publishes/OpenAPI smokes, the native
  persistence probe, nine shared EF/Npgsql PostgreSQL contracts without skips,
  two-host Wolverine delivery, the bounded Gateway protocol probe, and both
  non-root packaged native entrypoints. Its retained `linker-diagnostics.json`
  has `rid: linux-x64`, `total: 0`, and no first-party diagnostics.
- That run measured Admin/Gateway executable sizes of 100,567,984 / 93,453,392
  bytes, publish times of 482,308 / 294,734 ms, and infrastructure-free OpenAPI
  readiness of 685 / 203 ms. These are publish/boot baselines, not production
  workload comparisons. [Main CI](https://github.com/nickna/Conduit/actions/runs/38024991331)
  also passed on the same commit.
- The successful native run's retained process logs still show real unsupported
  workload failures. `wolverine-two-host-*/ConduitLLM.Gateway.out.log` records
  unprecompiled EF queries in function-audit cleanup and billing reconciliation;
  the Admin logs record global-setting load, pricing audit/canary, and operations
  metrics failures. The probe does not assert those background operations.
  Expected provider-error and connection-limit test logs are separate from these
  failures.
- Both services disable reflection JSON defaults and use generated resolver chains;
  no production `DefaultJsonTypeInfoResolver` remains. The serialization fixture
  project has 16 representative tests. The broad unit/integration test executables
  do not inherit the services' reflection-disabled feature switch.
- Microsoft's [current EF NativeAOT documentation](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries)
  still describes the EF query path as experimental and unsuitable for production,
  with dynamic queries unsupported. [Npgsql itself supports NativeAOT](https://www.npgsql.org/doc/compatibility.html);
  this does not make the EF provider/query workload proven. ADRs 0006 and 0009
  remain applicable.

### Phase disposition

| Child | Current state | Work required before closeout |
|---|---|---|
| [#1369](https://github.com/nickna/Conduit/issues/1369), analyzer/publish gates | Implemented and verified. Empty analyzer/linker ratchets, SDK checks, Linux process/image gates, symbols separation, and retained baselines exist. | Reconcile the issue checklist with the linked run; retain the gates for subsequent slices. |
| [#1370](https://github.com/nickna/Conduit/issues/1370), serialization/validation | Original reflection fallbacks, enum boot failure, and validation warnings are resolved. [ADR 0004](../decisions/0004-json-source-generation-contract-inventory.md) now distinguishes its historical scope from current generated contracts. | Exercise the remaining endpoint/provider graphs with reflection disabled; representative fixtures and OpenAPI materialization are insufficient for every runtime payload. |
| [#1371](https://github.com/nickna/Conduit/issues/1371), reflection/discovery | Static handler bridges/registries, provider capabilities, decorator traversal, and production `RunAsync` replace the original application discovery paths. | Review and retain bounded third-party Wolverine/EF metadata exceptions; remove them only with upstream support and passing native tests. |
| [#1372](https://github.com/nickna/Conduit/issues/1372), dependency boundaries | Neutral Contracts and Persistence.Abstractions plus optional adapter projects are extracted; generation tooling is opt-in. | Configuration still owns EF and references Functions, and Core/Providers still depend on it. Guard transitive publish dependencies as well as direct assembly references. Native MessagePack registration is excluded, but its package reference remains unconditional. |
| [#1373](https://github.com/nickna/Conduit/issues/1373), persistence | Standalone migrator, schema-version seam, two accepted ADRs, typed stores, and real database/native probes exist. | Complete the [remaining operation inventory](persistence-aot-query-inventory.md), including Admin workloads and Gateway workers/new discovery consumers. The 25 method-scoped query exceptions remain unsupported work, not completed queries. |
| [#1374](https://github.com/nickna/Conduit/issues/1374), runtime parity | OpenAI-compatible chat/SSE/accounting/image storage, authenticated JSON hubs, Redis backplane, and bounded Wolverine delivery pass natively. | Prove normal readiness, invalidation, tools/functions, additional providers/media, durable recovery and failures, and native aggregate values. See the [feature matrix](gateway-native-aot-feature-matrix.md) for exact evidence limits. |
| [#1375](https://github.com/nickna/Conduit/issues/1375), rollout | Minimal candidate-image, scan/attestation, benchmark and promotion-policy machinery exists. No release-workflow runs or production canary evidence were found during this audit. | Representative digest-pinned benchmarks, successor persistence approval, release evidence, dashboards/alerts, rollback rehearsals, then ordered 168-hour Admin and Gateway soaks. Keep `promotion-policy.json` blocked and JIT deployable. |

### Recommended next work and completion criteria

1. **Make native startup and scheduled work trustworthy (#1373/#1374).** Extract
   function audit/retention and billing-reconciliation operations behind named
   stores; cover checkpoint advancement, concurrent workers, rollback/retry, and
   retention boundaries with shared EF/Npgsql contracts and a published native
   worker test. Replace Admin startup reads/audit/metrics before treating Admin
   liveness as readiness. Require successful worker outcomes and classify unexpected
   process-log failures explicitly instead of accepting a probe pass alone.
2. **Prove readiness and invalidation (#1374).** Run the process gate in `Wait`
   mode; assert missing/stale schema holds `/health/ready` at 503, a migrated schema
   becomes ready, and unreachable PostgreSQL/Redis fails readiness. Exercise
   cross-host key disablement/credential rotation/cache invalidation on subsequent
   HTTP and hub operations. Remove exclusions only after these assertions pass.
3. **Close database and protocol gaps by operation (#1370/#1373/#1374).** Cover
   `/v1/discovery` and tool/function execution, durable outbox/inbox redelivery,
   worker restart/recovery, and supported video/audio/provider contracts. Add
   native seeded aggregate assertions: `/metrics` returning `# HELP` currently
   proves exposition only. Replace/prove all 25 excepted Admin/reporting methods
   and the remaining management/retention workload. Each selected native store
   needs one shared PostgreSQL contract plus published-process evidence.
4. **Finish graph ownership (#1372).** Continue ADR 0009's slice extraction,
   separate remaining persistence/domain dependencies, and verify the actual
   transitive publish graph. Remove native-only unused protocol packages without
   dropping JIT functionality. Admin has real provider testing, function credential,
   and S3 cleanup consumers; blanket dependency removal is inappropriate.
5. **Collect operational evidence (#1375).** Replace the health-only load driver
   with representative authenticated workloads and identical host/load inputs;
   retain repeated JIT/native measurements tied to image digests. Obtain the
   successor persistence decision, release security/SBOM/provenance and monitoring
   evidence, and rehearsed automatic/manual JIT rollback. Only then start Admin's
   seven-day soak followed by Gateway's separate seven-day soak.

This follow-up fixes one reproduced promotion-check defect: unlike request counts
or concurrency could previously pass. Both are now required to be equal positive
integers, with positive and negative regression fixtures. It also refreshes the
contract/query/feature/runbook documentation. It does not mark the epic complete or
turn a health-endpoint benchmark into production workload evidence.

## Analyzer ratchet

The 2026-08-12 post-phase audit contains **0** unique first-party diagnostics,
down from 509 before the epic-level cleanups. The first cleanup removed all 139
`MaxLengthAttribute` `IL2026` diagnostics by using statically analyzable string
validation and an explicit collection-count validator. The next cleanup removed 12
paired `IL2026`/`IL3050` diagnostics from the closed pricing-configuration shapes by
adding source-generated JSON metadata. The Bedrock Converse cleanup removed another
10 paired diagnostics by replacing dynamic JSON conversion with typed content handling
and generated metadata. The OpenAI-compatible mapping cleanup removed 10 more by using
typed tool-call contracts, generated annotation metadata, and explicit content-array
handling. The media-cleanup cache cleanup removed another eight paired diagnostics
with a dedicated source-generated Redis context. Regression tests preserve validation,
provider-wire behavior, and legacy cache contracts. Model-capability persistence then
removed eight more with generated configuration metadata while retaining legacy reads.
The async-task boundary cleanup removed another 22 paired diagnostics by generating
the persisted metadata/cache contracts and replacing anonymous result payloads with
statically described models or JSON DOM values. It also makes progress updates work
against the `JsonElement` values produced when persisted results are read back.
The shared security-cache cleanup removed another eight paired diagnostics by passing
generated contracts through its generic cache helpers and deleting an unused dynamic
value helper.
The multimodal content-helper cleanup removed another eight paired diagnostics by
limiting its supported shapes to strings, JSON elements, typed content parts, and
enumerables instead of reflectively serializing arbitrary objects to inspect them.
The Vertex service-account cleanup removed another eight paired diagnostics with a
generated credential/token/JWT contract, including explicit metadata for polymorphic
JWT string and integer claim values.
The shared Redis-cache cleanup removed another six paired diagnostics by requiring
source-generated metadata at its generic read/write boundary and registering the
Gateway provider, tool, and parsed-pricing cache shapes.
The streaming cleanup removed another six paired diagnostics by requiring generated
metadata for generic SSE/custom-stream parsing and registering the OpenAI-compatible
and MiniMax stream contracts.
The prompt-cache marker cleanup removed another six paired diagnostics by converting
known HTTP content through generated metadata and cloning existing JSON DOM nodes.
The provider-wire cleanup removed another 26 diagnostics by generating Cloudflare
image, Replicate prediction, OpenAI-compatible audio/chunk, and MiniMax chat/video
contracts, while making Replicate diagnostic formatting reflection-free.
The function-subsystem cleanup removed all 46 remaining Functions diagnostics by
generating built-in provider, pricing, execution-record, MCP, structured-JSON, and
hybrid-cache contracts; model-capability cache callers now provide primitive metadata.
The Core-boundary cleanup removed all 76 remaining Core diagnostics by generating
closed cache, pricing, webhook, error, rate-limit, and orchestration contracts; generic
HTTP and cache paths now resolve configured metadata, and ephemeral-key services pass
their generated contracts into the shared base class.
The configuration cleanup removed all 23 remaining Configuration diagnostics with
generated persistence contracts, statically analyzable collection/range validation,
the checked-in compiled EF model boundary, and removal of a redundant LINQ conversion.
The final provider cleanup removed all 12 remaining Providers diagnostics and wires the
generated provider/Core resolvers through generic HTTP calls. OpenAI-compatible request
maps now use statically described dictionaries, and provider response/stream contracts
are registered end to end.
The final Security cleanup removed its last paired diagnostic with a generated
middleware error-response contract.
The final Admin cleanup removed all 73 remaining diagnostics by routing persisted
and HTTP JSON through generated contracts, replacing reflective options validation,
and removing redundant query conversion.

The first-party analyzer inventory is now empty. Treat the generated
`diagnostics.json` as the source of truth for the fast analyzer lane. This does not
mean that the native linker inventory is empty: the linker analyzes additional
closed generic instantiations and EF expression trees that are not reached by the
regular compiler analyzers.

## Native publish lane

`.github/workflows/native-aot.yml` publishes both services for `linux-x64` after
merges to `master`, on a weekly schedule, and on manual dispatch. The workflow:

1. Native-publishes both service projects without changing normal build properties.
2. Inventories the real linker diagnostics and rejects increases above the checked-in
   `scripts/aot/linker-warning-baseline.json` ratchet.
3. Moves `.dbg`/`.pdb` files out of runtime directories into a symbols artifact.
4. Launches each native executable using the infrastructure-free OpenAPI entry path.
5. Retains publish time, executable and runtime size, OpenAPI readiness time, peak
   working set, generated OpenAPI documents, and process logs.

Native smoke failures fail the job. Diagnostics and measurements are retained even
on failure. The supported Gateway gate includes PostgreSQL, Redis, MinIO, provider
HTTP/SSE, request accounting, task state, and real JSON SignalR connections. JIT
build/test and Docker validation remain required independently.

The publish lane writes `linker-diagnostics.json` with each de-duplicated first-party
`IL2026`, `IL3050`, `IL207x`, or `IL209x` diagnostic and `linker-summary.md` with
counts by service, project, and warning code. The evaluator is also independently
callable against existing publish logs:

```powershell
./scripts/aot/evaluate-native-linker-warnings.ps1 `
  -ReportDirectory artifacts/native-aot/reports `
  -BaselinePath scripts/aot/linker-warning-baseline.json
```

EF Core 10's compiled-model generator emits closed enum and array mappings through
APIs annotated for arbitrary runtime types. The 22 affected generated `Create`
methods carry exact `IL3050` exceptions with an owner, upstream issue, and removal
condition. `scripts/aot/normalize-compiled-model.ps1` reapplies and verifies that
bounded generated-file set after regeneration.

The 2026-08-28 `win-x64` native publish contains **0** first-party linker diagnostics.
The checked-in linker baseline is therefore empty: any new first-party `IL2026`,
`IL3050`, `IL207x`, or `IL209x` diagnostic now fails the native publish lane.

This closeout removed the eight supported Gateway metrics diagnostics by introducing
the fixed-shape `IGatewayMetricsStore`; its EF reference and typed-Npgsql adapters pass
the same real-PostgreSQL aggregate contract. The remaining 29 emitted diagnostics came
from 25 EF query methods on the Admin management/reporting path that ADR 0006 explicitly
excludes from the supported NativeAOT data plane. Those methods now carry individual
`IL2026` exceptions with the database/runtime owner, `dotnet/efcore#29754`, and a
removal condition requiring either a fixed-shape native store or trim-safe EF query
construction. No assembly-, type-, or warning-category suppression is used.

Zero emitted first-party diagnostics is the warning ratchet, not proof that the
excepted Admin methods can execute natively. Those methods remain unsupported and
must be replaced or independently proven before the parent epic is complete.
Production NativeAOT promotion also requires a successful `linux-x64` release
publish plus the full feature-parity, digest-pinned benchmark, ordered soak, and
rollback gates described in the feature matrix and promotion policy.

## Integration checkpoint: 2026-10-07

The persistence/protocol stack previously merged only into feature branches is now
integrated with the current Gateway and Admin code. JIT retains FusionCache and the
existing distributed-lock registrations. Media acceptance, recovery, and terminal
writes retain their transactional Wolverine dispatch; stale task snapshots cannot
overwrite newer claims. Queued spend updates use the selected virtual-key runtime
store instead of re-entering EF in native builds.

Local verification uses .NET SDK 10.0.401, `win-x64`, PostgreSQL 17, Redis 7.4, and
pinned MinIO. The Release solution build, empty analyzer/linker ratchets, both native
OpenAPI smokes, two-host native Wolverine dispatch, native persistence probe,
nine shared EF/Npgsql contracts, and the
published Gateway provider/SSE/accounting/storage/JSON-SignalR matrix pass. The core
suite, billing invariants and fault injection, and 26 durable media/webhook cases
also pass. Offline OpenAPI and client types match the checked-in contracts across
two isolated generations; committed Wolverine adapters have no drift. The
`linux-x64` workflow remains the platform-specific release gate.

The parent epic remains open. Remaining work is:

- **Persistence (#1373):** replace or independently prove the 25 excepted Admin
  query methods and the remaining management/reporting/retention workload. Refresh
  the query inventory for features added since its original audit. Gateway function
  audit/retention and billing reconciliation still execute unextracted EF queries;
  native host startup logs expose those failures even when the bounded protocol
  probe passes. They require typed stores and process tests before production use.
- **Runtime parity (#1374):** prove database readiness, authentication-cache
  invalidation, tool/function providers, and provider/video/audio flows beyond the
  documented OpenAI-compatible chat/image matrix. JSON-only SignalR remains an
  explicit first-image limitation. Full native Admin management coverage remains
  outstanding. A passing bounded probe is insufficient for these workloads.
- **Operations (#1375):** collect digest-pinned JIT/native benchmarks, security,
  SBOM/provenance, dashboard/alert, and automatic/manual rollback evidence. Complete
  the policy's 168-hour Admin soak before the separate 168-hour Gateway soak. The
  promotion policy stays blocked until workload and operational evidence exist.

Native CI now checks the shared EF/Npgsql contracts, publishes/runs the native
persistence probe, and retains Gateway/Wolverine process logs with the native
reports. Source changes to shared libraries and service code trigger the native
lane. Compiled-model normalization checks the exact exception code, justification,
and placement on `Create`, including rejection of an altered exception.
