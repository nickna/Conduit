Run this bounded developer contract suite after a managed Gateway build exists:

```powershell
dotnet run --project Tests/ConduitLLM.EfQueryInventoryTests/ConduitLLM.EfQueryInventoryTests.csproj --no-launch-profile
```

An optional single argument selects another existing managed output directory. The runner reads
.NET 10 framework reference packs and the existing output assemblies. By default, available
`Shared` Release output for Configuration, Persistence.Abstractions, and Contracts takes precedence
over older copies in Gateway output. It then creates real Roslyn
compilations and executes `EfQueryInventoryAnalyzer` with `CompilationWithAnalyzers`. It does not
start a database or rebuild the service projects. Its project reference builds only the inventory
tool. No test-framework packages are required.

Fixtures deliberately bind to actual EF Core and Npgsql methods. The suite checks real source
spans, method/assembly/caller evidence, roots and unknown dependencies, semantic negatives,
generated source provenance, compilation defines, cancellation, raw SQL and mutation categories,
and the current discovery projector source. Its callsite counts describe source operations;
they do not establish runtime SQL command counts.

The runner exits with a nonzero code on failure and saves detailed evidence, source SHA-256
hashes, and assertion results to `bin/<configuration>/net10.0/contract-results.json`.

After the semantic runner has built the analyzer, run the separate resume integrity contracts:

```powershell
pwsh -NoProfile -File Tests/ConduitLLM.EfQueryInventoryTests/ResumeContracts.ps1
```

These use small synthetic capture fixtures and the production resume validator and entry point.
They check that changed SARIF, compiler inputs, references, command output, and missing or changed
seals are rejected before collector child setup. The fixtures have an independent argument guard
that prevents service builds if preflight regresses. Each run retains its result JSON and private
startup logs under a fresh `bin/resume-contracts-<id>/` directory.
