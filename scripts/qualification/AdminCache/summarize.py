"""Strict inventory replay and paired diagnostics; never release acceptance."""
from __future__ import annotations
import argparse
import hashlib
import json
import math
from collections import defaultdict
from pathlib import Path
from statistics import median

BUDGETS = {"minimumRuns": 5, "minimumP95SamplesPerRun": 100, "minimumP99SamplesPerRun": 1000,
           "throughputMinimumRatio": .95, "p50MaximumRatio": 1.05, "p95MaximumRatio": 1.05,
           "p99MaximumRatio": 1.05, "latencyAbsoluteAllowanceMilliseconds": 2,
           "cpuMaximumRatio": 1.05, "allocationMaximumRatio": 1.05, "sampledPeakMaximumRatio": 1.10}
ORIGIN = "f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa"


def require(value, message):
    if not value:
        raise ValueError(message)


def object_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=object_pairs,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Nonfinite JSON: " + value)))


def number(value):
    require(isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value), "Expected finite number")
    return value


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def quantile(values, fraction):
    values = sorted(map(number, values))
    return values[max(0, math.ceil(len(values) * fraction) - 1)]


def distribution(values):
    return {key: quantile(values, fraction) for key, fraction in (("P50", .5), ("P95", .95), ("P99", .99))}


def stats(values):
    return {"medianOfRunStatistics": median(values), "minimumRunStatistic": min(values), "maximumRunStatistic": max(values)} if values else None


def observer_window(path, expected_pid, runtime, architecture, validated, affected_allocation=False):
    body = read(path)
    window = body["window"]
    for snapshot in (window["start"], window["end"]):
        require(snapshot["processId"] == expected_pid and snapshot["runtimeVersion"] == runtime and snapshot["architecture"] == architecture, "Observer identity mismatch")
    require(window["samplingIntervalMs"] == 100, "Working-set interval changed")
    deltas = window["deltas"]
    for field, before, after in (("cpuMilliseconds", "totalCpuMilliseconds", "totalCpuMilliseconds"),
                                  ("allocatedBytes", "totalAllocatedBytes", "totalAllocatedBytes"),
                                  ("gen0Collections", "gen0Collections", "gen0Collections"),
                                  ("gen1Collections", "gen1Collections", "gen1Collections"),
                                  ("gen2Collections", "gen2Collections", "gen2Collections")):
        expected = window["end"][after] - window["start"][before]
        require(math.isclose(number(deltas[field]), number(expected), rel_tol=1e-12, abs_tol=1e-6), "Observer delta does not replay: " + field)
    issues = [field + " decreased" for field in ("cpuMilliseconds", "allocatedBytes", "gen0Collections", "gen1Collections", "gen2Collections", "gcPauseMilliseconds") if number(deltas[field]) < 0]
    require(all(deltas[field] >= 0 for field in ("cpuMilliseconds", "gen0Collections", "gen1Collections", "gen2Collections", "gcPauseMilliseconds")), "Unexpected negative observer counter")
    require(deltas["allocatedBytes"] >= 0 or affected_allocation, "Negative allocation counter outside known.NET10ServerGC scope")
    return {"serverWindowCpuMillisecondsPerValidated": deltas["cpuMilliseconds"] / validated if deltas["cpuMilliseconds"] >= 0 else None,
            "serverWindowAllocatedBytesPerValidated": deltas["allocatedBytes"] / validated if deltas["allocatedBytes"] >= 0 and not affected_allocation else None,
            "serverWindowSampledPeakWorkingSetBytes": window["sampledPeakWorkingSetBytes"],
            "serverWindowGen0PerValidated": deltas["gen0Collections"] / validated,
            "serverWindowGen1PerValidated": deltas["gen1Collections"] / validated,
            "serverWindowGen2PerValidated": deltas["gen2Collections"] / validated,
            "serverWindowGcPauseMillisecondsPerValidated": deltas["gcPauseMilliseconds"] / validated}, issues


