using BenchmarkDotNet.Running;

#if !STREAMING_MICRO_ONLY
using ConduitLLM.Benchmarks.Messaging;
#endif
using ConduitLLM.Benchmarks.Streaming;

namespace ConduitLLM.Benchmarks;

/// <summary>
/// BenchmarkDotNet runner for Conduit performance benchmarks.
/// </summary>
/// <remarks>
/// This project contains benchmarks comparing old vs. new implementations
/// for runtime modernization and streaming/transport experiments.
///
/// Run with: dotnet run -c Release --project ConduitLLM.Benchmarks
///
/// The <c>transport-throughput</c> mode is not a BenchmarkDotNet benchmark — it drives a
/// live Wolverine host against a real PostgreSQL queue to measure sustained delivery
/// throughput and headroom (#1223):
/// Use scripts/performance/phase3/run-queue-matrix.ps1 with an explicit private
/// PostgreSQL maintenance connection and a fresh evidence directory.
/// </remarks>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("writer-micro", StringComparison.Ordinal))
        {
            return await WriterMicroHarness.RunAsync(args[1..]).ConfigureAwait(false);
        }
        if (args.Length > 0 && args[0].Equals("collector-micro", StringComparison.Ordinal))
        {
            return await CollectorMicroHarness.RunAsync(args[1..]).ConfigureAwait(false);
        }
        if (args.Length > 0 && args[0].Equals("provider-micro", StringComparison.Ordinal))
        {
            return await ProviderMicroHarness.RunAsync(args[1..]).ConfigureAwait(false);
        }
#if !STREAMING_MICRO_ONLY
        if (args.Length > 0 && args[0].Equals("transport-throughput", StringComparison.OrdinalIgnoreCase))
        {
            return await TransportThroughputHarness.RunAsync(args[1..]).ConfigureAwait(false);
        }
#endif

        _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
