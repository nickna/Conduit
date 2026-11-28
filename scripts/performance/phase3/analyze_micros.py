"""Strict same-.NET11 micro evidence replay. All performance comparisons are descriptive."""
import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import platform
import statistics
import sys
import tarfile


class EvidenceError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise EvidenceError(message)


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def integer(value, minimum=0):
    require(type(value) is int and value >= minimum, "Invalid integer counter")
    return value


def number(value, positive=False):
    require(type(value) in (int, float) and math.isfinite(value) and (value > 0 if positive else value >= 0), "Invalid/nonfinite/negative metric")
    return value


def digest(value):
    require(type(value) is str and len(value) == 64 and all(c in "0123456789abcdef" for c in value), "Invalid SHA256 digest")
    return value


def revision(value):
    require(type(value) is str and len(value) == 40 and all(c in "0123456789abcdef" for c in value), "Invalid source revision/tree")
    return value


# Policy settings only. Region addresses, active/budget/heap diagnostics remain raw.
POLICY_KEYS = {"servergc", "concurrentgc", "retainvm", "noaffinitize", "cpugroup",
               "heapaffinitizemask", "heapaffinitizeranges", "heaphardlimit", "heaphardlimitpercent",
               "heaphardlimitsoh", "heaphardlimitloh", "heaphardlimitpoh", "heaphardlimitsohpercent",
               "heaphardlimitlohpercent", "heaphardlimitpohpercent", "conservemem", "name", "path",
               "dynamicadaptationmode", "lohthreshold", "largepages"}


def gc_policy(server, configuration):
    require(type(server) is bool and isinstance(configuration, dict) and configuration, "Missing actual GC metadata")
    result = {"actualServerGc": server}
    for key, value in configuration.items():
        canonical = key.lower().removeprefix("system.").replace(".", "").replace("_", "")
        if canonical.startswith("gc") and canonical not in ("servergc", "concurrentgc"):
            canonical = canonical[2:]
        canonical = {"server": "servergc", "concurrent": "concurrentgc"}.get(canonical, canonical)
        if canonical in POLICY_KEYS:
            require(type(value) in (str, int, bool), "Invalid GC policy value")
            normalized = str(value).lower() if type(value) is bool else str(value)
            if normalized.lower() in ("true", "false"):
                normalized = normalized.lower()
            require(canonical not in result or result[canonical] == normalized, "Conflicting GC policy aliases")
            result[canonical] = normalized
    if "servergc" in result:
        require(result["servergc"] == str(server).lower(), "Actual GC mode/configuration disagree")
    return result


def bound_path(root, relative):
    result = (root / relative).resolve()
    require(result.is_relative_to(root.resolve()), "Evidence path escaped its directory")
    return result


def file_inventory(directory):
    return {p.relative_to(directory).as_posix(): {"bytes": p.stat().st_size, "sha256": sha(p)}
            for p in directory.rglob("*") if p.is_file()}


def inventory(records):
    result = {}
    for row in records:
        path = row["path"].replace("\\", "/")
        require(path not in result and not path.startswith("/") and ".." not in Path(path).parts, "Invalid/duplicate inventory path")
        result[path] = {"bytes": integer(row["bytes"]), "sha256": row["sha256"]}
    return result


def tar_inventory(path):
    result = {}
    with tarfile.open(path) as archive:
        for member in archive:
            if member.isdir():
                continue
            require(member.isfile() and member.name not in result, "Unsupported linked/duplicate source archive member")
            require(not member.name.startswith("/") and ".." not in Path(member.name).parts, "Invalid archive path")
            with archive.extractfile(member) as stream:
                result[member.name] = {"bytes": member.size, "sha256": hashlib.file_digest(stream, "sha256").hexdigest()}
    return result


