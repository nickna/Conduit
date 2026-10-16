# Native publish evidence

`scripts/aot/publish-native.ps1` produces immutable manifests for the exact loose
Admin, Gateway and protocol-probe publish directories. These identify a publish;
they do not associate it with an independently rebuilt Docker image or establish
production qualification. The completed image-association machinery is described
below and tracked in [#1582](https://github.com/nickna/Conduit/issues/1582).
Collection attempts and accepted qualification windows are implemented under
[#1583](https://github.com/nickna/Conduit/issues/1583); the
[canary runbook](native-aot-canary.md) defines their operational use.

## Fresh output and ownership

Choose a fresh `ArtifactDirectory` for each invocation. Before running any publish,
the script rejects nonempty `runtime/admin`, `runtime/gateway` and `runtime/probe`
directories, including hidden files, linked output directories and an existing
baseline. It claims the report directory with a `CreateNew` ownership file.
Concurrent invocations and reuse of a previously claimed directory fail without
deleting its contents. A failed invocation retains its diagnostic files; use a new
directory for a retry.

Preflight also rejects linked report, symbol and native-build output ancestors.
Each service receives fresh invocation-owned `NativeIntermediateOutputPath` and
`NativeOutputPath` directories. Both native compilation and linking therefore
produce new outputs, rather than attaching current tool identities to an older
cached executable. Normal managed `obj` and `bin` paths remain unchanged. Native
build outputs are retained for diagnostics outside the runtime directories.

```powershell
./scripts/aot/publish-native.ps1 -ArtifactDirectory artifacts/native-aot-review-1
./scripts/aot/native-smoke.ps1 -ArtifactDirectory artifacts/native-aot-review-1
```

## Manifest contents

The three schema-1 `conduit-native-publish` manifests live under
`reports/publish/<buildId>/`. A fresh lowercase GUID identifies the invocation.
Each manifest records its service, RID, UTC publish interval, publish properties,
source identity, resolved toolchain and runtime file identity.

Source identity records the actual Git revision and dirty state when available.
A source snapshot without Git metadata explicitly records an unavailable revision
and dirty state. It does not invent a clean checkout or commit association.

An opt-in MSBuild target captures the selected toolchain after successful native
linking. The publisher records the SDK, actual NativeAOT compiler package and ILC
program, selected linker driver and flavor, program hashes and host/target
architecture. It does not assume that whichever clang appears on PATH compiled
the program. Windows records the selected linker; the NativeAOT application
compile/link steps do not invoke `cl.exe`.

The build-time capture reader, native-output verifier and toolchain resolver live
in `scripts/aot/native-publish-build.ps1`. Producers pass the requested RID directly
to those checks. Dot-sourcing the helper defines functions without writing files;
published hosts do not import it.

The captured object and binary paths must match the invocation's selected native
output paths. The publisher requires both new regular outputs and verifies that
the linked binary's bytes match the executable copied into the runtime directory.

Runtime identity includes every regular file beneath the service directory after
debug symbols move out, including hidden and nested files. There are no excluded
runtime files. Links and reparse points are rejected. Relative paths preserve case,
use `/` separators and sort ordinally. Each entry records its path, 64-bit byte
length and lowercase SHA-256. Fixed-order UTF-8 JSON of that file array supplies the
aggregate digest. The executable entry must agree with the manifest's executable
identity, and all lengths must sum to `runtimeArtifactBytes`.

Manifests are created without overwriting existing files and remain outside the
runtime directories, avoiding a self-hash. The schema-2 `native-baselines.json`
retains the existing service metrics and shallow manifest references containing
the build ID, relative path and manifest hash. Probe identity has its own reference.
The OpenAPI smoke may update timing metrics and the summary timestamp; it preserves
these references and leaves the immutable manifests untouched.

## Qualification boundary

Successful references are emitted only after all three publishes, symbol separation
and the linker ratchet succeed. The contract suite exercises file mutations,
ownership and failure paths independently of native compilation. Actual Linux
publishing must also verify the resolved toolchain capture.

The native Dockerfiles use the assembled image producer described below. These
separate image builds do not establish that their payloads match the loose publish
directories above, even when their declared source revisions agree. Each assembled
image now carries its own complete sealed publish manifests and OCI association;
the benchmark reader requires a digest-pinned candidate and replays that binding.
Existing historical schema-1 baselines have no manifest identity. Production
promotion remains blocked by the [canary policy](native-aot-canary.md).

The document kind distinguishes the two uses of schema 1: a current
`conduit-native-publish` manifest contains source, toolchain and file identity;
a historical `native-baselines.json` report contains unbound metrics. A version
number alone does not make the historical report qualify. The container collector
rejects the legacy `NativeBaselinePath` argument and reads metrics from exactly
one completed image package, avoiding first-match selection from a service list.

## Assembled image payloads

`scripts/aot/publish-native-image.ps1` prepares a complete application payload for
one service, including a separately compiled native health probe. It requires a
fresh empty `ArtifactDirectory` and retains an exclusive invocation claim after
success or failure. Application and health-probe compilation use distinct fresh
native object and binary directories. Both captures must match the actual
invocation's version, commit and timestamp properties.

```powershell
./scripts/aot/publish-native-image.ps1 -Service admin -ArtifactDirectory artifacts/admin-image-review-1
```

The `payload` directory contains every application runtime file, including hidden
and nested files, plus the health executable renamed to `conduit-health-probe`.
All debug symbols move to component-specific symbol directories, preserving their
relative paths. The health component must need only its native executable; extra
runtime dependencies fail publication. A collision with the health executable's
packaged name also fails. Both real publish logs feed the owning service's linker
ratchet, so the health probe cannot bypass warning checks. Updating the shared
warning baseline still requires both services.

The `evidence` directory contains exactly three immutable documents.
`application.json` and `health-probe.json` each manifest the entire assembled
payload. Their selected executables and toolchains identify the respective native
compilations; the health manifest uses the `probe` role. `image-publish.json` binds
their hashes to one build ID, service and RID, records declared assembly metadata,
and reports both measured publish durations and the final executable/payload sizes.
The verifier recomputes both full file manifests and checks roles, source identity,
metrics and reference hashes without accessing foreign compiler paths.

Actual Git metadata, when available, must agree with the assembly commit property.
A snapshot without Git metadata retains an unavailable source identity. A supplied
commit argument alone does not establish a clean checkout or promotion evidence.

Both native Dockerfiles invoke this producer and package its exact `payload`
directory as `/app`. They copy the three `evidence` documents to `/conduit-evidence`,
outside the runtime file manifest. The separate scratch `symbols` stage preserves
the application and health-probe symbol directories, including nested paths. Build
tools, compiler captures and publish logs stay in the builder stage.

Historical filtered local Docker contexts excluded `.git`, so those builds record source
status `unavailable` with null revision and dirty state. The declared commit argument
and OCI revision label remain metadata, without establishing actual Git provenance.
Embedded evidence from those snapshots supports historical payload inspection.
It cannot satisfy the clean-Git benchmark association requirement. Current native
CI uses the checksum-pinned Git context described below; registry qualification
still requires the separate operational evidence.

## Runtime archive inspection

`native-image-runtime-archive.ps1` provides `Expand-NativeImageRuntimeArchive` for
inspecting a Linux container's exported tar archive on Windows or Linux. Pass an
archive path, a fresh empty output directory and the `admin` or `gateway` service.
It extracts only `/app` and `/conduit-evidence`, retaining a separate extraction
claim. Failed extractions retain their partial files for inspection and cannot be
reused as fresh output directories.

The reader rejects unsafe or duplicate paths, links and special files in the
selected payload, build tooling and symbols, incorrect runtime file ownership,
and missing executable permissions. Base-image links outside the selected roots
are never materialized. Archive size, selected file sizes, entry counts and read
time are bounded. The returned payload and evidence paths can be passed to
`Assert-NativeImagePublishEvidenceMatchesPayload`; extraction alone does not
establish source provenance or an image digest association.

## Packaged image verification

`verify-native-image.ps1 -Image IMAGE -Service admin|gateway` resolves the image
once and inspects a stopped container created from its immutable local image ID.
The existing `http` service argument selects Gateway. Each invocation uses a fresh
artifact directory; `-ArtifactDirectory` can select an empty destination.

The verifier checks the runtime configuration, exported file ownership and modes,
both complete payload manifests, and all three embedded evidence documents. It
also compares the embedded service, RID and declared build metadata with the image
configuration and OCI labels. After removing its owned container and confirming
its absence, it saves `packaging-evidence.json` alongside the archive and extracted
evidence, then runs the packaged entrypoint smoke using the same local image ID.
Failed inspections retain their files without a successful packaging report.

The entrypoint driver uses an owned container with no external network access to
export the native OpenAPI document. It checks the real process exit status and
validates the bounded JSON document before removing the container. A running
owned container is stopped within the cleanup budget; removal verifies ownership
and confirms absence before `runtime/evidence.json` records success. The evidence
binds the execution to the original packaging report and OpenAPI bytes. Failed
execution leaves its diagnostic files without successful execution evidence.

The runtime verifier addresses uncertain creation
([#1595](https://github.com/nickna/Conduit/issues/1595)) by recording its create
attempt before launching Docker. A fresh GUID supplies the exact container name
and owner label. Cleanup can recover that name when creation accepted the request
but returned an error, timed out or produced an unusable ID. Inspection must agree
on name, owner, service and immutable image before cleanup pins or acts on the ID.
An ownership mismatch fails verification and leaves that container untouched.

Execution and cleanup have independent budgets. Cleanup inspects again, stops an
owned running container, verifies its stopped state, removes it without force and
confirms absence. Successful evidence is created only after that sequence and a
final replay of the package and OpenAPI identity. A retry requires a fresh artifact
directory; diagnostic files from a failed invocation cannot become a successful
report from a later run. `test-native-image-runtime-evidence.ps1` exercises these
boundaries with real child commands; packaged entrypoint evidence is supplied by
the hosted image jobs described below.

Each invocation retains a fresh parent directory with `packaging` and `runtime`
children. The Node command `native-container-smoke.mjs IMAGE SERVICE` calls this
same PowerShell driver; an optional third argument selects the fresh parent
directory. The PowerShell command accepts the corresponding `-ArtifactDirectory`
argument. Both entry points require `pwsh`.

The report records the observed local image ID, which can identify an OCI index
in Docker's containerd image store. It preserves the actual available or unavailable
source record and records registry digest qualification as false. The completed
package association below verifies local descriptor roles; production qualification
still requires the canary policy.

## Offline OCI archive association

`native-image-oci-association.ps1` verifies a saved OCI archive against separately
observed image and container descriptors. `Get-NativeImageOciArchiveAssociation`
accepts `-ArchivePath`, `-RequestedImage`, `-ImageObservation` with `id` and
`descriptor`, and `-ContainerObservation` with `id`, `image`, and
`imageManifestDescriptor`. Save the observed immutable local image ID without a
`--platform` filter to retain its original index target.

The verifier hashes the original root index or manifest, the unique Linux/amd64
manifest selected by the container, its config, and every selected layer. It
records these digest roles separately from the archive's `index.json` wrapper
hash. The [OCI image layout](https://github.com/opencontainers/image-spec/blob/v1.1.1/image-layout.md)
allows that wrapper to reference another index or manifest.

Callers must require `associationStatus` to equal `verified` and
`descriptorChainVerified` to equal true. Classic archives, unavailable inspection
descriptors, or missing required content produce an explicit hold result. A
verified archive chain retains `registryDigestQualification=false`; source,
completed-package association, and benchmark qualification remain separate
requirements. Existing unavailable source provenance remains unavailable.

## Completed package association

New packaging reports use schema 2 and seal three retained files through the
`ociAssociation` field: `image-observations.json`, `oci-association.json`, and
`image-save.tar`. Each reference records its relative path and SHA-256; the
archive reference also records its byte count. The packaging container is
created for `linux/amd64`, and its observed manifest identifies the same
container that supplies the exported payload and embedded publish manifests.

Completed-package readers verify those references and recompute the original
OCI descriptor chain before runtime execution and again after cleanup. For a
verified chain, the runtime container must select the packaged manifest too.
The runtime report's packaging report hash binds the complete association.

Schema-1 packages remain readable for historical inspection and local smoke
checks. Schema-2 packages from a classic image store or an engine without the
required descriptor observations retain an explicit association hold. Local
smoke checks can still run in that state; they do not qualify an image digest.
Both versions keep `registryDigestQualification=false`. The benchmark collector
and promotion evaluator establish their own candidate binding through
`native-benchmark-image-evidence.ps1`. They require the exact digest-pinned
requested image reference, a verified original OCI association, and clean Git
source matching the declared commit and OCI revision. Current schema-4 benchmarks retain
the packaging report hash, build ID, both executable/toolchain records, runtime
inventory hash and three publish manifest hashes. Every native sample, its
median and publish metrics carry the same sealed binding. The evaluator replays
an explicitly supplied retained packaging directory, including after its
calculations. Changed files, source, build, digest roles or metrics produce HOLD.
This binding leaves the operational promotion policy and its provenance,
benchmark, soak and rollback requirements in force.

The following contracts exercise the build association required by
[#1582](https://github.com/nickna/Conduit/issues/1582):

| Association | Validator and regression suite |
| --- | --- |
| Exact source, selected toolchain, fresh outputs and full runtime inventory | `native-publish-evidence.ps1`, `native-publish-build.ps1`; `test-native-publish-evidence.ps1` |
| Independently compiled application and health probe belong to one sealed image build | `native-image-publish-evidence.ps1`; `test-native-image-publish.ps1`, including a substituted child manifest with its reference hash updated |
| Requested index, selected platform manifest, config and observed local image/container retain their distinct roles | `native-image-oci-association.ps1`; `test-native-image-oci-association.ps1` |
| Every measured sample and publish metric belongs to the retained package | `native-benchmark-image-evidence.ps1`, collector and evaluator; `test-native-benchmark-collector.ps1` and `test-native-promotion.ps1` |

The promotion suite rejects a different sealed build even when its source,
runtime bytes and metric values match, and rejects a single cold sample borrowed
from another build. Collector regressions reject the cross-build package before
launching the workload. Mutated retained payloads, manifests and OCI archives also
fail validation. These are contract fixtures; their synthetic promotion decisions
do not supply production measurements or operational approval.

Schema 4 also seals collection attempts and atomic report publication. The evaluator
requires the latest successful attempt, or an explicitly approved historical
selection inside an accepted qualification window. It rechecks original report,
archive, attempt and window hashes after evaluation. Failed or running reruns,
expired windows and changed evidence produce HOLD. Historical schema-3 reports
retain their original meaning; they do not acquire schema-4 lifecycle evidence.

## Hosted CI descriptor gate

The native image jobs configure the runner's Docker daemon with the containerd
image store before Buildx starts. The source-pinned Docker setup action is scoped
to these fresh hosted image jobs, which have no service containers. The ordinary
verifier continues to report a truthful HOLD on classic stores.

After the packaged entrypoint smoke, a required CI step replays
`Get-NativeImageRuntimePackaging` and requires exactly one schema-2 package for the
requested service, image and clean `GITHUB_SHA`. It requires a verified local OCI
descriptor chain, a selected manifest and no hold reasons, while preserving
`registryDigestQualification=false`. Artifact retention runs even on failure.
The exact YAML step passes ten offline policy cases on each of Windows and Linux,
including tagged-image acceptance and HOLD/source/service/image/cardinality
rejection; all 26 workflow policy tests also pass. Those synthetic cases do not
establish live hosted containerd integration.

The earlier successful
[Linux run 38175668349](https://github.com/nickna/Conduit/actions/runs/38175668349)
at `e0f9f9904df187460ab338bd86619d912f653d60` retains clean-Git payloads and
successful Admin/Gateway packaged OpenAPI documents (191/35 paths). Both packages
honestly report missing image/container descriptors and OCI association HOLD from
the classic image store.

[Linux run 38856125759](https://github.com/nickna/Conduit/actions/runs/38856125759)
subsequently passed the required OCI association gate for both Admin and Gateway,
with packaged OpenAPI exports of 191 and 35 paths and confirmed runtime cleanup.
The run's PR head is `bc9a0741fa68ed3a401161b633825cc76a6fbe94`; its actual
checkout and image source are the synthetic merge
`e340f3f940a8f9419384dbb4cc4b6d0b11d38a73`. The gate verified each clean package
against that merge's `GITHUB_SHA`. This supplies hosted local descriptor-chain
evidence for those builds. Registry digest qualification and operational promotion
remain separate, and later source changes require their own CI verification.

## Native build source

Native CI, release candidates, and symbol exports use a Git context whose `ref`
and `checksum` both name the declared full commit. They retain `.git` with
`BUILDKIT_CONTEXT_KEEP_GIT_DIR=1` and pass
`NATIVE_SOURCE_POLICY=require-clean-git`. The native Dockerfiles select
Dockerfile frontend 1.18.0; checksum query contexts require Buildx 0.28 or later,
as documented by [Docker](https://docs.docker.com/build/concepts/context/#url-queries).

The image publisher's `-SourcePolicy require-clean-git` requires an explicit full
commit matching actual clean Git source before claiming output or publishing.
It checks the source again after both native components have been published,
before writing their manifests and the image summary. Missing Git metadata,
tracked or untracked changes, and mismatched revisions fail that policy.

Both component publishes set `IncludeSourceRevisionInInformationalVersion=false`
so the supplied informational version remains exact. The SDK otherwise
[appends the Git revision](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/8.0/source-link)
during assembly metadata generation, including when an informational version was
supplied explicitly. The publisher still requires the captured version,
informational version, commit and timestamp to match its invocation. The full
Git revision remains recorded independently in the source and commit metadata.

Local publishing defaults to `-SourcePolicy local`. A filtered local Docker
context can therefore retain unavailable source provenance for local checks;
the declared commit metadata does not supply a missing Git identity. Historical
packages keep their original source records. Clean source is one prerequisite
for image/build association, and does not establish promotion qualification.
