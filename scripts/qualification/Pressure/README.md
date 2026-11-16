# Supplemental receive-buffer pressure fixture

This project is a separate copy of the frozen extended workload fixture. It changes no production code and does not alter or rebuild the original `Conduit.Phase0.Workload.dll`. `copied-source-identities.json` records the source-copy hashes and original DLL hash. The provider protocol and strict Unicode/text/tool/usage/DONE/Responses/cancellation/upload validators are preserved.

Build only after the primary extended timing window is closed:

```text
dotnet build Pressure.csproj -c Release -m:1 -nr:false
dotnet bin/Release/net10.0/Conduit.Phase0.Pressure.dll --provider 18083
CONDUIT_EXT_BENCHMARK_API_KEY=<fixture virtual key> dotnet bin/Release/net10.0/Conduit.Phase0.Pressure.dll --run <case.json>
```

`catalog.json` defines four controls: standard/enhanced SSE, each fast or paced, with 10,000 content chunks of 1,024 characters plus separators, one measured request, no warmup and a 32 MiB response bound. Root orchestrates five fresh-Gateway repetitions. Fast and paced cases both request an **8,192-byte socket receive buffer**. Fast reads have no explicit delay; paced reads cap each underlying read at 4,096 bytes and wait 2 ms before each read. The actual first marker is shorter than the subsequent fixed-width chunks, and exact content is generated/validated per request ID.

The optional `receiveBufferBytes` field defaults to zero, preserving the standard `SocketsHttpHandler` connection path. Nonzero values must be 4,096–65,536. The pressure path uses `ConnectCallback`, sets `Socket.NoDelay=true` and `Socket.ReceiveBufferSize`, awaits connection and returns a `NetworkStream` that owns the socket. Each connection records its initial request ID, destination, requested receive buffer, **actual OS-reported receive buffer**, NoDelay and monotonic connection timestamp in `sockets[]`. The actual OS value may differ from the requested value; Linux may report a doubled buffer. The provider-observation control connection is outside the measured data path and uses the default receive buffer.

Per-request rows additionally record `lastBodyReadTicks` and `providerFirstContentToLastFlushMilliseconds`, derived from provider first-content write-begin and last successful awaited application flush. Raw provider/client timestamps, exact byte/read counts, full validation outcomes and per-request billing IDs remain in the report. Root separately collects actual Gateway CPU/allocation/GC and sampled working-set peaks.

Compare fast and paced controls within each stream dialect. A longer provider write/flush span under the paced reader, together with much larger body size, smaller actual receive buffer and longer client drain, can demonstrate pressure propagation in this fixture. If the provider span does not change, retain that observation and do not claim demonstrated backpressure. These counters do not locate a specific kernel buffer, isolate network wire time or establish socket-level flush completion. Server peak-memory observations remain sampled; they are not instantaneous allocation peaks. Five single-request repetitions are exploratory and do not establish a production capacity or p99 guarantee.
