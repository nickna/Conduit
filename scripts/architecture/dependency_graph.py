"""Evaluate production project edges and inventory resolved packages for one profile.

Run after a matching restore, or pass --restore. Publish inputs and output files
are independently enforced by dependency-boundaries.targets during dotnet publish.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys


EXECUTION = {"ConduitLLM.Core", "ConduitLLM.Providers", "ConduitLLM.Functions", "ConduitLLM.Tokenization"}
NEUTRAL = {"ConduitLLM.Contracts", "ConduitLLM.Persistence.Abstractions"}
HOSTS = {"ConduitLLM.Admin", "ConduitLLM.Gateway"}
PERSISTENCE = re.compile(r"^(Microsoft\.EntityFrameworkCore|Npgsql|Microsoft\.Build)(\.|$)", re.I)
IMPLEMENTATIONS = re.compile(
    r"^(Microsoft\.EntityFrameworkCore|Npgsql|Microsoft\.Build|ModelContextProtocol|"
    r"Wolverine|WolverineFx|RabbitMQ\.Client|Microsoft\.AspNetCore\.(SignalR|OpenApi)|"
    r"MessagePack|AWSSDK|Amazon|Microsoft\.OpenApi|Microsoft\.ML\.Tokenizers|Scalar)(\.|$)", re.I)
TOOLING = re.compile(
    r"^(Microsoft\.EntityFrameworkCore\.Design|Microsoft\.Build|Microsoft\.CodeAnalysis|"
    r"JasperFx\.RuntimeCompiler|Wolverine(Fx)?\.RuntimeCompilation)(\.|$)", re.I)
ADMIN_UNUSED = re.compile(r"^(Microsoft\.AspNetCore\.SignalR|Microsoft\.ML\.Tokenizers)(\.|$)", re.I)
OPTIONAL_ADAPTERS = re.compile(
    r"^(Wolverine|WolverineFx|RabbitMQ\.Client|Microsoft\.AspNetCore\.(SignalR|OpenApi)|"
    r"MessagePack|AWSSDK|Amazon|Microsoft\.OpenApi|Microsoft\.ML\.Tokenizers|Scalar)(\.|$)", re.I)
CLIENT_PROTOCOL = "Microsoft.AspNetCore.SignalR.Protocols.MessagePack"
BACKPLANE = "Microsoft.AspNetCore.SignalR.StackExchangeRedis"


def validate_graph(graph, allowed):
    errors = []
    for source, targets in graph.items():
        if source not in allowed:
            errors.append(f"Unclassified production project: {source}")
        for target in targets:
            if target not in graph:
                errors.append(f"Production reference leaves the inventoried graph: {source} -> {target}")
            if target not in allowed.get(source, []):
                errors.append(f"Prohibited project edge: {source} -> {target}")
            if target in HOSTS:
                errors.append(f"Production project references a service executable: {source} -> {target}")
    visited = set()

    def visit(node, path):
        if node in path:
            errors.append("Project cycle: " + " -> ".join([*path, node]))
            return
        if node in visited:
            return
        for target in graph.get(node, []):
            visit(target, [*path, node])
        visited.add(node)

    for source in graph:
        visit(source, [])
    for source in EXECUTION | NEUTRAL | {"ConduitLLM.Configuration"}:
        pending = [(source, [source])]
        seen = {source}
        while pending:
            node, path = pending.pop()
            for target in graph.get(node, []):
                route = [*path, target]
                forbidden = source in EXECUTION and target in {
                    "ConduitLLM.Configuration", "ConduitLLM.Persistence.Composition", "ConduitLLM.Persistence.Npgsql"}
                forbidden |= source in NEUTRAL and target not in NEUTRAL
                forbidden |= source == "ConduitLLM.Configuration" and target == "ConduitLLM.Functions"
                if forbidden:
                    errors.append("Prohibited transitive project path: " + " -> ".join(route))
                if target not in seen:
                    seen.add(target)
                    pending.append((target, route))
    return errors


def validate_packages(project, packages, profile):
    errors = []
    for package in packages:
        forbidden = bool(project in EXECUTION and PERSISTENCE.match(package))
        forbidden |= bool(project in {"ConduitLLM.Core", "ConduitLLM.Providers"} and OPTIONAL_ADAPTERS.match(package))
        forbidden |= bool(project in NEUTRAL and IMPLEMENTATIONS.match(package))
        forbidden |= bool(project in HOSTS and TOOLING.match(package))
        forbidden |= bool(project == "ConduitLLM.Configuration" and package.startswith("ModelContextProtocol"))
        forbidden |= bool(project == "ConduitLLM.Admin" and ADMIN_UNUSED.match(package))
        forbidden |= project in HOSTS | {"ConduitLLM.SignalR"} and profile != "jit" and package == CLIENT_PROTOCOL
        if forbidden:
            errors.append(f"{project} resolves prohibited package {package} ({profile})")
    if project in {"ConduitLLM.SignalR", "ConduitLLM.Gateway"}:
        required = {BACKPLANE, "MessagePack"}
        if profile == "jit":
            required.add(CLIENT_PROTOCOL)
        for package in required - set(packages):
            errors.append(f"{project} lost required protocol/backplane package {package} ({profile})")
    return errors


def dotnet(arguments, root):
    result = subprocess.run(["dotnet", *arguments], cwd=root, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout


def validate_restore_inputs(project, evaluated_packages, evaluated_projects, assets, rid=None):
    errors = []
    for framework in assets["project"]["frameworks"].values():
        # The SDK injects ILLink/ILCompiler auto-references during restore targets,
        # after plain project evaluation. They are still inventoried/guarded in
        # the resolved closure, but are not user-selected PackageReferences.
        restored = {name.casefold() for name, value in framework.get("dependencies", {}).items()
                    if not value.get("autoReferenced", False)}
        if restored != {name.casefold() for name in evaluated_packages}:
            errors.append(f"{project} assets do not match evaluated package references; restore the requested profile")
    for framework in assets["project"]["restore"]["frameworks"].values():
        restored = {Path(path).resolve() for path in framework.get("projectReferences", {})}
        if restored != {Path(path).resolve() for path in evaluated_projects}:
            errors.append(f"{project} assets do not match evaluated project references; restore the requested profile")
    if rid and not any(target.endswith("/" + rid) for target in assets["targets"]):
        errors.append(f"{project} assets lack requested RID {rid}; restore the requested profile")
    return errors


def inventory(root, profile, rid, restore):
    properties = ["-p:Configuration=Release", "-p:ConduitEfTooling=false", "-p:ConduitWolverineCodegen=false"]
    if profile == "audit":
        properties.append("-p:ConduitAotAudit=true")
    if profile == "native":
        properties += ["-p:PublishAot=true", "-p:ConduitAotAudit=true", f"-p:RuntimeIdentifier={rid}"]
    if restore:
        for host in sorted(HOSTS):
            dotnet(["restore", f"Services/{host}/{host}.csproj", *properties], root)
    projects = sorted([*root.glob("Shared/*/*.csproj"), *root.glob("Services/*/*.csproj")])
    graph, records = {}, []
    for project in projects:
        evaluation = json.loads(dotnet([
            "msbuild", str(project), "-nologo", "-getItem:ProjectReference,PackageReference",
            "-getProperty:MSBuildProjectName,ProjectAssetsFile", *properties], root))
        name = evaluation["Properties"]["MSBuildProjectName"]
        if name in graph:
            raise RuntimeError(f"Duplicate production project name: {name}")
        graph[name] = [Path(item["FullPath"]).stem for item in evaluation["Items"]["ProjectReference"]]
        assets_path = Path(evaluation["Properties"]["ProjectAssetsFile"])
        if not assets_path.is_file():
            raise RuntimeError(f"Missing resolved assets for {name}; restore matching {profile} profile first or pass --restore")
        assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
        direct_packages = sorted(item["Identity"] for item in evaluation["Items"]["PackageReference"])
        restore_errors = validate_restore_inputs(name, direct_packages,
                                                [item["FullPath"] for item in evaluation["Items"]["ProjectReference"]],
                                                assets, rid if profile == "native" else None)
        # All target graphs are checked: a RID-specific runtime dependency must
        # not evade the guard by being absent from the framework-only target.
        packages = sorted(key.rsplit("/", 1)[0] for key, value in assets["libraries"].items() if value["type"] == "package")
        resolved_projects = sorted(key.rsplit("/", 1)[0] for key, value in assets["libraries"].items() if value["type"] == "project")
        records.append({"project": name, "path": project.relative_to(root).as_posix(),
                        "references": graph[name], "directPackages": direct_packages,
                        "resolvedPackages": packages, "resolvedProjects": resolved_projects,
                        "assetsSha256": hashlib.sha256(assets_path.read_bytes()).hexdigest(),
                        "restoreErrors": restore_errors,
                        "resolvedPackageVersions": {key.rsplit("/", 1)[0]: key.rsplit("/", 1)[1]
                                                    for key, value in sorted(assets["libraries"].items()) if value["type"] == "package"},
                        "targets": sorted(assets["targets"])})
    return graph, records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", choices=["jit", "audit", "native"], default="jit")
    parser.add_argument("--rid", default="linux-x64")
    parser.add_argument("--restore", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    graph, records = inventory(root, args.profile, args.rid, args.restore)
    allowed = json.loads((Path(__file__).parent / "project-edges.json").read_text())
    errors = validate_graph(graph, allowed)
    for record in records:
        errors += record["restoreErrors"]
        errors += validate_packages(record["project"], record["resolvedPackages"], args.profile)
        # Check NuGet's project closure independently of evaluated source edges.
        closure = {record["project"]: record["resolvedProjects"]}
        for error in validate_graph(closure, allowed):
            if error.startswith("Prohibited transitive project path:"):
                errors.append("Resolved " + error)
    report = {"schemaVersion": 1, "profile": args.profile, "rid": args.rid if args.profile == "native" else None,
              "properties": {"Configuration": "Release", "PublishAot": args.profile == "native",
                             "ConduitAotAudit": args.profile != "jit", "ConduitEfTooling": False,
                             "ConduitWolverineCodegen": False},
              "sourceRevision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
              "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True).strip()),
              "sdk": dotnet(["--version"], root).strip(), "projects": records, "errors": errors}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if errors:
        raise RuntimeError("\n".join(errors))
    print(f"PASS: {len(records)} {args.profile} evaluated project and resolved package graphs")


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
