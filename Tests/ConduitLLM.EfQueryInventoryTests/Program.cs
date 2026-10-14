using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConduitLLM.EfQueryInventory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace ConduitLLM.EfQueryInventoryTests;

internal static class Program
{
    private const string CallsiteId = "CEQ0001";
    private const string CompilationId = "CEQ0000";
    private const string ExcludedId = "CEQ0002";
    private static readonly SymbolDisplayFormat EvidenceSymbolFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private static readonly List<object> Results = [];
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        var root = FindRepositoryRoot();
        var referenceDirectory = args.Length == 1 ? Path.GetFullPath(args[0]) :
            Path.Combine(root, "Services", "ConduitLLM.Gateway", "bin", "Debug", "net10.0");
        try
        {
            var references = LoadReferences(referenceDirectory, root, args.Length == 0);
            Console.WriteLine($"Managed reference directory: {referenceDirectory}");
            Console.WriteLine($"Roslyn: {typeof(Compilation).Assembly.GetName().Version}; references: {references.Length}");
            var fixtureDirectory = Path.Combine(root, "Tests", "ConduitLLM.EfQueryInventoryTests", "Fixtures");
            var semanticPath = Path.Combine(fixtureDirectory, "SemanticCases.cs");
            var generatedPath = Path.Combine(fixtureDirectory, "GeneratedQueries.g.cs");
            var fixtureSources = new[] { semanticPath, generatedPath };
            var jit = await AnalyzeAsync("SemanticCases.Jit", fixtureSources, references, []);
            Check("JIT fixtures bind against real EF, Npgsql, and framework references", () => NoCompilerErrors(jit));
            Check("all callsites retain actual bound method, assembly, caller, and source spans", () => AuditEvidence(jit));
            Check("custom same-named async terminal is a semantic negative", () =>
                ExpectCall(jit, "CustomSameNamedTerminal", "ToListAsync", "negative.custom", "SemanticCases.Jit"));
            Check("ordinary Enumerable materialization is a semantic negative", () =>
                ExpectCall(jit, "EnumerableMaterializer", "ToList", "negative.enumerable", "System.Linq"));
            Check("Enumerable over EF root records synchronous enumeration", () =>
                ExpectCall(jit, "EnumerableTerminal", "Count", "ef.sync-enumeration", "System.Linq"));
            Check("inline DbContext.Set chain proves the root", () =>
                ExpectRoot(jit, "InlineEfRoot", "ToListAsync", "proven", "dbcontext-set"));
            Check("namespace alias static call still binds to real EF", () =>
                ExpectRoot(jit, "AliasStaticEfRoot", "ToListAsync", "proven", "dbset-property"));
            Check("named arguments preserve the query operand despite source reordering", () =>
                ExpectRoot(jit, "ReorderedNamedArgumentRoot", "ToListAsync", "proven", "dbset-property"));
            Check("Queryable sync terminal preserves a proven EF root", () =>
            {
                ExpectCall(jit, "QueryableTerminal", "Count", "queryable.sync-terminal", "System.Linq.Queryable");
                ExpectRoot(jit, "QueryableTerminal", "Count", "proven", "dbset-property");
            });
            Check("Queryable parameter is a potential query with unknown provider", () =>
                ExpectUnknownDependency(jit, "UnknownQueryableParameter", "Count", "parameter", "query"));
            Check("mutable IQueryable alias remains unknown", () =>
                ExpectUnknownDependency(jit, "MutableAliasTerminal", "ToListAsync", "local", "query"));
            Check("query syntax alias remains unknown and compiler binds its operators", () =>
            {
                ExpectUnknownDependency(jit, "QuerySyntaxTerminal", "ToListAsync", "local", "query");
                ExpectCall(jit, "QuerySyntaxTerminal", "Where", "queryable.query-operator", "System.Linq.Queryable");
            });
            Check("opaque helper return remains unknown", () =>
                ExpectUnknownDependency(jit, "OpaqueHelperTerminal", "ToListAsync", "helper-return", "CreateQuery"));
            Check("base generic helper terminal retains unknown dependencies", () =>
                ExpectUnknownDependency(jit, "BaseGenericTerminal", "ToListAsync", null, "ApplyDefaultIncludes"));
            Check("expression parameter remains an explicit unknown dependency", () =>
            {
                var record = FindCall(jit, "ExpressionArgumentTerminal", "ToListAsync");
                Require(record.Evidence.GetProperty("queryRoot").GetProperty("status").GetString() == "unknown",
                    "A non-inline expression must make the complete chain unknown.");
                Require(record.Evidence.GetProperty("queryRoot").GetProperty("dependencies").EnumerateArray()
                    .Any(item => item.GetProperty("symbol").GetString()!.Contains("predicate", StringComparison.Ordinal)),
                    "The predicate parameter dependency was omitted.");
            });
            Check("Npgsql provider function uses its real provider assembly", () =>
                ExpectCall(jit, "NpgsqlPredicateTerminal", "JsonContains", "ef.provider-function", "Npgsql.EntityFrameworkCore.PostgreSQL"));
            Check("raw SQL command and query operations are distinguished", () =>
            {
                ExpectCall(jit, "RawSqlCommand", "ExecuteSqlRawAsync", "ef.raw-sql", "Microsoft.EntityFrameworkCore.Relational");
                ExpectCall(jit, "RawSqlQuery", "FromSqlRaw", "ef.raw-sql", "Microsoft.EntityFrameworkCore.Relational");
            });
            Check("EF mutation operations retain real method identities", () =>
            {
                ExpectCall(jit, "BulkMutation", "ExecuteDeleteAsync", "ef.mutation", "Microsoft.EntityFrameworkCore");
                ExpectCall(jit, "SaveMutation", "SaveChangesAsync", "ef.mutation", "Microsoft.EntityFrameworkCore");
            });
            Check("Npgsql ADO operation binds to the real command assembly", () =>
                ExpectCall(jit, "NpgsqlAdoCommand", "ExecuteNonQueryAsync", "ado", "Npgsql"));
            Check("generated sources are emitted with explicit provenance", () =>
            {
                var record = FindCall(jit, "GeneratedTerminal", "ToListAsync");
                Require(record.Evidence.GetProperty("generated").GetBoolean(), "Generated callsite was marked authored.");
                var authored = FindCall(jit, "InlineEfRoot", "ToListAsync");
                Require(!authored.Evidence.GetProperty("generated").GetBoolean(), "Authored callsite was marked generated.");
            });
            Check("an auto-generated marker inside user data does not change source provenance", () =>
                Require(jit.Calls.Where(call => call.Diagnostic.Location.SourceTree!.FilePath.EndsWith("SemanticCases.cs", StringComparison.Ordinal))
                    .All(call => !call.Evidence.GetProperty("generated").GetBoolean()), "User data was mistaken for a generated header."));
            Check("compilation sentinel includes sources and real reference identities", () => AuditSentinel(jit, references));
            Check("JIT profile excludes native-only source", () => AssertProfile(jit, "JitProfileOnly", "NativeProfileOnly"));
            Check("inactive source receives lexical evidence without bound symbol claims", () => AuditExcluded(jit));

            var native = await AnalyzeAsync("SemanticCases.Native", fixtureSources, references, ["CONDUIT_NATIVE_AOT"]);
            Check("native fixtures compile and bind", () => NoCompilerErrors(native));
            Check("native profile excludes JIT-only source", () => AssertProfile(native, "NativeProfileOnly", "JitProfileOnly"));
            Check("native defines are retained as compilation evidence", () =>
            {
                var record = FindCall(native, "NativeProfileOnly", "ToListAsync");
                Require(record.Evidence.GetProperty("compilation").GetProperty("defines").EnumerateArray()
                    .Any(value => value.GetString() == "CONDUIT_NATIVE_AOT"), "Native profile define was omitted.");
            });

            var discoveryPath = Path.Combine(root, "Shared", "ConduitLLM.Core", "Services", "DiscoveryModelProjector.cs");
            var discovery = await AnalyzeAsync("DiscoverySource", [discoveryPath], references, []);
            Check("actual current discovery projector compiles against existing managed output", () => NoCompilerErrors(discovery));
            Check("actual discovery chain preserves terminal and navigation evidence", () =>
            {
                var terminal = FindCall(discovery, "ProjectAsync", "ToListAsync");
                Require(terminal.Classification == "ef.async-terminal", "Actual discovery terminal classification changed.");
                ExpectRoot(discovery, "ProjectAsync", "ToListAsync", "proven", "dbset-property");
                var calls = discovery.Calls.Where(call => call.Caller.Name == "ProjectAsync").ToArray();
                Require(calls.Count(call => call.Method.Name == "ToListAsync") == 1, "Discovery must have one source terminal callsite.");
                Require(calls.Count(call => call.Method.Name == "Include") == 3, "Discovery Include branches were lost.");
                Require(calls.Count(call => call.Method.Name == "ThenInclude") == 3, "Discovery ThenInclude branches were lost.");
                AuditEvidence(discovery);
            });
            var functionDiscoveryPath = Path.Combine(root, "Shared", "ConduitLLM.Configuration", "Repositories", "EfFunctionDiscoveryRuntimeStore.cs");
            var functionDiscovery = await AnalyzeAsync("FunctionDiscoverySource.Jit", [functionDiscoveryPath], references, []);
            Check("actual four-branch function discovery source compiles", () => NoCompilerErrors(functionDiscovery));
            Check("all four function discovery terminal branches retain full chain evidence", () =>
            {
                var terminals = functionDiscovery.Calls.Where(call => call.SourceOwner == "ListEnabledAsync" && call.Method.Name == "ToListAsync").ToArray();
                Require(terminals.Length == 4, $"Expected four source terminal branches, got {terminals.Length}.");
                foreach (var terminal in terminals)
                {
                    Require(terminal.Classification == "ef.async-terminal", "A real EF terminal was reclassified.");
                    var queryRoot = terminal.Evidence.GetProperty("queryRoot");
                    Require(queryRoot.GetProperty("status").GetString() == "proven" &&
                        queryRoot.GetProperty("roots").EnumerateArray().Any(item =>
                            item.GetProperty("symbol").GetString()!.Contains("FunctionConfigurations", StringComparison.Ordinal)),
                        "A full FunctionConfigurations branch lost its bound root.");
                    var operation = terminal.Operation;
                    Require(operation.Arguments.Any(argument => argument.Parameter?.Name == "cancellationToken" &&
                        argument.Value is IParameterReferenceOperation { Parameter.Name: "cancellationToken" }),
                        "Terminal did not preserve the actual cancellation parameter binding.");
                }
                Require(terminals.Select(call => call.Diagnostic.Location.SourceSpan.Start).Distinct().Count() == 4,
                    "Four source branches collapsed onto one location.");
                AuditEvidence(functionDiscovery);
            });
            var functionNative = await AnalyzeAsync("FunctionDiscoverySource.Native", [functionDiscoveryPath], references, ["CONDUIT_NATIVE_AOT"]);
            Check("native profile excludes actual JIT function discovery source semantically", () =>
            {
                NoCompilerErrors(functionNative);
                Require(functionNative.Calls.Length == 0, "Excluded JIT store emitted bound callsites in native profile.");
                AuditExcluded(functionNative);
            });
            Check("inventory makes no physical SQL query-count or raw SQL text claim", () =>
            {
                foreach (var batch in new[] { jit, native, discovery, functionDiscovery, functionNative })
                {
                    foreach (var record in batch.Diagnostics)
                    {
                        AuditNoQueryCountClaims(record.Evidence);
                        Require(!record.Evidence.GetRawText().Contains("DELETE FROM fixture_contract", StringComparison.Ordinal) &&
                            !record.Evidence.GetRawText().Contains("SELECT * FROM fixture_contract", StringComparison.Ordinal),
                            "Raw SQL text leaked into evidence.");
                    }
                }
            });
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                await jit.Compilation.WithAnalyzers([new EfQueryInventoryAnalyzer()])
                    .GetAnalyzerDiagnosticsAsync(canceled.Token);
                Check("canceled analysis respects cancellation", () => Require(false, "Canceled analysis completed normally."));
            }
            catch (OperationCanceledException)
            {
                Check("canceled analysis respects cancellation", () => { });
            }
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FATAL: {exception}");
            Results.Add(new { test = "runner setup", passed = false, failure = exception.ToString() });
        }

        var logPath = Path.Combine(AppContext.BaseDirectory, "contract-results.json");
        await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(new { passed = _passed, failed = _failed, results = Results },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Contract results: {_passed} passed, {_failed} failed; {logPath}");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task<Batch> AnalyzeAsync(string assemblyName, IEnumerable<string> paths,
        ImmutableArray<MetadataReference> references, string[] defines)
    {
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithPreprocessorSymbols(defines);
        var trees = paths.Select(path => CSharpSyntaxTree.ParseText(
            SourceText.From(File.ReadAllText(path), Encoding.UTF8, SourceHashAlgorithm.Sha256), options, path)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; " +
            "global using System.Linq; global using System.Threading; global using System.Threading.Tasks;", options,
            Path.Combine(FindRepositoryRoot(), "Tests", "ConduitLLM.EfQueryInventoryTests", "FixtureGlobalUsings.g.cs")));
        var compilation = CSharpCompilation.Create(assemblyName, trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty,
            new FixtureOptionsProvider(Path.Combine(FindRepositoryRoot(), "Tests", "ConduitLLM.EfQueryInventoryTests", "ConduitLLM.EfQueryInventoryTests.csproj")));
        var diagnostics = await compilation.WithAnalyzers([new EfQueryInventoryAnalyzer()], analyzerOptions).GetAnalyzerDiagnosticsAsync();
        var records = diagnostics.Select(diagnostic => new Record(diagnostic, ReadEvidence(diagnostic))).ToArray();
        var calls = records.Where(record => record.Diagnostic.Id == CallsiteId).Select(record => BindRecord(compilation, record)).ToArray();
        Console.WriteLine($"ANALYZED {assemblyName}: {trees.Count} source trees, {calls.Length} callsite records");
        Results.Add(new
        {
            compilation = assemblyName,
            sources = trees.Select(tree => new { path = tree.FilePath, sha256 = Sha256(tree.GetText().ToString()) }),
            compilerErrors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString()),
            analyzerRecords = records.Select(record => record.Evidence)
        });
        return new Batch(compilation, records, calls);
    }

    private static JsonElement ReadEvidence(Diagnostic diagnostic)
    {
        Require(diagnostic.Id is CallsiteId or CompilationId or ExcludedId, $"Unexpected analyzer diagnostic: {diagnostic}");
        Require(diagnostic.Properties.TryGetValue("schemaVersion", out var version) && version == "1", "Missing schemaVersion=1.");
        Require(diagnostic.Properties.TryGetValue("evidence", out var evidence) && evidence is not null, "Missing evidence JSON.");
        return JsonDocument.Parse(evidence!).RootElement.Clone();
    }

    private static BoundCall BindRecord(CSharpCompilation compilation, Record record)
    {
        var tree = record.Diagnostic.Location.SourceTree ?? throw new InvalidOperationException("Callsite has no source tree.");
        var model = compilation.GetSemanticModel(tree);
        var collector = new InvocationCollector();
        foreach (var method in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (model.GetOperation(method) is { } operation)
            {
                collector.Visit(operation);
            }
        }
        var span = record.Diagnostic.Location.SourceSpan;
        var target = collector.Invocations.FirstOrDefault(operation => operation.Syntax.Span == span);
        if (target is null)
        {
            var node = tree.GetRoot().FindNode(span, getInnermostNodeForTie: true);
            target = model.GetOperation(node) as IInvocationOperation;
        }
        Require(target is not null, $"Diagnostic span does not identify a bound invocation: {record.Diagnostic}");
        var caller = model.GetEnclosingSymbol(span.Start) as IMethodSymbol;
        while (caller is { MethodKind: MethodKind.AnonymousFunction })
        {
            caller = caller.ContainingSymbol as IMethodSymbol;
        }
        Require(caller is not null, $"No callable source owner at {record.Diagnostic.Location}.");
        var sourceOwner = target!.Syntax.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? caller!.Name;
        return new BoundCall(record.Diagnostic, record.Evidence, target.TargetMethod, caller!, sourceOwner, target);
    }

    private static void AuditEvidence(Batch batch)
    {
        foreach (var call in batch.Calls)
        {
            var evidence = call.Evidence;
            Require(evidence.GetProperty("targetMethod").GetString() == call.Method.ToDisplayString(EvidenceSymbolFormat),
                "Reported target method disagrees with Roslyn binding.");
            Require(evidence.GetProperty("originalMethod").GetString() == call.Method.OriginalDefinition.ToDisplayString(EvidenceSymbolFormat),
                "Reported original method disagrees with the actual generic definition.");
            var bound = batch.Compilation.GetSemanticModel(call.Operation.Syntax.SyntaxTree)
                .GetSymbolInfo(call.Operation.Syntax).Symbol as IMethodSymbol;
            Require(evidence.GetProperty("boundMethod").GetString() == bound?.ToDisplayString(EvidenceSymbolFormat),
                "Reported syntax binding disagrees with the compiler.");
            Require(evidence.GetProperty("reducedFrom").GetString() == (bound?.ReducedFrom ?? call.Method.ReducedFrom)?.ToDisplayString(EvidenceSymbolFormat),
                "Reported extension reduction disagrees with the compiler.");
            Require(evidence.GetProperty("declaringType").GetString() == call.Method.ContainingType.ToDisplayString(EvidenceSymbolFormat),
                "Reported declaring type disagrees with Roslyn binding.");
            Require(evidence.GetProperty("targetAssembly").GetString() == call.Method.ContainingAssembly.Identity.ToString(),
                "Reported target assembly disagrees with Roslyn binding.");
            Require(evidence.GetProperty("caller").GetString() == call.Caller.ToDisplayString(EvidenceSymbolFormat),
                $"Reported caller disagrees with source owner at {call.Diagnostic.Location}: expected {call.Caller.ToDisplayString(EvidenceSymbolFormat)}, got {evidence.GetProperty("caller").GetString()}.");
            Require(evidence.GetProperty("typeArguments").EnumerateArray().Select(value => value.GetString())
                .SequenceEqual(call.Method.TypeArguments.Select(type => type.ToDisplayString(EvidenceSymbolFormat))),
                "Reported generic type arguments disagree with the bound method.");
            var location = call.Diagnostic.Location;
            Require(evidence.GetProperty("file").GetString() == location.SourceTree!.FilePath, "Reported source path changed.");
            var span = evidence.GetProperty("span");
            Require(span.GetProperty("start").GetInt32() == location.SourceSpan.Start &&
                span.GetProperty("length").GetInt32() == location.SourceSpan.Length, "Reported source span changed.");
            var actualLine = location.GetLineSpan();
            Require(span.GetProperty("line").GetInt32() == actualLine.StartLinePosition.Line + 1 &&
                span.GetProperty("column").GetInt32() == actualLine.StartLinePosition.Character + 1 &&
                span.GetProperty("endLine").GetInt32() == actualLine.EndLinePosition.Line + 1 &&
                span.GetProperty("endColumn").GetInt32() == actualLine.EndLinePosition.Character + 1, "Reported line/column span changed.");
            var checksum = evidence.GetProperty("sourceChecksum");
            var sourceText = location.SourceTree.GetText();
            Require(checksum.GetProperty("algorithm").GetString() == sourceText.ChecksumAlgorithm.ToString() &&
                checksum.GetProperty("value").GetString() == Convert.ToHexString(sourceText.GetChecksum().AsSpan()),
                "Reported checksum disagrees with the actual compiler source text.");
            Require(call.Diagnostic.Properties["recordKind"] == "callsite", "Callsite record kind changed.");
            Require(call.Diagnostic.Severity == DiagnosticSeverity.Info, "Inventory must remain an informational analyzer.");
        }
    }

    private static void AuditSentinel(Batch batch, ImmutableArray<MetadataReference> references)
    {
        var sentinel = batch.Diagnostics.Single(record => record.Diagnostic.Id == CompilationId);
        Require(sentinel.Diagnostic.Location == Location.None, "Compilation sentinel must have no source location.");
        Require(sentinel.Diagnostic.Properties["recordKind"] == "compilation", "Compilation record kind changed.");
        Require(sentinel.Evidence.GetProperty("syntaxTrees").GetArrayLength() == batch.Compilation.SyntaxTrees.Count(), "Sentinel omitted a source tree.");
        var actualReferences = sentinel.Evidence.GetProperty("references").GetRawText();
        Require(actualReferences.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) &&
            actualReferences.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal), "Sentinel omitted real EF/provider identities.");
        Require(sentinel.Evidence.GetProperty("references").GetArrayLength() == references.Length, "Sentinel omitted a metadata reference.");
        var identity = sentinel.Evidence.GetProperty("compilation");
        Require(identity.GetProperty("projectPath").GetString() ==
            Path.Combine(FindRepositoryRoot(), "Tests", "ConduitLLM.EfQueryInventoryTests", "ConduitLLM.EfQueryInventoryTests.csproj") &&
            identity.GetProperty("rootNamespace").GetString() == "InventoryContractFixtures", "Analyzer config provenance was omitted.");
    }

    private static void NoCompilerErrors(Batch batch)
    {
        var errors = batch.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Require(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
    }

    private static BoundCall FindCall(Batch batch, string callerName, string methodName) =>
        batch.Calls.Single(call => call.SourceOwner == callerName && call.Method.Name == methodName);

    private static void ExpectCall(Batch batch, string callerName, string methodName, string classification, string assembly)
    {
        var call = FindCall(batch, callerName, methodName);
        Require(call.Classification == classification, $"{callerName}.{methodName}: expected {classification}, got {call.Classification}.");
        Require(call.Method.ContainingAssembly.Identity.Name == assembly,
            $"{callerName}.{methodName}: expected assembly {assembly}, got {call.Method.ContainingAssembly.Identity.Name}.");
    }

    private static void ExpectRoot(Batch batch, string callerName, string methodName, string status, string rootKind)
    {
        var root = FindCall(batch, callerName, methodName).Evidence.GetProperty("queryRoot");
        Require(root.GetProperty("status").GetString() == status, $"{callerName}: expected root {status}, got {root}.");
        Require(root.GetProperty("roots").EnumerateArray().Any(item => item.GetProperty("kind").GetString() == rootKind),
            $"{callerName}: missing {rootKind} root.");
    }

    private static void ExpectUnknownDependency(Batch batch, string callerName, string methodName, string? kind, string symbol)
    {
        var root = FindCall(batch, callerName, methodName).Evidence.GetProperty("queryRoot");
        Require(root.GetProperty("status").GetString() == "unknown", $"{callerName}: dependency was falsely proven.");
        Require(root.GetProperty("dependencies").EnumerateArray().Any(item =>
            (kind is null || item.GetProperty("kind").GetString() == kind) &&
            item.GetProperty("symbol").GetString()!.Contains(symbol, StringComparison.Ordinal)), $"{callerName}: missing {symbol} dependency.");
    }

    private static void AssertProfile(Batch batch, string includedCaller, string excludedCaller)
    {
        FindCall(batch, includedCaller, "ToListAsync");
        Require(batch.Calls.All(call => call.SourceOwner != excludedCaller), $"Inactive {excludedCaller} source was included.");
    }

    private static void AuditExcluded(Batch batch)
    {
        var excluded = batch.Diagnostics.Where(record => record.Diagnostic.Id == ExcludedId).ToArray();
        Require(excluded.Length > 0, "Inactive query-shaped source lost its lexical provenance.");
        foreach (var record in excluded)
        {
            Require(record.Diagnostic.Properties["recordKind"] == "lexical-excluded" &&
                record.Diagnostic.Properties["classification"] == "preprocessor-excluded", "Inactive source was treated as a semantic record.");
            Require(record.Evidence.GetProperty("reason").GetString() == "inactive-preprocessor-text", "Exclusion reason changed.");
            Require(record.Evidence.GetProperty("candidateNames").EnumerateArray().Any(value => value.GetString() == "ToListAsync"),
                "Inactive async candidate was omitted.");
            Require(!record.Evidence.TryGetProperty("targetMethod", out _) && !record.Evidence.TryGetProperty("caller", out _),
                "Inactive lexical text falsely claimed a bound method or caller.");
        }
    }

    private static void AuditNoQueryCountClaims(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                Require(property.Name is not ("queryCount" or "sqlQueryCount" or "physicalQueryCount" or "estimatedQueryCount"),
                    $"Source inventory cannot claim physical SQL counts: {property.Name}.");
                AuditNoQueryCountClaims(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) AuditNoQueryCountClaims(item);
        }
    }

    private static ImmutableArray<MetadataReference> LoadReferences(string referenceDirectory, string root, bool preferCurrentSharedOutput)
    {
        Require(Directory.Exists(referenceDirectory), $"Missing existing managed build output: {referenceDirectory}. Build it separately or pass another directory.");
        var dotnetRoot = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (!Directory.Exists(Path.Combine(dotnetRoot, "packs")))
        {
            dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packName in new[] { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" })
        {
            var packDirectory = Path.Combine(dotnetRoot, "packs", packName);
            var versionDirectory = Directory.GetDirectories(packDirectory).Where(path => Path.GetFileName(path).StartsWith("10.", StringComparison.Ordinal))
                .OrderByDescending(path => Version.Parse(Path.GetFileName(path))).First();
            foreach (var path in Directory.GetFiles(Path.Combine(versionDirectory, "ref", "net10.0"), "*.dll"))
            {
                paths.TryAdd(Path.GetFileName(path), path);
            }
        }
        foreach (var path in Directory.GetFiles(referenceDirectory, "*.dll"))
        {
            paths.TryAdd(Path.GetFileName(path), path);
        }
        if (preferCurrentSharedOutput)
        {
            foreach (var project in new[] { "ConduitLLM.Configuration", "ConduitLLM.Persistence.Abstractions", "ConduitLLM.Contracts" })
            {
                var currentPath = Path.Combine(root, "Shared", project, "bin", "Release", "net10.0", $"{project}.dll");
                if (File.Exists(currentPath)) paths[$"{project}.dll"] = currentPath;
            }
        }
        foreach (var required in new[] { "Microsoft.EntityFrameworkCore.dll", "Microsoft.EntityFrameworkCore.Relational.dll",
            "Npgsql.EntityFrameworkCore.PostgreSQL.dll", "ConduitLLM.Configuration.dll", "ConduitLLM.Core.dll" })
        {
            Require(paths.ContainsKey(required), $"Missing real reference {required} in {referenceDirectory}.");
            Console.WriteLine($"REFERENCE {AssemblyName.GetAssemblyName(paths[required]).FullName}");
        }
        return paths.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
            (MetadataReference)MetadataReference.CreateFromFile(item.Value)).ToImmutableArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "Shared")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate Conduit repository root.");
    }

    private static string Sha256(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static void Check(string name, Action assertion)
    {
        try
        {
            assertion();
            _passed++;
            Console.WriteLine($"PASS {name}");
            Results.Add(new { test = name, passed = true });
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            Results.Add(new { test = name, passed = false, failure = exception.ToString() });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Record(Diagnostic Diagnostic, JsonElement Evidence);
    private sealed record Batch(CSharpCompilation Compilation, Record[] Diagnostics, BoundCall[] Calls);
    private sealed record BoundCall(Diagnostic Diagnostic, JsonElement Evidence, IMethodSymbol Method,
        IMethodSymbol Caller, string SourceOwner, IInvocationOperation Operation)
    {
        public string Classification => Diagnostic.Properties["classification"]!;
    }

    private sealed class InvocationCollector : OperationWalker
    {
        public List<IInvocationOperation> Invocations { get; } = [];
        public override void VisitInvocation(IInvocationOperation operation)
        {
            Invocations.Add(operation);
            base.VisitInvocation(operation);
        }
    }

    private sealed class FixtureOptionsProvider(string projectPath) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new FixtureOptions(new Dictionary<string, string>
        {
            ["build_property.MSBuildProjectFullPath"] = projectPath,
            ["build_property.RootNamespace"] = "InventoryContractFixtures"
        });
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new FixtureOptions([]);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new FixtureOptions([]);
    }

    private sealed class FixtureOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
