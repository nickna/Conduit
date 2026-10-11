# Native publish evidence

`scripts/aot/publish-native.ps1` produces immutable manifests for the exact loose
Admin, Gateway and protocol-probe publish directories. These identify a publish;
they do not associate it with an independently rebuilt Docker image or establish
production qualification. Image association is tracked in
[#1582](https://github.com/nickna/Conduit/issues/1582). Collection attempts and
qualification windows are tracked in
[#1583](https://github.com/nickna/Conduit/issues/1583).

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

Dockerfiles currently run separate publishes and add a native health probe. The
same source revision does not prove that those image payloads match these loose
publish directories. Until image extraction and digest association are verified,
the manifests support inspection of these exact directories only. Existing
historical schema-1 baselines have no manifest identity. Production promotion
remains blocked by the [canary policy](native-aot-canary.md).