def report_rows(tool, report, entry, architecture, bound):
    require(report["schemaVersion"] == 1 and report["architecture"] == architecture, "Report schema/architecture differs")
    runtime = report.get("runtime", report.get("framework", "").removeprefix(".NET "))
    require(runtime == "11.0.0", "Micro runtime must be11.0.0")
    server = report["isServerGc"] if tool == "collector" else report["serverGc"]
    policy = gc_policy(server, report["gcConfiguration"])
    if "processId" in report:
        require(report["processId"] == entry["processId"], "Report/owned process identity differs")
    output = []

    def assembly(name, actual):
        require(name + ".dll" in bound and actual == bound[name + ".dll"]["sha256"], "Report assembly/source binding differs")

    def row(case, sample, units, wall, cpu, thread, process=None, semantic=None):
        integer(sample, 1); integer(units, 1)
        output.append({"case": case, "sample": sample, "units": units,
                       "metrics": {"elapsedMillisecondsPerUnit": number(wall, True) / units,
                                   "processCpuMillisecondsPerUnit": number(cpu) / units,
                                   "threadAllocatedBytesPerUnit": None if thread is None else integer(thread) / units,
                                   "processAllocatedBytesPerUnit": None if process is None else integer(process) / units},
                       "semantic": semantic})

    if tool == "collector":
        require(report["passed"] is True and report["declaredSourceRevision"] == entry["sourceSha"], "Collector source/success differs")
        require(report["innerSamplesPerCase"] == 5 and report["warmupStreamsPerCase"] == 1, "Collector inner/warmup count differs")
        assembly("ConduitLLM.Core", report["productionAssemblySha256"])
        assembly("ConduitLLM.Benchmarks", report["harnessAssemblySha256"])
        require(report["currentCallerReader"] in ("HasRecordedFirstToken", "GetMetrics.TimeToFirstTokenMs"), "Unknown first-token adapter")
        expected = {(n, m) for n in (10, 100, 1000, 10000) for m in
                    ("snapshot-each-content-chunk", "current-caller-first-token-check", "record-plus-final-snapshot-control")}
        expected.add((100000, "record-plus-final-snapshot-control"))
        require(len(report["rows"]) == 13 and {(r["contentChunks"], r["mode"]) for r in report["rows"]} == expected, "Collector case inventory differs")
        frequency = integer(report["stopwatchFrequency"], 1)
        for case in report["rows"]:
            require(len(case["samples"]) == 5, "Collector sample count differs")
            for index, sample in enumerate(case["samples"], 1):
                start = integer(sample["startedTicks"], 1); end = integer(sample["endedTicks"], 1)
                require(end > start and sample["contentChunksRecorded"] == case["contentChunks"], "Collector clock/chunk count differs")
                # Stopwatch.GetElapsedTime returns a TimeSpan, quantized to100ns.
                require(abs(number(sample["microseconds"], True) - (end - start) * 1e6 / frequency) <= .110001, "Collector reported duration differs from ticks")
                integer(sample["managedThreadId"], 1); number(sample["averageInterTokenLatencyMilliseconds"])
                row(f"{case['mode']}:{case['contentChunks']}", index, case["contentChunks"], sample["microseconds"] / 1000,
                    sample["processCpuMilliseconds"], sample["threadLocalAllocatedBytes"])
    elif tool == "writer":
        for name, key in (("ConduitLLM.Gateway", "gateway"), ("ConduitLLM.Core", "core")):
            assembly(name, report[key]["sha256"])
            if "_publishedDirectory" in entry:
                require(report[key]["path"].replace("\\", "/") == entry["_publishedDirectory"].replace("\\", "/") + "/" + name + ".dll", "Writer loaded assembly path differs")
        require(len(report["rows"]) == 80, "Writer row inventory differs")
        seen = set()
        for item in report["rows"]:
            require(type(item["enhanced"]) is bool and item["characters"] in (32, 1024) and item["frames"] in (10, 100, 1000, 10000), "Writer case shape differs")
            key = (item["enhanced"], item["characters"], item["frames"], item["run"])
            require(key not in seen and integer(item["run"], 1) in range(1, 6), "Writer sample inventory differs"); seen.add(key)
            value = item["observation"]; frames = item["frames"]; enhanced = item["enhanced"]
            require(value["validated"] is True and digest(value["wireSha256"]) == value["expectedWireSha256"], "Writer wire validation failed")
            require(value["EventsWritten"] == frames + (2 if enhanced else 1) and value["Flushes"] == frames + (3 if enhanced else 2), "Writer event/flush/DONE count differs")
            integer(value["Bytes"], 1); integer(value["Writes"], 1)
            require(type(value["sameThread"]) is bool and (value["currentThreadAllocatedBytes"] is not None) == value["sameThread"], "Writer thread-counter claim differs")
            row(f"enhanced={enhanced}:chars={item['characters']}:frames={frames}", item["run"], frames,
                value["elapsedMilliseconds"], value["processCpuMilliseconds"], value["currentThreadAllocatedBytes"], value["processAllocatedBytes"],
                {"wireSha256": value["wireSha256"], "bytes": value["Bytes"], "events": value["EventsWritten"], "flushes": value["Flushes"]})
    elif tool == "provider":
        require(report["innerSamples"] == 5 and report["freshProcessesInThisReport"] == 1 and report["parserControlsDeployed"] is False, "Provider method scope differs")
        names = [a["name"] for a in report["assemblies"]]
        require(len(names) == 3 and set(names) == {"ConduitLLM.Benchmarks", "ConduitLLM.Core", "ConduitLLM.Providers"}, "Provider assembly inventory differs")
        for item in report["assemblies"]:
            assembly(item["name"], item["sha256"])
        cases = {("groq", p, v) for p in ("content", "hosted-usage") for v in ("groq-public-options", "groq-client-call-path")}
        cases |= {("parser-controls", "content", v) for v in ("dom-raw-text-model", "dom-model-control", "direct-typed-control")}
        require(len(report["rows"]) == 140, "Provider row inventory differs")
        seen = set()
        for item in report["rows"]:
            key = (item["suite"], item["payload"], item["variant"])
            sample_key = (*key, item["frames"], item["sample"])
            require(key in cases and item["frames"] in (10, 100, 1000, 10000) and integer(item["sample"], 1) in range(1, 6) and sample_key not in seen, "Provider case/sample inventory differs"); seen.add(sample_key)
            value = item["observation"]
            require(value["validated"] is True and value["checksum"] == value["expectedChecksum"], "Provider checksum validation failed")
            integer(value["checksum"], 1)
            integer(value["expectedChecksum"], 1)
            start = integer(value["rawProcessAllocatedStartBytes"]); end = integer(value["rawProcessAllocatedEndBytes"])
            require(value["processAllocationMonotonic"] is True and end - start == integer(value["rawProcessAllocatedDeltaBytes"]), "Provider allocation counter reset/delta mismatch")
            require(type(value["sameThread"]) is bool and (value["currentThreadAllocatedBytes"] is not None) == value["sameThread"], "Provider thread-counter claim differs")
            require(len(value["collections"]) == 3, "Provider GC inventory differs")
            for count in value["collections"]: integer(count)
            number(value["gcPauseMilliseconds"])
            digest(item["fixtureSha256"]); digest(value["wireSha256"])
            row(f"{':'.join(key)}:frames={item['frames']}", item["sample"], item["frames"], value["elapsedMilliseconds"],
                value["processCpuMilliseconds"], value["currentThreadAllocatedBytes"], value["rawProcessAllocatedDeltaBytes"],
                {"fixtureSha256": item["fixtureSha256"], "wireSha256": value["wireSha256"], "checksum": value["checksum"], "hostedTools": value["hostedTools"]})
    else:
        raise EvidenceError("Unknown micro tool")
    return output, policy


