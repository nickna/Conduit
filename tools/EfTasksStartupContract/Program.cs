using System.Collections;
using System.Xml.Linq;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.EntityFrameworkCore.Tasks.Internal;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: EfTasksStartupContract <EF Tasks 10.0.12 package directory> <fresh evidence directory>");
    return 2;
}

var package = Path.GetFullPath(args[0]);
var evidence = Path.GetFullPath(args[1]);
if (Directory.Exists(evidence) || File.Exists(evidence))
{
    Console.Error.WriteLine("Use a fresh evidence directory.");
    return 2;
}

Directory.CreateDirectory(evidence);
var checks = 0;
var targets = XDocument.Load(Path.Combine(package, "buildTransitive", "Microsoft.EntityFrameworkCore.Tasks.targets"));
var task = targets.Descendants().Single(element => element.Name.LocalName == "OptimizeDbContext"
    && element.Attribute("StartupAssembly") != null);
Require(task.Attribute("StartupAssembly")!.Value == "$(EFStartupAssembly)", "startup property mapping");
Require(task.Attribute("DbContextType")!.Value == "$(DbContextType)", "context property mapping");
var imports = Directory.EnumerateFiles(Path.Combine(package, "buildTransitive"), "*", SearchOption.AllDirectories)
    .Where(path => path.EndsWith(".props", StringComparison.Ordinal) || path.EndsWith(".targets", StringComparison.Ordinal));
Require(!imports.Any(path => File.ReadAllText(path).Contains("EFStartupProject", StringComparison.Ordinal)),
    "no EFStartupProject props/targets mapping");
Require(File.ReadAllText(Path.Combine(package, "docs", "PACKAGE.md")).Contains("EFStartupProject", StringComparison.Ordinal),
    "stale package documentation remains observable");

var startup = Path.Combine(evidence, "Startup.exe");
var deps = Path.ChangeExtension(startup, ".deps.json");
var runtimeConfig = Path.ChangeExtension(startup, ".runtimeconfig.json");
// Presence-only fixtures: no task Execute(), dotnet child, or startup assembly loading.
File.WriteAllText(deps, "{}");
File.WriteAllText(runtimeConfig, "{}");
foreach (var suffix in new[] { ".dll", ".csproj", ".EXE", ".exe" })
{
    var engine = new CaptureBuildEngine();
    var probe = CreateProbe(engine);
    probe.StartupAssembly = new TaskItem(Path.ChangeExtension(startup, suffix));
    var accepted = probe.ValidateOnly();
    Require(accepted == (suffix == ".exe"), "startup suffix " + suffix);
    if (!accepted)
    {
        Require(engine.Errors.Count == 1 && engine.Errors[0].Contains("EFStartupProject", StringComparison.Ordinal),
            "validation diagnostic names the stale property for " + suffix);
    }
}

var commandProbe = CreateProbe(new CaptureBuildEngine());
commandProbe.StartupAssembly = new TaskItem(startup);
var command = commandProbe.CommandOnly();
Require(command.Contains("--startup-assembly", StringComparison.Ordinal)
    && command.Contains(Path.ChangeExtension(startup, ".dll"), StringComparison.Ordinal)
    && !command.Contains(startup, StringComparison.Ordinal), "exe selector becomes managed dll argument");
Require(command.Contains("--depsfile", StringComparison.Ordinal) && command.Contains(deps, StringComparison.Ordinal),
    "startup sibling dependency file");
Require(command.Contains("--runtimeconfig", StringComparison.Ordinal) && command.Contains(runtimeConfig, StringComparison.Ordinal),
    "startup sibling runtime configuration");
File.WriteAllText(Path.Combine(evidence, "command.txt"), command);

var implicitProbe = CreateProbe(new CaptureBuildEngine());
Require(implicitProbe.ValidateOnly(), "omitted startup selector is not rejected by suffix validation");
File.WriteAllText(Path.Combine(evidence, "result.txt"), $"PASS: {checks} parameter-contract checks; taskExecute=false; database=false; nativePublish=false.");
Console.WriteLine($"PASS: {checks} parameter-contract checks; taskExecute=false; database=false; nativePublish=false.");
return 0;

ProbeTask CreateProbe(CaptureBuildEngine engine) => new()
{
    BuildEngine = engine,
    Assembly = new TaskItem(Path.Combine(evidence, "ContextLibrary.dll")),
    TargetFrameworkMoniker = ".NETCoreApp,Version=v10.0"
};

void Require(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException("Parameter contract changed: " + name);
    }

    checks++;
}

sealed class ProbeTask : OperationTaskBase
{
    public bool ValidateOnly() => ValidateParameters();
    public string CommandOnly() => GenerateCommandLineCommands();
}

sealed class CaptureBuildEngine : IBuildEngine
{
    public List<string> Errors { get; } = [];
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "parameter-contract";
    public void LogErrorEvent(BuildErrorEventArgs value) => Errors.Add(value.Message ?? "");
    public void LogWarningEvent(BuildWarningEventArgs value) { }
    public void LogMessageEvent(BuildMessageEventArgs value) { }
    public void LogCustomEvent(CustomBuildEventArgs value) { }
    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
        => throw new NotSupportedException("The parameter contract must not build another project.");
}
