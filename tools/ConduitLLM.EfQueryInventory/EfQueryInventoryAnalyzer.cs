using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ConduitLLM.EfQueryInventory;

/// <summary>
/// Inventories resolved compiler operations. Root evidence is deliberately local:
/// this does not prove SQL translation, runtime reachability, or AOT compatibility.
/// The collector must reconcile compilation receipts before claiming coverage.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EfQueryInventoryAnalyzer : DiagnosticAnalyzer
{
    public const string CompilationDiagnosticId = "CEQ0000";
    public const string CallsiteDiagnosticId = "CEQ0001";
    public const string ExcludedDiagnosticId = "CEQ0002";

    private static readonly DiagnosticDescriptor CompilationRule = new(
        CompilationDiagnosticId, "EF inventory compilation receipt",
        "EF inventory compilation receipt for {0}", "EfQueryInventory",
        DiagnosticSeverity.Info, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CallsiteRule = new(
        CallsiteDiagnosticId, "Resolved query inventory callsite",
        "Resolved query inventory: {0} ({1})", "EfQueryInventory",
        DiagnosticSeverity.Info, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ExcludedRule = new(
        ExcludedDiagnosticId, "Inactive preprocessor lexical observation",
        "Inactive preprocessor text contains {0} query-shaped names (lexical evidence only)",
        "EfQueryInventory", DiagnosticSeverity.Info, isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat SymbolFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly ImmutableHashSet<string> AsyncTerminals = Names(
        "ToListAsync", "ToArrayAsync", "ToDictionaryAsync", "ToHashSetAsync", "LoadAsync",
        "FirstAsync", "FirstOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync",
        "LastAsync", "LastOrDefaultAsync", "ElementAtAsync", "ElementAtOrDefaultAsync",
        "AnyAsync", "AllAsync", "ContainsAsync", "CountAsync", "LongCountAsync",
        "MinAsync", "MaxAsync", "SumAsync", "AverageAsync", "AggregateAsync",
        "MinByAsync", "MaxByAsync");

    private static readonly ImmutableHashSet<string> SyncTerminals = Names(
        "ToList", "ToArray", "ToDictionary", "ToHashSet", "Load", "First", "FirstOrDefault",
        "Single", "SingleOrDefault", "Last", "LastOrDefault", "ElementAt", "ElementAtOrDefault",
        "Any", "All", "Contains", "Count", "LongCount", "Min", "Max", "Sum", "Average",
        "Aggregate", "MinBy", "MaxBy", "SequenceEqual");

    private static readonly ImmutableHashSet<string> Mutations = Names(
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
        "SaveChanges", "SaveChangesAsync", "Add", "AddAsync", "AddRange", "AddRangeAsync",
        "Attach", "AttachRange", "Update", "UpdateRange", "Remove", "RemoveRange");

    private static readonly ImmutableHashSet<string> RawSqlMethods = Names(
        "FromSql", "FromSqlRaw", "FromSqlInterpolated", "SqlQuery", "SqlQueryRaw",
        "ExecuteSql", "ExecuteSqlAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync",
        "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync");

    private static readonly ImmutableHashSet<string> AdoMethods = Names(
        "ExecuteReader", "ExecuteReaderAsync", "ExecuteScalar", "ExecuteScalarAsync",
        "ExecuteNonQuery", "ExecuteNonQueryAsync");

    private static readonly ImmutableHashSet<string> LexicalCandidates = AsyncTerminals
        .Union(SyncTerminals).Union(Mutations).Union(RawSqlMethods).Union(AdoMethods)
        .Union(Names("Include", "ThenInclude", "AsNoTracking", "AsTracking", "AsSplitQuery",
            "AsSingleQuery", "AsAsyncEnumerable", "Where", "Select", "SelectMany", "Find", "FindAsync", "Set"));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CompilationRule, CallsiteRule, ExcludedRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(start =>
        {
            var identity = CompilationIdentity(start.Compilation, start.Options);
            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, identity),
                OperationKind.Invocation);
            start.RegisterCompilationEndAction(end => ReportCompilation(end, identity));
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, object compilationIdentity)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var source = QuerySource(invocation);
        var queryRoot = TraceRoot(source);
        var expressionDependencies = ExpressionDependencies(invocation);
        if (expressionDependencies.Count > 0)
        {
            queryRoot.Dependencies.AddRange(expressionDependencies);
        }

        var classification = Classify(method, queryRoot);
        if (classification is null)
        {
            return;
        }

        var tree = invocation.Syntax.SyntaxTree;
        var location = invocation.Syntax.GetLocation();
        var lineSpan = location.GetLineSpan();
        var bound = context.Compilation.GetSemanticModel(tree)
            .GetSymbolInfo(invocation.Syntax, context.CancellationToken).Symbol as IMethodSymbol;
        var reduced = bound?.ReducedFrom ?? method.ReducedFrom;
        var evidence = new
        {
            targetMethod = Display(method),
            boundMethod = Display(bound),
            originalMethod = Display(method.OriginalDefinition),
            reducedFrom = Display(reduced),
            targetAssembly = method.ContainingAssembly.Identity.ToString(),
            declaringType = Display(method.ContainingType),
            typeArguments = method.TypeArguments.Select(Display).ToArray(),
            caller = Display(context.ContainingSymbol),
            file = tree.FilePath,
            span = new
            {
                start = location.SourceSpan.Start,
                length = location.SourceSpan.Length,
                line = lineSpan.StartLinePosition.Line + 1,
                column = lineSpan.StartLinePosition.Character + 1,
                endLine = lineSpan.EndLinePosition.Line + 1,
                endColumn = lineSpan.EndLinePosition.Character + 1
            },
            sourceChecksum = Checksum(tree, context.CancellationToken),
            generated = IsGenerated(tree, context.CancellationToken),
            compilation = compilationIdentity,
            queryRoot = new
            {
                status = queryRoot.Status,
                roots = queryRoot.Roots.Distinct().ToArray(),
                dependencies = queryRoot.Dependencies.Distinct().ToArray()
            },
            expressionDependencies = expressionDependencies.Distinct().ToArray()
        };
        context.ReportDiagnostic(Diagnostic.Create(CallsiteRule, location,
            Properties("callsite", classification, evidence), method.Name, classification));
    }

    private static string? Classify(IMethodSymbol target, RootEvidence root)
    {
        var method = target.ReducedFrom ?? target;
        var type = method.ContainingType;
        var name = method.Name;
        if (IsEfType(type, "Microsoft.EntityFrameworkCore", "EntityFrameworkQueryableExtensions")
            || IsEfType(type, "Microsoft.EntityFrameworkCore", "RelationalQueryableExtensions"))
        {
            if (RawSqlMethods.Contains(name)) return "ef.raw-sql";
            if (Mutations.Contains(name)) return "ef.mutation";
            if (AsyncTerminals.Contains(name)) return "ef.async-terminal";
            if (SyncTerminals.Contains(name)) return "ef.sync-terminal";
            if (name == "AsAsyncEnumerable") return "ef.deferred";
            // Only methods with a query operand are query operators (not EF.CompileQuery).
            if (method.Parameters.Any(parameter => IsQueryType(parameter.Type)))
                return "ef.query-operator";
        }

        if (IsEfType(type, "Microsoft.EntityFrameworkCore", "RelationalDatabaseFacadeExtensions")
            && RawSqlMethods.Contains(name)) return "ef.raw-sql";

        if (IsEfType(type, "Microsoft.EntityFrameworkCore", "DbSet`1") || IsCanonicalDbContext(type))
        {
            if (name is "Find" or "FindAsync")
                return name.EndsWith("Async", StringComparison.Ordinal)
                    ? "ef.async-terminal" : "ef.sync-terminal";
            if (Mutations.Contains(name)) return "ef.mutation";
        }

        if (IsProviderFunction(method)) return "ef.provider-function";
        if (AdoMethods.Contains(name) && Inherits(type, "System.Data.Common", "DbCommand", "System.Data.Common"))
            return "ado";

        if (IsLinqType(type, "Queryable") && root.Status != "none")
            return SyncTerminals.Contains(name) ? "queryable.sync-terminal" : "queryable.query-operator";

        if (IsLinqType(type, "Enumerable") && SyncTerminals.Contains(name))
            return root.Status == "none" ? "negative.enumerable" : "ef.sync-enumeration";

        // Negative evidence is based on the resolved non-EF symbol, never name matching proof.
        if (AsyncTerminals.Contains(name) || RawSqlMethods.Contains(name)
            || name is "FindAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync")
            return "negative.custom";
        return null;
    }

    private static RootEvidence TraceRoot(IOperation? operation, int depth = 0)
    {
        var result = new RootEvidence();
        operation = Unwrap(operation);
        if (operation is null) return result;
        if (depth > 48)
        {
            result.Dependencies.Add(Dependency("depth-limit", null, operation.Type));
            return result;
        }

        switch (operation)
        {
            case IPropertyReferenceOperation property when IsDbSet(property.Type)
                && IsDbContext(property.Property.ContainingType):
                result.Roots.Add(new Root("dbset-property", Display(property.Property),
                    Display(DbSetEntityType(property.Type))));
                return result;
            case IInvocationOperation invocation:
            {
                var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
                if (method.Name == "Set" && IsCanonicalDbContext(method.ContainingType))
                {
                    result.Roots.Add(new Root("dbcontext-set", Display(method),
                        method.TypeArguments.Length > 0 ? Display(method.TypeArguments[0]) : null));
                    return result;
                }
                if (RawSqlMethods.Contains(method.Name) && IsEfAssembly(method.ContainingAssembly)
                    && method.ContainingType.Name == "RelationalDatabaseFacadeExtensions")
                {
                    result.Roots.Add(new Root("raw-sql", Display(method),
                        method.TypeArguments.Length > 0 ? Display(method.TypeArguments[0]) : null));
                    return result;
                }
                if (IsLinqType(method.ContainingType, "Queryable")
                    || IsLinqType(method.ContainingType, "Enumerable")
                    || IsEfQueryOperator(method))
                {
                    result = TraceRoot(QuerySource(invocation), depth + 1);
                    result.Dependencies.AddRange(ExpressionDependencies(invocation));
                    // Joining or concatenating queries depends on every query operand.
                    foreach (var argument in invocation.Arguments.Where(argument => argument.Parameter?.Ordinal > 0))
                    {
                        if (!IsQueryType(Unwrap(argument.Value)?.Type)) continue;
                        result.Merge(TraceRoot(argument.Value, depth + 1));
                    }
                    return result;
                }
                if (IsQueryType(operation.Type))
                    result.Dependencies.Add(Dependency(method.IsGenericMethod
                        ? "generic-helper" : "helper-return", method, operation.Type));
                return result;
            }
            case ILocalReferenceOperation local when IsQueryType(local.Type):
                // No data-flow proof is attempted; aliases can be reassigned or composed later.
                result.Dependencies.Add(Dependency("local", local.Local, local.Type));
                return result;
            case IParameterReferenceOperation parameter when IsQueryType(parameter.Type):
                result.Dependencies.Add(Dependency("parameter", parameter.Parameter, parameter.Type));
                return result;
            case IFieldReferenceOperation field when IsQueryType(field.Type):
                result.Dependencies.Add(Dependency("field", field.Field, field.Type));
                return result;
            case IPropertyReferenceOperation property when IsQueryType(property.Type):
                result.Dependencies.Add(Dependency("property", property.Property, property.Type));
                return result;
            case IConditionalOperation conditional:
                result.Merge(TraceRoot(conditional.WhenTrue, depth + 1));
                result.Merge(TraceRoot(conditional.WhenFalse, depth + 1));
                // Selecting between providers is not proof of one static query shape.
                if (IsQueryType(conditional.Type))
                    result.Dependencies.Add(Dependency("conditional", null, conditional.Type));
                return result;
            default:
                if (IsQueryType(operation.Type))
                    result.Dependencies.Add(Dependency("unsupported-shape", null, operation.Type));
                return result;
        }
    }

    private static List<QueryDependency> ExpressionDependencies(IInvocationOperation invocation)
    {
        var result = new List<QueryDependency>();
        foreach (var argument in invocation.Arguments)
        {
            if (!IsExpressionType(argument.Parameter?.Type)) continue;
            var value = Unwrap(argument.Value);
            if (value is IAnonymousFunctionOperation) continue;
            var symbol = value switch
            {
                IParameterReferenceOperation reference => (ISymbol)reference.Parameter,
                ILocalReferenceOperation reference => reference.Local,
                IFieldReferenceOperation reference => reference.Field,
                IPropertyReferenceOperation reference => reference.Property,
                IInvocationOperation reference => reference.TargetMethod,
                _ => null
            };
            result.Add(Dependency("expression-argument", symbol, argument.Parameter?.Type));
        }
        return result;
    }

    private static IOperation? QuerySource(IInvocationOperation invocation)
    {
        if (invocation.Instance is not null) return invocation.Instance;
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (method.IsExtensionMethod)
            return invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value;
        return null;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion: operation = conversion.Operand; continue;
                case IParenthesizedOperation parenthesized: operation = parenthesized.Operand; continue;
                case IDelegateCreationOperation creation: operation = creation.Target; continue;
                default: return operation;
            }
        }
    }

    private static bool IsEfQueryOperator(IMethodSymbol method) =>
        IsEfAssembly(method.ContainingAssembly)
        && method.ContainingNamespace.ToDisplayString() == "Microsoft.EntityFrameworkCore"
        && method.Parameters.Any(parameter => IsQueryType(parameter.Type));

    private static bool IsProviderFunction(IMethodSymbol method) =>
        method.ContainingAssembly.Name == "Npgsql.EntityFrameworkCore.PostgreSQL"
        && method.IsExtensionMethod && method.Parameters.Length > 0
        && IsEfType(method.Parameters[0].Type as INamedTypeSymbol,
            "Microsoft.EntityFrameworkCore", "DbFunctions");

    private static bool IsQueryType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (IsDbSet(named) || IsIQueryable(named)) return true;
        return named.AllInterfaces.Any(IsIQueryable);
    }

    private static bool IsIQueryable(INamedTypeSymbol type) =>
        type.ContainingNamespace.ToDisplayString() == "System.Linq"
        && type.OriginalDefinition.MetadataName is "IQueryable" or "IQueryable`1"
        && type.ContainingAssembly.Name is "System.Linq.Expressions" or "System.Core";

    private static bool IsExpressionType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.MetadataName == "Expression`1"
        && named.ContainingNamespace.ToDisplayString() == "System.Linq.Expressions"
        && named.ContainingAssembly.Name is "System.Linq.Expressions" or "System.Core";

    private static bool IsDbSet(ITypeSymbol? type) =>
        Inherits(type as INamedTypeSymbol, "Microsoft.EntityFrameworkCore", "DbSet`1", "Microsoft.EntityFrameworkCore");

    private static ITypeSymbol? DbSetEntityType(ITypeSymbol? type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (IsEfType(current, "Microsoft.EntityFrameworkCore", "DbSet`1"))
                return current.TypeArguments.FirstOrDefault();
        }
        return null;
    }

    private static bool IsDbContext(INamedTypeSymbol? type) =>
        Inherits(type, "Microsoft.EntityFrameworkCore", "DbContext", "Microsoft.EntityFrameworkCore");

    private static bool IsCanonicalDbContext(INamedTypeSymbol type) =>
        IsEfType(type, "Microsoft.EntityFrameworkCore", "DbContext");

    private static bool Inherits(INamedTypeSymbol? type, string ns, string metadataName, string assembly)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.MetadataName == metadataName
                && current.ContainingNamespace.ToDisplayString() == ns
                && current.ContainingAssembly.Name == assembly) return true;
        }
        return false;
    }

    private static bool IsEfType(INamedTypeSymbol? type, string ns, string metadataName) =>
        type is not null && type.OriginalDefinition.MetadataName == metadataName
        && type.ContainingNamespace.ToDisplayString() == ns && IsEfAssembly(type.ContainingAssembly);

    private static bool IsEfAssembly(IAssemblySymbol assembly) =>
        assembly.Name is "Microsoft.EntityFrameworkCore" or "Microsoft.EntityFrameworkCore.Relational";

    private static bool IsLinqType(INamedTypeSymbol type, string name) =>
        type.Name == name && type.ContainingNamespace.ToDisplayString() == "System.Linq"
        && type.ContainingAssembly.Name is "System.Linq" or "System.Linq.Queryable" or "System.Core";

    private static void ReportCompilation(CompilationAnalysisContext context, object identity)
    {
        ReportExcludedText(context, identity);
        var evidence = new
        {
            compilation = identity,
            analyzer = typeof(EfQueryInventoryAnalyzer).Assembly.GetName().FullName,
            syntaxTrees = context.Compilation.SyntaxTrees.OrderBy(tree => tree.FilePath, StringComparer.Ordinal)
                .Select(tree => new
                {
                    file = tree.FilePath,
                    checksum = Checksum(tree, context.CancellationToken),
                    generated = IsGenerated(tree, context.CancellationToken),
                    defines = (tree.Options as CSharpParseOptions)?.PreprocessorSymbolNames
                        .OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? []
                }).ToArray(),
            references = context.Compilation.References.Select(reference => new
            {
                identity = context.Compilation.GetAssemblyOrModuleSymbol(reference)?.ToDisplayString(),
                kind = reference.Properties.Kind.ToString(),
                aliases = reference.Properties.Aliases.ToArray()
            }).OrderBy(reference => reference.identity, StringComparer.Ordinal).ToArray()
        };
        context.ReportDiagnostic(Diagnostic.Create(CompilationRule, Location.None,
            Properties("compilation", "compilation", evidence), context.Compilation.AssemblyName ?? "unknown"));
    }

    private static void ReportExcludedText(CompilationAnalysisContext context, object identity)
    {
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            foreach (var trivia in tree.GetRoot(context.CancellationToken).DescendantTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia)) continue;
                // These are lexical observations only, including possible comment/string matches.
                // No disabled source, SQL text, or claimed compiler symbol is serialized.
                var candidates = Regex.Matches(trivia.ToFullString(),
                        @"\b([A-Za-z_][A-Za-z_0-9]*)\s*(?:<[^>\r\n]*>)?\s*\(")
                    .Select(match => match.Groups[1].Value).Where(LexicalCandidates.Contains)
                    .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                if (candidates.Length == 0) continue;
                var location = tree.GetLocation(trivia.Span);
                var lines = location.GetLineSpan();
                var evidence = new
                {
                    compilation = identity,
                    file = tree.FilePath,
                    span = new
                    {
                        start = trivia.Span.Start,
                        length = trivia.Span.Length,
                        line = lines.StartLinePosition.Line + 1,
                        column = lines.StartLinePosition.Character + 1,
                        endLine = lines.EndLinePosition.Line + 1,
                        endColumn = lines.EndLinePosition.Character + 1
                    },
                    sourceChecksum = Checksum(tree, context.CancellationToken),
                    generated = IsGenerated(tree, context.CancellationToken),
                    candidateNames = candidates,
                    reason = "inactive-preprocessor-text"
                };
                context.ReportDiagnostic(Diagnostic.Create(ExcludedRule, location,
                    Properties("lexical-excluded", "preprocessor-excluded", evidence), candidates.Length));
            }
        }
    }

    private static object CompilationIdentity(Compilation compilation, AnalyzerOptions options)
    {
        var global = options.AnalyzerConfigOptionsProvider.GlobalOptions;
        global.TryGetValue("build_property.MSBuildProjectFullPath", out var projectPath);
        global.TryGetValue("build_property.RootNamespace", out var rootNamespace);
        return new
        {
            assembly = compilation.Assembly.Identity.ToString(),
            projectPath,
            rootNamespace,
            defines = compilation.SyntaxTrees.SelectMany(tree =>
                (tree.Options as CSharpParseOptions)?.PreprocessorSymbolNames ?? [])
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
        };
    }

    private static ImmutableDictionary<string, string?> Properties(string kind, string classification, object evidence) =>
        ImmutableDictionary<string, string?>.Empty
            .Add("schemaVersion", "1").Add("recordKind", kind)
            .Add("classification", classification).Add("evidence", JsonSerializer.Serialize(evidence));

    private static object Checksum(SyntaxTree tree, CancellationToken cancellationToken)
    {
        var source = tree.GetText(cancellationToken);
        return new
        {
            algorithm = source.ChecksumAlgorithm.ToString(),
            value = Convert.ToHexString(source.GetChecksum().AsSpan())
        };
    }

    private static bool IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
    {
        var path = tree.FilePath;
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)) return true;
        var header = tree.GetRoot(cancellationToken).GetLeadingTrivia().ToFullString();
        return header.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
            || header.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Display(ISymbol? symbol) => symbol?.ToDisplayString(SymbolFormat);
    private static ImmutableHashSet<string> Names(params string[] names) =>
        names.ToImmutableHashSet(StringComparer.Ordinal);
    private static QueryDependency Dependency(string kind, ISymbol? symbol, ITypeSymbol? type) =>
        new(kind, Display(symbol), Display(type));

    private sealed record Root(string kind, string? symbol, string? entityType);
    private sealed record QueryDependency(string kind, string? symbol, string? type);

    private sealed class RootEvidence
    {
        public List<Root> Roots { get; } = [];
        public List<QueryDependency> Dependencies { get; } = [];
        public string Status => Dependencies.Count > 0 ? "unknown" : Roots.Count > 0 ? "proven" : "none";

        public void Merge(RootEvidence other)
        {
            Roots.AddRange(other.Roots);
            Dependencies.AddRange(other.Dependencies);
        }
    }
}