def stats(values):
    return {"median": statistics.median(values), "minimum": min(values), "maximum": max(values)}


def analyze(directory):
    root = Path(directory).resolve(); config = read(root / "configuration.json")
    require(config["schemaVersion"] == 1 and config["runs"] == 5 and config["innerSamples"] == 5 and config["sourceSdk"] == "11.0.100" and config["runtime"] == "11.0.0", "Configuration toolchain/repetition differs")
    require(config["baselineRevision"] == "b1d49922b8dfdc6719c6d46d123f2eb5a265dcca" and config["candidateRevision"] != config["baselineRevision"], "Source controls differ")
    revision(config["candidateRevision"]); revision(config["driverRevision"])
    context = read(root / "context.json")
    require(context["architecture"] == config["architecture"] and not context["gcEnvironment"] and context["kernel"].startswith("Linux "), "Native/default-GC context differs")
    require(config["architecture"] in ("X64", "Arm64"), "Unsupported architecture")
    require(file_inventory(root / "driver-source") == inventory(read(root / "driver-source-bindings.json")) and sha(root / "driver-source/analyze_micros.py") == sha(__file__), "Captured analyzer/driver source differs")
    execution = read(root / "execution.json")
    require(execution["failure"] is None and not execution["cleanupFailures"] and all(p["closed"] for p in execution["processesClosed"]), "Capture failed or owned processes remain")
    final = read(root / "final-input-integrity.json")
    require(len(final) == 2 and all(r[k]["passed"] and not any(r[k][v] for v in ("added", "missing", "modified")) for r in final for k in ("source", "published")), "Final input integrity failed")
    require(sha(root / "work/measurement-overlay.tar") == config["overlaySha256"], "Overlay archive hash differs")
    overlay_tar = tar_inventory(root / "work/measurement-overlay.tar")
    prefix = "Tests/ConduitLLM.Benchmarks/"
    overlay = {p.removeprefix(prefix): v for p, v in overlay_tar.items()}
    require(all(p.startswith(prefix) for p in overlay_tar) and overlay == inventory(read(root / "measurement-overlay-bindings.json")), "Measurement overlay escaped/differs")
    require({"Program.cs", "ConduitLLM.Benchmarks.csproj", "Streaming/CollectorMicroHarness.cs", "Streaming/WriterMicroHarness.cs", "ProvidersStreamingBenchmarks.cs"} <= set(overlay), "Measurement overlay is incomplete")
    cohorts = {c["id"]: c for c in config["cohorts"]}
    require(set(cohorts) == {"baseline", "candidate"}, "Cohort inventory differs")
    bound = {}; policies = {}; source_bindings = read(root / "source-bindings.json")
    require(len(source_bindings) == 2 and {b["id"] for b in source_bindings} == set(cohorts), "Source binding inventory differs")
    for binding in source_bindings:
        arm = binding["id"]; expected_source = config["baselineRevision"] if arm == "baseline" else config["candidateRevision"]
        revision(binding["sourceTree"])
        require(arm in cohorts and binding["sourceSha"] == cohorts[arm]["sourceSha"] == expected_source and cohorts[arm]["sourceSdk"] == "11.0.100", "Source binding revision differs")
        tar = bound_path(root, binding["archive"])
        require(sha(tar) == binding["archiveSha256"], "Production archive hash differs")
        merged = {p: v for p, v in tar_inventory(tar).items() if not p.startswith(prefix)}
        merged.update(overlay_tar)
        require(merged == inventory(binding["sourceFiles"]), "Only the identical measurement subtree may be overlaid")
        path = root / "work/published" / arm
        bound[arm] = inventory(binding["publishedFiles"])
        require(file_inventory(path) == bound[arm], "Published file set/hash differs")
        runtime = read(path / "ConduitLLM.Benchmarks.runtimeconfig.json")["runtimeOptions"]
        require(runtime["tfm"] == "net11.0", "Published TFM differs")
        frameworks = runtime.get("frameworks", [runtime.get("framework")])
        require(any(f and f["name"] == "Microsoft.NETCore.App" and f["version"] == "11.0.0" for f in frameworks), "Published runtime differs")
        policies[arm] = {k: v for k, v in runtime.get("configProperties", {}).items() if k.startswith("System.GC.")}
    require(policies["baseline"] == policies["candidate"], "Published GC policy differs")
    queue = read(root / "candidate-queue-publish.json")
    queue_files = inventory(queue["files"])
    require(queue["sourceSha"] == config["candidateRevision"] and queue["sourceSdk"] == "11.0.100" and "-p:StreamingMicroOnly=false" in queue["arguments"] and {"ConduitLLM.Benchmarks.dll", "ConduitLLM.Benchmarks.runtimeconfig.json"} <= set(queue_files) and file_inventory(root / "work/candidate-queue") == queue_files, "Required default candidate queue publish differs")
    manifest = read(root / "manifest.json")
    expected = {(arm, run, tool) for arm in cohorts for run in range(1, 6) for tool in ("collector", "writer", "provider")}
    require(len(manifest) == 30 and {(e["arm"], e["run"], e["tool"]) for e in manifest} == expected, "Fresh-process inventory differs")
    grouped = defaultdict(list); semantics = {}; observed_policies = []
    report_sources = []
    for entry in manifest:
        arm = entry["arm"]; cohort = cohorts[arm]
        require(entry["sourceSha"] == cohort["sourceSha"], "Invocation source differs")
        name = f"r{entry['run']}-{entry['tool']}-{arm}"
        original_report = config["outputDirectory"].replace("\\", "/") + "/" + entry["report"].replace("\\", "/")
        original_output = original_report if entry["tool"] == "collector" else original_report.rsplit("/", 1)[0]
        arguments = [cohort["publishedDirectory"] + "/ConduitLLM.Benchmarks.dll", entry["tool"] + "-micro", "--output", original_output]
        if entry["tool"] == "collector":
            arguments += ["--source-sha", cohort["sourceSha"], "--samples", "5"]
        require(entry["program"] == config["dotnet"] and entry["arguments"] == arguments and entry["workingDirectory"] == cohort["sourceDirectory"], "Invocation command/source differs")
        exit = read(bound_path(root, entry["exitReceipt"]))
        require(exit["name"] == name and exit["processId"] == entry["processId"] and exit["exitCode"] == 0 and exit["exitConfirmed"] is True and exit["outputCaptureCompleted"] is True and exit["timedOut"] is False, "Owned micro process did not finish cleanly")
        path = bound_path(root, entry["report"])
        require(sha(path) == entry["reportSha256"], "Raw report hash differs")
        rows, policy = report_rows(entry["tool"], read(path), {**entry, "_publishedDirectory": cohort["publishedDirectory"]}, config["architecture"], bound[arm])
        expected_gc = policies[arm].get("System.GC.Server", False)
        require(policy["actualServerGc"] == expected_gc, "Actual/published GC mode differs")
        observed_policies.append(policy)
        for row in rows:
            group = (arm, entry["run"], entry["tool"], row["case"])
            grouped[group].append(row)
            semantic_key = (entry["tool"], row["case"])
            require(semantic_key not in semantics or semantics[semantic_key] == row["semantic"], "Matched wire/provider evidence differs")
            semantics[semantic_key] = row["semantic"]
        report_sources.append({"file": entry["report"], "sha256": entry["reportSha256"], "processId": entry["processId"]})
    require(all(p == observed_policies[0] for p in observed_policies), "Effective stable GC policy differs; raw diagnostic/address fields are not compared")
    for run in range(1, 6):
        for tool in ("collector", "writer", "provider"):
            require([e["arm"] for e in manifest if e["run"] == run and e["tool"] == tool] == (["baseline", "candidate"] if run % 2 else ["candidate", "baseline"]), "Process pair order did not alternate")
    per_run = []
    for (arm, run, tool, case), rows in sorted(grouped.items()):
        require(len(rows) == 5 and {r["sample"] for r in rows} == set(range(1, 6)), "Inner samples missing/duplicated")
        metrics = {key: None if any(r["metrics"][key] is None for r in rows) else statistics.median(r["metrics"][key] for r in rows) for key in rows[0]["metrics"]}
        per_run.append({"arm": arm, "run": run, "tool": tool, "case": case, "innerSamples": 5, "metrics": metrics})
    comparisons = []
    for tool, case in sorted({(r["tool"], r["case"]) for r in per_run}):
        series = {arm: {r["run"]: r["metrics"] for r in per_run if r["arm"] == arm and r["tool"] == tool and r["case"] == case} for arm in cohorts}
        result = {}
        for metric in next(iter(series["baseline"].values())):
            before = [series["baseline"][run][metric] for run in range(1, 6)]; after = [series["candidate"][run][metric] for run in range(1, 6)]
            available = all(v is not None for v in before + after)
            ratios = [a / b for b, a in zip(before, after)] if available and all(v > 0 for v in before) and ("Cpu" not in metric or all(v > 0 for v in after)) else []
            result[metric] = {"baseline": stats(before) if available else None, "candidate": stats(after) if available else None, "pairedRatios": stats(ratios) if ratios else None, "pairs": len(ratios), "status": "descriptive-only" if ratios else "unavailable-no-selective-subset"}
        comparisons.append({"tool": tool, "case": case, "metrics": result})
    return {"schemaVersion": 1, "collectionValidated": True, "architecture": config["architecture"], "sourceSdk": "11.0.100", "runtime": "11.0.0", "baselineRevision": config["baselineRevision"], "candidateRevision": config["candidateRevision"], "freshProcesses": 30, "independentPairs": 5,
            "effectiveGcPolicyCompared": observed_policies[0], "gcPolicyScope": "Stable policy keys only; full raw GC configuration including address/region/heap-budget diagnostics remains in individual reports.",
            "perRun": per_run, "comparisons": comparisons, "reportSources": report_sources, "sameRuntimeAllocationEvidenceAvailable": True, "performanceAcceptanceGranted": False,
            "scope": "Same-.NET11 synchronous/in-memory micro measurements. Each process contributes a median of5inner samples, then5same-run process ratios; no pooled samples, no HTTP/queue/financial/p99 claim or automatic adoption. Valid.NET11 allocation observations do not repair the historical.NET10 ServerGC baseline.",
            "inputs": {"configurationSha256": sha(root / "configuration.json"), "manifestSha256": sha(root / "manifest.json"), "analyzerSha256": sha(__file__)}, "analysisPython": platform.python_version()}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("--directory", required=True); parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        result = analyze(args.directory); code = 0
    except (EvidenceError, KeyError, ValueError, TypeError, OSError) as error:
        result = {"schemaVersion": 1, "collectionValidated": False, "error": str(error), "performanceAcceptanceGranted": False}; code = 1
    Path(args.output).write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({"collectionValidated": result["collectionValidated"], "output": args.output, "performanceAcceptanceGranted": False}))
    sys.exit(code)
