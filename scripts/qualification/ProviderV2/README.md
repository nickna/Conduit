# Provider observation version2

This standalone net10 fixture uses exact SDK10.0.404 and no production references or external packages. One fixed DLL/runtime serves both unchanged Workload and Pressure clients. Their original provider/client source files and original datasets remain frozen.

The wire schema remains1 so frozen clients still validate content/tools/usage/DONE/EOF. /identity declares providerObservationVersion=2, actual runtime/architecture/PID, compile-time SourceSdk assembly metadata and assembly SHA256. The runner checks all fields before each cohort and hashes the full published provider folder before/after. Raw /observations adds version2 and per-request cancellationEvidence alongside the unchanged cancellationTicks field.

First observation atomically publishes timestamp and source. Callback evidence is request-aborted-callback; raw callback time remains separate. If the callback registration was disposed during cancellation unwinding, catch records request-aborted-observed-at-catch ONLY when public HttpContext.RequestAborted.IsCancellationRequested is true. It records the current clock, never reconstructs initiation or backdates the observation. OCE or IOException alone is insufficient. Later callbacks cannot overwrite the first source/time. Wire payloads and pacing retain original behavior.

This defines a new observation cohort. Earlier validated version1 observations remain their own evidence, and version1 failures are not relabeled. Provider write/flush times remain application observations, not socket completion.

Run framework tests from this directory for exact SDK selection:

    dotnet run --project Tests/Conduit.ProviderV2.Tests.csproj -c Release -p:UseSharedCompilation=false -- /absolute/provider-v2-tests.json

Tests deterministically reproduce LIFO removal of the callback registration, verify public-token catch fallback/current-clock boundaries, retain missing raw callbacks, reject nonrequested/None tokens and OCE-only claims, and check atomic publication. Local Windows10.0.15 identity/HTTP cancellation smoke is functionality evidence; Linux/native application/financial/performance qualification remains a separate hosted gate.

The observations endpoint serializes detached immutable DTOs. The handler release-publishes its final timestamp only after all mutable fields and registration disposal finish. A snapshot acquire-reads that timestamp once: progress DTOs contain stable ID/accepted time with all mutable completion/error/cancellation fields unset; final DTOs copy a single cancellation snapshot and all final fields. Capturing progress before completion and serializing later cannot create a zero-end/positive-cancel mixture. No per-chunk lock is introduced.

Final local validation:19 pure-framework checks,45 analyzer tests,4 published-folder mutation cases, and actual Windows10.0.15 identity/before-usage HTTP cancellation. Strict replay binds the V2 identity to PID/architecture/runtime/SDK/assembly hash, requires root/per-request version/provenance, and cross-checks retained versus embedded provider observations. Extra, missing or changed published files fail integrity. Linux/native V2 execution is a separate hosted cohort, still required.
