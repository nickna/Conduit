# Controlled benchmark container networking

The current reproduction of [#1702](https://github.com/nickna/Conduit/issues/1702)
fails while establishing TCP connections through Docker Desktop's
`host.docker.internal`/published-port path. Reusing a recently closed frontend
TCP four-tuple can leave new SYN packets unanswered until the request deadline.
The provider never receives an HTTP request on those failed connections.
Direct communication on the fixture's Linux bridge avoids this failure in the
captured controls, including recent four-tuple reuse.

This explains the reproduced communication failure. It does not explain the
[historical SSE performance result](native-aot-gateway-controlled-benchmark.md).
The original historical failure has no retained packet capture, so its exact
network mechanism cannot be established retrospectively.

## Discriminating controls

The [numeric receipt](benchmarks/2026-10-17-gateway-transport-root-cause.json)
retains control counts, capture hashes and the explicit inference bounds.

The provider-only controls use application revision
`dc06e59a84821ef13ab9806bdf5762692204c87f`, the actual frozen HTTP helper and named
client registration, the deterministic provider and its unchanged opaque TCP
relay. The managed client runs .NET 10.0.13. Each batch has 100 warmup and 1,000
measured requests, concurrency eight, a 30-second request deadline and the
original complete JSON validator. The first client runs two batches; its
replacement runs one. The exact mapped request is 129 UTF-8 bytes, SHA-256
`24947705c1aaba84a3e7e7be545c0002ada755baece1d39b9323b143e939bee2`.
These controls do not supply a Gateway performance comparison.

| Captured path | First client | Replacement client | Result |
| --- | --- | --- | --- |
| Host gateway and published provider port, complete repeat | 2,200/2,200 | 1,084/1,100 | 16 deadline failures |
| Direct fixture bridge to the same opaque relay implementation | 2,200/2,200 | 1,100/1,100 | Zero failures |

Both clients reuse the same source IP within each row. In the failed forwarded
control, all 16 failed connections send repeated SYNs without a SYN-ACK or HTTP
payload. All 16 repeat four-tuples used by the preceding client. Those old
connections completed both FIN acknowledgments. Failed reuses begin
9.975–39.224 seconds after the old final packet; ten successful reuses begin
68.929–68.941 seconds later. This brackets an observed retention window; it does
not measure an exact timeout or identify the forwarding implementation.

A separate forwarded control fails 18 requests within its first client. Every
failure reuses that client's own completed frontend tuple, beginning as little
as 14.9 milliseconds after closure. Successful reuses after completed connections
occur 65.116–65.306 seconds later. Its backend connections all complete, including
170 backend tuple reuses under 60 seconds. Thus fresh published ports and distinct
IPs between Gateway variants are insufficient protections against this mechanism.

On the direct bridge, all 138 observed cross-client reuses of recently completed
frontend tuples succeed at ages 9.134–9.252 seconds. The body framing, relay,
helper, validator and deadlines are unchanged. This is the intervention that
removes the failing forwarding path.

The complete forwarded capture has 3,284 accepted provider requests, matching
2,200 plus 1,084 successful client requests. Both provider legs complete all
requests with HTTP 200; there are no orphan provider connections. Complete
captures include both IP families but contain IPv4 traffic only. No underlying
HTTP response exists for the SYN-only failures. Their caller cancellations can
therefore become Gateway communication errors and mapped 502 audits while the
original probe has already reached its deadline.

Packet capture must drain completely before analysis. An earlier capture written
per packet to a Windows bind mount retained only a prefix despite reporting zero
kernel drops. The complete controls write inside the owned Linux capture
container, join it, then copy and hash the file. The provider capture's Linux
loopback statistics count copies that libpcap suppresses from the file: 72,740
file records plus 39,432 loopback copies equal 112,172 received packets, with zero
kernel drops. Client captures match their received counts exactly. Zero drops
alone is not a completeness proof.

## Gateway reproduction

The [closed Gateway receipt](benchmarks/2026-10-17-gateway-causal-failure.json)
confirms the same mechanism in the actual JIT request pipeline at the same
application revision. Of 100 warmup and 1,000 measured JSON requests, 1,092 pass
the original validator and eight reach the 30-second deadline without response
headers. The sequence stops there; SSE and Native workloads do not run.

All eight failed TCP sessions send nine SYNs, receive no SYN-ACK, and carry no
HTTP payload. Each reuses a completed connection from that same JIT process,
8.368–8.772 seconds after its last packet. Both FINs of every predecessor were
acknowledged. The complete provider captures contain exactly 1,092 business
requests and three health requests per leg, all HTTP 200, with no orphan
connections or SYN retries.

All eight probe failures join by trace ID to event 1702
`send-buffered / caller-cancelled`, an absent observed upstream status, and a
persisted 502 audit without usage or calculated cost. These trace joins and the
aggregate packet counts agree; an individual trace-to-socket identity is not
established. Enabled logging scopes did not emit scope lines, so generic log
lines cannot supply additional trace joins.

Completed usage has matching cost, ledger debits and group spend of 0.0819;
the balance is 99.9181. The eight ambiguous started reservations remain
unresolved, totaling 0.32796. Verified PostgreSQL and Redis archives and the
closed capture manifest preserve the failure without resetting the fixture or
releasing holds. This is causal evidence, with instrumented probe diagnostics;
it is not a performance measurement.

## Use the fixture bridge

Pass the existing fixture bridge through `-DockerNetwork`. Seed the provider and
credential base URLs with the provider container's name and internal relay port.
Give the Gateway direct PostgreSQL and Redis container endpoints on that bridge.
Keep separate private host-published URLs for the release Migrator, seed tool and
accounting observers that run outside Docker.

```powershell
./scripts/aot/measure-native-container.ps1 `
  -Service gateway `
  -DockerNetwork $fixtureNetwork `
  -JitImage $jitImageByDigest `
  -NativeImage $nativeImageByDigest `
  -DatabaseUrl $gatewayDatabaseUrl `
  -RedisUrl $gatewayRedisUrl `
  -WorkloadProbePath $publishedProbe `
  -WorkloadConfigurationPaths $jsonConfiguration, $sseConfiguration `
  -EnvironmentEvidencePath $environmentEvidence `
  -ContainerEnvironmentPath $privateRuntimeEnvironment `
  -NativePackagingDirectory $nativePackagingDirectory `
  -PairedRuns 5 `
  -OutputPath $newOutputPath
```

The collector pins and verifies the named local bridge and every running
container's actual attachment. It leaves the caller-managed network and backing
services in place. Container ownership, image binding, graceful shutdown and
absence verification still apply. The measured application ports remain
published on host loopback for the original workload probe.

Selecting a network does not rewrite provider URLs stored in the database. An
existing host-gateway provider URL would keep the problematic backend hop even
with `-DockerNetwork`. Prepare a fresh fixture with direct URLs before collecting;
do not alter a preserved failed fixture or reset its counters and balances.
Declare the changed transport profile in environment evidence and retain the
earlier reports independently.

The discarded pair, five alternating pairs, full JSON/SSE validation, request
counts, deadlines, financial settlement and failed-attempt publication gates
remain required. Preserve ambiguous started reservations as unknown-cost
obligations; connection diagnosis does not authorize releasing them. No runtime,
thread-pool, retry or timeout change is justified by these captures.

## Standard collection verification

The [fresh direct-bridge receipt](benchmarks/2026-10-18-gateway-direct-bridge-verification.json)
records a complete collection using the same frozen JIT/native application
images and original workload probe. The modified collector completes its
discarded pair and five alternating recorded pairs. Every host runs both JSON
and SSE with 100 warmup requests, 1,000 measured requests, concurrency eight and
a 30-second request deadline. All 26,400 collection requests pass the original
complete validators. Two separately settled preflights bring the fixture total
to 26,402 operations: 13,201 JSON and 13,201 SSE requests.

All twelve hosts pass the pinned-network and image checks, graceful shutdown,
removal and independent absence verification. All 24 stdout/stderr streams join
without truncation, overflow or forced termination; their retained file hashes
match. Three post-shutdown accounting observations, each more than 32 seconds
apart, agree on 26,402 request logs and usage audits, 264,020 input and 132,010
output tokens, matching cost/spend/debits of 1.98015 and a balance of 98.01985.
There are zero error audits, residual financial obligations or application
database backends. No fixture state was reset and no ambiguous holds were
manually released.

An earlier direct-bridge collection passed business and accounting checks but
exceeded its external 64-MiB log retention cap. It remains archived as
diagnostics-incomplete. The accepted complete-capture proof uses a fresh seed,
256 MiB per stream and an 8-GiB shared capture budget; logging verbosity and all
application/workload gates remain unchanged. The helper drains after overflow,
and separate synthetic controls verify its overflow and 64-bit budget behavior.

The collection overlapped simulated collector contract tests on the host. It
establishes response, transport, lifecycle and accounting correctness under the
direct-bridge profile; it supplies no performance qualification or production
promotion claim.

The focused bridge proof passes 50 helper contracts and seven process cases;
the closed diagnostic schema proof passes 171 contracts. The overlapping full
suite stopped on its existing driver-timeout timing assertion. An isolated
unchanged control measured 19.946 seconds in collector invocation and 20.196
seconds after later oracle work, demonstrating the timer-accounting defect
tracked in [#1717](https://github.com/nickna/Conduit/issues/1717). The numeric
collection receipt retains that suite's original exit code; it does not claim
a complete contract-suite pass.
