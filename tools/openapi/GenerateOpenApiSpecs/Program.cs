using System.Diagnostics;
using System.Reflection;

namespace GenerateOpenApiSpecs;

internal static class Program
{
    private const string Usage =
        "Usage: GenerateOpenApiSpecs [admin|gateway|all] [--output-directory <path>]";

    private sealed record Target(
        string Name,
        string ProjectDirectory,
        string AssemblyName,
        string OutputFileName);

    private static readonly IReadOnlyDictionary<string, Target> Targets =
        new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase)
        {
            ["admin"] = new(
                "admin",
                Path.Combine("Services", "ConduitLLM.Admin"),
                "ConduitLLM.Admin.dll",
                "openapi-admin.json"),
            ["gateway"] = new(
                "gateway",
                Path.Combine("Services", "ConduitLLM.Gateway"),
                "ConduitLLM.Gateway.dll",
                "openapi-gateway.json")
        };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var repositoryRoot = FindRepositoryRoot();
            var outputDirectory = options.OutputDirectory is null
                ? null
                : Path.GetFullPath(options.OutputDirectory);
            var configuration =
                typeof(Program).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
                ?? "Debug";

            foreach (var target in SelectTargets(options.Selection))
            {
                var destinationDirectory = outputDirectory ??
                    Path.Combine(repositoryRoot, target.ProjectDirectory);
                await ExportAsync(repositoryRoot, configuration, target, destinationDirectory);
            }

            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static (string Selection, string? OutputDirectory) ParseArguments(string[] args)
    {
        var selection = "all";
        string? outputDirectory = null;
        var selectionSpecified = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--output-directory", StringComparison.Ordinal))
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    throw new ArgumentException("--output-directory requires a path.");
                }

                outputDirectory = args[index];
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown option '{argument}'.");
            }

            if (selectionSpecified)
            {
                throw new ArgumentException($"Unexpected argument '{argument}'.");
            }

            selection = argument;
            selectionSpecified = true;
        }

        if (!string.Equals(selection, "all", StringComparison.OrdinalIgnoreCase) &&
            !Targets.ContainsKey(selection))
        {
            throw new ArgumentException($"Unknown target '{selection}'.");
        }

        return (selection, outputDirectory);
    }

    private static IEnumerable<Target> SelectTargets(string selection)
    {
        if (string.Equals(selection, "all", StringComparison.OrdinalIgnoreCase))
        {
            yield return Targets["admin"];
            yield return Targets["gateway"];
            yield break;
        }

        yield return Targets[selection];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Conduit.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new InvalidOperationException(
                "Could not locate the repository root containing Conduit.slnx.");
    }

    private static async Task ExportAsync(
        string repositoryRoot,
        string configuration,
        Target target,
        string destinationDirectory)
    {
        var assemblyPath = await GetTargetPathAsync(repositoryRoot, configuration, target);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                $"The {target.Name} host assembly was not built: {assemblyPath}",
                assemblyPath);
        }

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, target.OutputFileName);
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"conduit-openapi-{target.Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryOutput = Path.Combine(temporaryDirectory, target.OutputFileName);

        try
        {
            Console.WriteLine($"Generating {target.Name} OpenAPI contract...");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                WorkingDirectory = repositoryRoot
            };
            startInfo.ArgumentList.Add(assemblyPath);
            startInfo.Environment["CONDUIT_OPENAPI_GENERATION"] = "true";
            startInfo.Environment["CONDUIT_OPENAPI_OUTPUT"] = temporaryOutput;
            startInfo.Environment["Logging__EventLog__LogLevel__Default"] = "None";
            if (!startInfo.Environment.TryGetValue("DATABASE_URL", out var databaseUrl) ||
                string.IsNullOrWhiteSpace(databaseUrl))
            {
                startInfo.Environment["DATABASE_URL"] =
                    "postgresql://conduit:conduitpass@localhost:5432/conduitdb";
            }

            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException($"Failed to start the {target.Name} host.");
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{target.Name} OpenAPI export exited with code {process.ExitCode}.");
            }

            if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
            {
                throw new InvalidOperationException(
                    $"{target.Name} OpenAPI export completed without producing {target.OutputFileName}.");
            }

            File.Copy(temporaryOutput, destinationPath, overwrite: true);
            Console.WriteLine($"Wrote {destinationPath}");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<string> GetTargetPathAsync(string repositoryRoot, string configuration, Target target)
    {
        var projectPath = Path.Combine(repositoryRoot, target.ProjectDirectory,
            Path.ChangeExtension(target.AssemblyName, ".csproj"));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "msbuild", projectPath, "-nologo", "-verbosity:quiet",
                     "-getProperty:TargetPath", $"-property:Configuration={configuration}" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Failed to resolve the {target.Name} host TargetPath.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var targetPath = (await output).Trim();
        var errorText = await error;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath))
        {
            throw new InvalidOperationException(
                $"Could not resolve the {target.Name} host TargetPath (exit {process.ExitCode}): {errorText}{targetPath}");
        }

        return targetPath;
    }
}