def microprocess(path, expected_pid, runtime, architecture, operations):
    body = read(path)
    require(body["processId"] == expected_pid and body["framework"] == ".NET " + runtime and body["architecture"] == architecture, "Microprocess identity mismatch")
    require(type(body["serverGC"]) is bool, "Microprocess GC mode is not a boolean")
    start, end = body["start"], body["end"]
    points = body["allocationCounterSamples"]
    require(points[0]["allocatedBytes"] == start["allocatedBytes"] and points[-1]["allocatedBytes"] == end["allocatedBytes"], "Allocation timeline endpoints differ")
    require(all(right["timestamp"] >= left["timestamp"] for left, right in zip(points, points[1:])), "Allocation timeline order invalid")
    decreases = [{"before": left, "after": right} for left, right in zip(points, points[1:]) if right["allocatedBytes"] < left["allocatedBytes"]]
    cpu, allocated = end["cpuMilliseconds"] - start["cpuMilliseconds"], end["allocatedBytes"] - start["allocatedBytes"]
    affected = runtime == "10.0.15" and body["serverGC"]
    require(cpu >= 0 and all(end[key] >= start[key] for key in ("gen0", "gen1", "gen2", "gcPauseMilliseconds")), "Unexpected negative microprocess CPU/GC counter")
    require((not decreases and allocated >= 0) or affected, "Unqualified microprocess allocation counter decrease")
    issues = (["process-wide allocation counter decreased"] if decreases or allocated < 0 else []) + (["CPU counter decreased"] if cpu < 0 else [])
    metrics = {"wholeInvocationCpuMillisecondsPerAllOperation": cpu / operations if cpu >= 0 else None,
               "wholeInvocationAllocatedBytesPerAllOperation": allocated / operations if allocated >= 0 and not decreases and not affected else None,
               "wholeInvocationSampledPeakWorkingSetBytes": body["sampledPeakWorkingSetBytes"],
               "wholeInvocationGcPauseMillisecondsPerAllOperation": (end["gcPauseMilliseconds"] - start["gcPauseMilliseconds"]) / operations}
    metrics.update({f"wholeInvocationGen{generation}PerAllOperation": (end[f"gen{generation}"] - start[f"gen{generation}"]) / operations for generation in range(3)})
    return {"metrics": metrics, "rawAllocatedDelta": allocated, "allocationCounterDecreases": decreases,
            "counterIssues": issues, "allOperations": operations, "periodicSamples": body["periodicSamples"],
            "serverGC": body["serverGC"], "gcConfigurationVariables": body["gcConfigurationVariables"],
            "scope": body["scope"], "allocationCounterSource": body["allocationCounterSource"]}


def paired(before, after, definitions=None, samples=None, functional=False):
    left, right = {item["run"]: item for item in before}, {item["run"]: item for item in after}
    require(left.keys() == right.keys() and len(left) == len(before) and len(right) == len(after), "Comparison run sets differ")
    metrics = {}
    for metric in sorted(left[next(iter(left))]["metrics"]):
        pairs = [(run, right[run]["metrics"].get(metric), left[run]["metrics"].get(metric)) for run in left]
        if any(a is None or b is None for _, a, b in pairs):
            metrics[metric] = {"pairs": 0, "medianPairedRatio": None, "minimumPairedRatio": None, "maximumPairedRatio": None,
                               "status": "unavailable", "workingThreshold": None, "budgetEvaluated": False,
                               "reason": "At least one matched window lacks qualified metric evidence; no selective positive-window ratios or budget acceptance."}
            continue
        ratios = [number(a) / number(b) for _, a, b in pairs if a is not None and b is not None and b > 0]
        row = {"pairs": len(ratios), "medianPairedRatio": median(ratios) if ratios else None,
               "minimumPairedRatio": min(ratios) if ratios else None, "maximumPairedRatio": max(ratios) if ratios else None,
               "status": "functional-smoke-only" if functional else "descriptive-only", "workingThreshold": None}
        if definitions and metric in definitions:
            key, direction = definitions[metric]
            row.update(workingThreshold=BUDGETS[key], thresholdDirection=direction, status="insufficient-evidence")
            minimum = BUDGETS["minimumP99SamplesPerRun"] if metric == "p99Milliseconds" else BUDGETS["minimumP95SamplesPerRun"] if metric == "p95Milliseconds" else 1
            sufficient = not functional and len(ratios) >= BUDGETS["minimumRuns"] and (samples is None or samples >= minimum)
            if metric in ("p50Milliseconds", "p95Milliseconds", "p99Milliseconds"):
                budget_ratios = [a / max(b * BUDGETS[key], b + BUDGETS["latencyAbsoluteAllowanceMilliseconds"]) for _, a, b in pairs if a is not None and b is not None and b > 0]
                row.update(workingThresholdRule="after<=max(before*ratio,before+absoluteAllowanceMilliseconds)", workingAbsoluteAllowanceMilliseconds=2,
                           medianPairedBudgetRatio=median(budget_ratios) if budget_ratios else None)
                outside = bool(budget_ratios) and median(budget_ratios) > 1
            else:
                outside = bool(ratios) and (median(ratios) < BUDGETS[key] if direction == "minimum" else median(ratios) > BUDGETS[key])
            if sufficient:
                row["status"] = "outside-working-threshold" if outside else "within-working-threshold"
        metrics[metric] = row
    return {"metrics": metrics, "thresholdsAreDiagnosticsOnly": True, "performanceAcceptanceGranted": False}


def summarize(directory, configuration):
    manifest = read(directory / "manifest.json")
    cohorts = {cohort["id"]: cohort for cohort in configuration["cohorts"]}
    bindings = {binding["id"]: binding for binding in read(directory.parent / "source-bindings.json")}
    fixed_files = {item["path"]: item["sha256"] for item in read(directory.parent / "fixed-harness-bindings.json")["files"]}
    production_files = {}
    for name, cohort in cohorts.items():
        require(bindings[name]["sourceSha"] == cohort["sourceSha"], "Published source binding differs")
        production_files[name] = {item["path"]: item["sha256"] for item in bindings[name]["files"]}
    require(set(cohorts) == {"legacy", "fixed", "candidate"}, "Cohort set invalid")
    count = configuration["runs"]
    require(count == 1 if configuration["functionalSmoke"] else 3 <= count <= 6, "Run inventory invalid")
    require(len(manifest) == count * 3 * 4, "Manifest inventory incomplete")
    expected = {(cohort, run, kind, mode) for cohort in cohorts for run in range(1, count + 1) for kind, mode in (("admin", ""), ("cache", "write"), ("cache", "read"), ("collector", ""))}
    actual = {(row["cohort"], row["run"], row["kind"], row.get("mode", "")) for row in manifest}
    require(len(actual) == len(manifest) and actual == expected, "Manifest duplicate/missing cohort state")
    admin, cache, collector, whole, issues = [], [], [], [], []
    fingerprints = set()
    for entry in manifest:
        cohort = cohorts[entry["cohort"]]
        require(entry["sourceSha"] == cohort["sourceSha"], "Source attribution mismatch")
        base = {"cohort": cohort["id"], "run": entry["run"], "sourceSha": cohort["sourceSha"]}
        if entry["kind"] == "admin":
            raw = read(directory / entry["steady"])
            result = raw["workloads"][0]
            require(raw["schemaVersion"] == 2 and raw["service"] == "admin" and len(raw["workloads"]) == 1, "Admin report invalid")
            require(result["requested"] == result["executed"] == result["completed"] == configuration["adminRequests"] and result["failures"] == 0, "Admin validated count mismatch")
            require(result["warmupRequests"] == configuration["adminWarmups"] and result["concurrency"] == 8 and result["authenticated"], "Admin warmup/concurrency/auth mismatch")
            fingerprints.add(result["inputFingerprint"])
            for part in ("first", "steady"):
                client_metadata = read(directory / (Path(entry[part]).name.removesuffix(".stdout.log") + ".process.json"))
                require(client_metadata["framework"] == ".NET 10.0.15" and client_metadata["architecture"] == configuration["architecture"] and client_metadata["samplingIntervalMilliseconds"] == 0, "Fixed client runtime/architecture/instrumentation differs")
                require(client_metadata["harnessAssemblySha256"] == fixed_files["seed/ConduitLLM.GatewayNativeAotTests.dll"], "Fixed client assembly changed")
            host = read(directory / entry["steadyWindow"])["window"]["start"]
            total = configuration["adminRequests"] + configuration["adminWarmups"]
            require(entry["validatedWindowCalls"] == total, "Admin numerator/denominator scope differs")
            metadata = read(directory / entry["runtimeMetadata"])
            require(metadata["processId"] == host["processId"] and metadata["architecture"] == configuration["architecture"] and metadata["framework"] == ".NET " + cohort["expectedRuntime"], "Admin GC-mode metadata identity differs")
            require(metadata["samplingIntervalMilliseconds"] == 0, "Admin metadata observer unexpectedly sampled")
            require(type(metadata["serverGC"]) is bool, "Admin GC mode is not a boolean")
            affected = cohort["expectedRuntime"] == "10.0.15" and metadata["serverGC"]
            metrics, bad = observer_window(directory / entry["steadyWindow"], host["processId"], cohort["expectedRuntime"], configuration["architecture"], total, affected)
            if affected:
                metrics["serverWindowAllocatedBytesPerValidated"] = None
                bad.append("Default.NET10ServerGC precise process-allocation counter affected by active-versus-maximum heap aggregation bug; positive deltas are also unqualified")
            metrics.update(validatedRatePerSecond=result["throughputPerSecond"], p50Milliseconds=result["p50Milliseconds"], p95Milliseconds=result["p95Milliseconds"], p99Milliseconds=result["p99Milliseconds"])
            first = read(directory / entry["first"])["workloads"][0]
            require(first["completed"] == 1 and first["failures"] == first["warmupRequests"] == 0, "First-read result invalid")
            first_metrics, first_bad = observer_window(directory / entry["firstWindow"] , host["processId"], cohort["expectedRuntime"], configuration["architecture"], 1, affected)
            if affected:
                first_metrics["serverWindowAllocatedBytesPerValidated"] = None
            startup = read(directory / entry["startup"])["window"]
            row = base | {"metrics": metrics, "measuredRequests": result["completed"], "warmupRequests": result["warmupRequests"],
                          "normalizer": total, "readyMilliseconds": entry["readyMilliseconds"], "firstValidatedReadMilliseconds": first["p50Milliseconds"],
                          "firstReadServerWindow": first_metrics, "startupCpuAtReadyMilliseconds": startup["end"]["totalCpuMilliseconds"],
                          "startupLifetimePeakWorkingSetAtReadyBytes": startup["end"]["processLifetimePeakWorkingSetBytes"],
                          "serverGC": metadata["serverGC"], "gcConfigurationVariables": metadata["gcConfigurationVariables"],
                          "processAllocationCounterQualified": not affected,
                          "counterIssues": bad + first_bad, "windowScope": entry["serverWindowIncludes"]}
            admin.append(row)
            issues.extend({"cohort": cohort["id"], "run": entry["run"], "scope": "admin", "issue": value} for value in row["counterIssues"])
        else:
            raw = read(directory / entry["report"])
            require(raw["Succeeded"] and raw["SourceRevision"] == ORIGIN and raw["Framework"] == ".NET " + cohort["expectedRuntime"], "Frozen micro report/source/runtime invalid")
            sidecar = microprocess(directory / entry["process"], raw["ProcessId"], cohort["expectedRuntime"], configuration["architecture"],
                                   entry["totalCacheLookups"] if entry["kind"] == "cache" else entry["allStreams"])
            observed = read(directory / entry["process"])
            filename = "Conduit.CacheMeasurements.dll" if entry["kind"] == "cache" else "Conduit.CollectorMeasurements.dll"
            expected_harness = production_files[cohort["id"]][cohort["id"] + "-" + entry["kind"] + "/" + filename]
            require(observed["harnessAssemblySha256"] == expected_harness, "Micro harness binary changed after build binding")
            require(observed["samplingIntervalMilliseconds"] == 100, "Micro counter sampling policy changed")
            affected = cohort["expectedRuntime"] == "10.0.15" and observed["serverGC"]
            if affected:
                sidecar["metrics"]["wholeInvocationAllocatedBytesPerAllOperation"] = None
                sidecar["counterIssues"].append("Default.NET10ServerGC process-allocation aggregate unqualified even when positive")
            whole.append(base | {"kind": entry["kind"], "mode": entry.get("mode"), **sidecar})
            issues.extend({"cohort": cohort["id"], "run": entry["run"], "scope": entry["kind"], "issue": value} for value in sidecar["counterIssues"])
            if entry["kind"] == "cache":
                for assembly in raw["ProductionAssemblies"]:
                    expected_assembly = production_files[cohort["id"]].get(cohort["id"] + "-cache/" + assembly["Name"] + ".dll")
                    require(expected_assembly == assembly["Sha256"], "Actual cache production assembly differs from published binding")
                require(raw["Warmups"] == configuration["cacheWarmups"] and raw["MeasuredPerState"] == configuration["cacheSamples"], "Cache configured count differs")
                require(raw["Fixture"]["DiscoveryModelCount"] == 32 and raw["Fixture"]["PricingAssociationCount"] == 8, "Complete graph fixture changed")
                states = {"cold-distinct-key", "immediate-l1-owned-hit"} if entry["mode"] == "write" else {"independent-process-l2-distinct-key"}
                require({(row["Domain"], row["State"]) for row in raw["Rows"]} == {(domain, state) for domain in ("mapping", "discovery", "pricing") for state in states}, "Cache state inventory differs")
                expected_loads = configuration["cacheWarmups"] + configuration["cacheSamples"] if entry["mode"] == "write" else 0
                require(raw["FactoryCalls"] == {domain: expected_loads for domain in ("mapping", "discovery", "pricing")}, "Cache loads/repair count differs")
                for item in raw["Rows"]:
                    values = item["Samples"]
                    require(len(values) == configuration["cacheSamples"] and [value["Index"] for value in values] == list(range(len(values))), "Cache samples missing/duplicated")
                    expected_calls = int(item["State"] == "cold-distinct-key")
                    require(all(value["BusinessLoaderCalls"] == expected_calls for value in values), "Cache sample state disagrees with loader count")
                    for key, value in distribution([value["Microseconds"] for value in values]).items():
                        require(math.isclose(item["LatencyMicroseconds"][key], value, abs_tol=1e-9), "Cache latency quantile differs")
                    allocated = [number(value["ProcessWideAllocatedBytes"]) for value in values]
                    bad = [value for value in values if value["ProcessWideAllocatedBytes"] < 0]
                    require(not bad or affected, "Negative timed cache allocation outside known.NET10ServerGC scope")
                    if bad:
                        issues.append(base | {"scope": "cache-timed-operation", "domain": item["Domain"], "state": item["State"], "issue": "Negative raw process-wide allocation increments", "rawSamples": bad})
                    if item["State"] == "immediate-l1-owned-hit": require(item["MaximumColdToL1EndMilliseconds"] < 80, "L1 TTL state unverified")
                    cache.append(base | {"domain": item["Domain"], "state": item["State"], "samples": len(values), "metrics":
                        {**{"latency" + key + "Microseconds": value for key, value in distribution([value["Microseconds"] for value in values]).items()},
                         **{"processWideAllocatedBytes" + key: value if not bad and not affected else None for key, value in distribution(allocated).items()}},
                        "allocationCounterValid": not bad and not affected, "businessLoaderCallsMeasured": item["BusinessLoaderCalls"],
                        "originLiteralSourceRevision": raw["SourceRevision"], "productionAssemblies": raw["ProductionAssemblies"]})
            else:
                require(raw["HarnessAssemblySha256"] == expected_harness, "Collector self-reported binary hash differs")
                require(raw["ProductionAssemblySha256"] == production_files[cohort["id"]][cohort["id"] + "-collector/ConduitLLM.Core.dll"], "Collector production Core assembly differs")
                require(len(raw["Rows"]) == 8 and raw["Architecture"] == configuration["architecture"], "Collector size/mode inventory differs")
                require({(item["ContentChunks"], item["Mode"]) for item in raw["Rows"]} == {(size, mode) for size in (10, 100, 1000, 10000) for mode in ("snapshot-each-content-chunk", "record-plus-final-snapshot-control")}, "Collector sizes/modes missing")
                for item in raw["Rows"]:
                    values = item["Samples"]
                    require(len(values) == 3 and all(value["ContentChunksRecorded"] == item["ContentChunks"] and value["ThreadLocalAllocatedBytes"] >= 0 for value in values), "Collector sample state differs")
                    collector.append(base | {"chunks": item["ContentChunks"], "mode": item["Mode"], "measuredStreams": 3, "metrics":
                        {"medianLatencyMicroseconds": median([number(value["Microseconds"]) for value in values]),
                         "medianThreadLocalAllocatedBytes": median([number(value["ThreadLocalAllocatedBytes"]) for value in values])},
                        "productionAssemblySha256": raw["ProductionAssemblySha256"], "originLiteralSourceRevision": raw["SourceRevision"]})
    require(len(fingerprints) == 1, "Admin input differs across paired cohorts")
    grouped = defaultdict(list)
    for kind, rows in (("admin", admin), ("cache", cache), ("collector", collector)):
        for row in rows:
            case = "admin-setting-read" if kind == "admin" else row["domain"] + ":" + row["state"] if kind == "cache" else str(row["chunks"]) + ":" + row["mode"]
            grouped[(kind, case, row["cohort"])].append(row)
    comparisons = []
    definitions = {"validatedRatePerSecond": ("throughputMinimumRatio", "minimum"), "p50Milliseconds": ("p50MaximumRatio", "maximum"),
                   "p95Milliseconds": ("p95MaximumRatio", "maximum"), "p99Milliseconds": ("p99MaximumRatio", "maximum"),
                   "serverWindowCpuMillisecondsPerValidated": ("cpuMaximumRatio", "maximum"),
                   "serverWindowAllocatedBytesPerValidated": ("allocationMaximumRatio", "maximum"),
                   "serverWindowSampledPeakWorkingSetBytes": ("sampledPeakMaximumRatio", "maximum")}
    for baseline, candidate in (("legacy", "fixed"), ("fixed", "candidate"), ("legacy", "candidate")):
        for kind, case in sorted({(key[0], key[1]) for key in grouped}):
            comparison = paired(grouped[(kind, case, baseline)], grouped[(kind, case, candidate)], definitions if kind == "admin" else None,
                                configuration["adminRequests"] if kind == "admin" else None, configuration["functionalSmoke"])
            comparisons.append({"kind": kind, "case": case, "baseline": baseline, "candidate": candidate, **comparison})
    summaries = []
    for (kind, case, cohort), rows in sorted(grouped.items()):
        summaries.append({"kind": kind, "case": case, "cohort": cohort, "runs": len(rows), "metrics":
                          {key: stats([row["metrics"][key] for row in rows if row["metrics"][key] is not None]) for key in rows[0]["metrics"]}})
    return {"schemaVersion": 1, "collectionValidated": True, "functionalSmoke": configuration["functionalSmoke"],
            "architecture": configuration["architecture"], "sourceCohorts": list(cohorts.values()), "adminRuns": admin,
            "cacheRuns": cache, "collectorRuns": collector, "wholeMicroprocessRuns": whole, "summaries": summaries,
            "comparisons": comparisons, "counterValidityIssues": issues, "allCounterBasedMetricsQualified": not issues,
            "allocationGateStatus": "open-unavailable" if any(not row["processAllocationCounterQualified"] for row in admin) else "evidence-collected-no-acceptance",
            "allocationGateSatisfied": False,
            "workingAdminBudgets": BUDGETS, "performanceAcceptanceGranted": False, "financialQualification": False,
            "aggregation": "Median/range of per-run quantiles and explicit same-run candidate/baseline ratios; never pooled quantiles. Collector uses median of3streams/process, then median/range across processes.",
            "microScope": "Complete cache graphs and actual StreamingMetricsCollector call patterns. Logical loader/fill/hit states strictly validated. No collector queue/drain API exists; no delivery or application capacity claim. Whole-process diagnostics include warmups, fixture/validation/serialization/disposal/observer and ownership rereads, not timed-row pure cost.",
            "allocationCaveat": "Raw counters never clamped. ALL.NET10.0.15 ServerGC process-wide allocation windows are unqualified, including positive deltas, because active-heap aggregation can omit retired heaps (dotnet/runtime#131069). Actual GC mode/config retained. Unexpected counter decreases outside this scope fail replay. WorkstationGC is not affected by this particular active-heap omission; synchronous same-thread collector allocation is independent. A missing metric invalidates every matching allocation ratio; no selective positive-window acceptance.",
            "originAttribution": "Embedded frozenSourceRevision=f68 and oldWindows/noARM narrative describe phase0fixture origin, not current production/cohort/runtime/architecture. External recipes, actualdynamicruntime fields and production assembly hashes identify each execution.",
            "inputs": {"configurationSha256": sha(directory / "configuration.source.json"), "manifestSha256": sha(directory / "manifest.json"),
                       "analyzerSha256": sha(__file__)}}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--configuration", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.directory, read(args.configuration))
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({"collectionValidated": True, "adminRuns": len(result["adminRuns"]), "cacheStateRows": len(result["cacheRuns"]),
                      "collectorRows": len(result["collectorRuns"]), "counterIssues": len(result["counterValidityIssues"])}))
