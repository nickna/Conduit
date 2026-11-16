"""Matched run summaries. Diagnostic thresholds never approve a release/tradeoff."""
from __future__ import annotations
import argparse
import hashlib
import json
import math
import re
from collections import defaultdict
from statistics import median
from pathlib import Path
from qualification import EvidenceError, analysis_identity, host_prefix, integer, money, number, read, register_scoped_ids, report_rows, require, sha256, validate_drains, validate_provider_instances, write

DEFAULT_BUDGETS = {"minimumRuns": 5, "minimumP95SamplesPerRun": 100,
                   "minimumP99SamplesPerRun": 1000, "throughputMinimumRatio": .95,
                   "p50MaximumRatio": 1.05, "p95MaximumRatio": 1.05, "p99MaximumRatio": 1.05,
                   "latencyAbsoluteAllowanceMilliseconds": 2,
                   "cpuMaximumRatio": 1.05, "allocationMaximumRatio": 1.05,
                   "sampledPeakMaximumRatio": 1.10}


def quantile(values, q):
    values = sorted(values)
    return None if not values else values[max(0, math.ceil(len(values) * q) - 1)]


def allocation_evidence(start, end, delta):
    """Retain raw lifetime counters; never manufacture monotonic allocation bytes."""
    raw = integer(delta["allocatedBytes"])
    require(integer(end["totalAllocatedBytes"]) - integer(start["totalAllocatedBytes"]) == raw,
            "Observer allocation delta inconsistent")
    require(integer(start["totalAllocatedBytes"]) >= 0 and integer(end["totalAllocatedBytes"]) >= 0,
            "Negative lifetime allocation counter")
    runtime = start["runtimeVersion"]
    metadata = all(snapshot.get("observerVersion") == 2 for snapshot in (start, end))
    require(not any(snapshot.get("observerVersion") == 2 for snapshot in (start, end)) or metadata,
            "Observer metadata version changed within allocation window")
    affected = False
    gc_config = None
    server_gc = None
    if metadata:
        server_gc = start.get("isServerGc")
        gc_config = start.get("gcConfiguration")
        require(type(server_gc) is bool and end.get("isServerGc") is server_gc,
                "Observer GC mode missing/changed")
        require(isinstance(gc_config, dict) and gc_config and
                all(isinstance(k, str) and isinstance(v, str) for k, v in gc_config.items()) and
                end.get("gcConfiguration") == gc_config, "Observer GC configuration missing/changed")
        require(gc_config.get("ServerGC") == str(server_gc), "Observer GC mode/configuration disagree")
        # Scope conservatively includes every positive window too: absence of a
        # visible decrease cannot establish that no retired heap was omitted.
        affected = runtime == "10.0.15" and server_gc
    require(raw >= 0 or affected, "Negative observer allocation delta outside known.NET10ServerGC defect")
    valid = metadata and not affected
    reason = ("dotnet/runtime#131069: .NET10ServerGC/DATAS retired-heap lifetime allocations can disappear; all windows unavailable, including positive deltas"
              if affected else "GC policy metadata absent; frozen historical observer cannot qualify allocation comparisons"
              if not metadata else "Public precise process-wide lifetime counter difference; window includes background/runtime/observer allocations")
    return {"valid": valid, "status": "available" if valid else "unavailable", "reason": reason,
            "runtime": runtime, "observerVersion": start.get("observerVersion", 1),
            "isServerGc": server_gc, "gcConfiguration": gc_config,
            "rawStartBytes": integer(start["totalAllocatedBytes"]), "rawEndBytes": integer(end["totalAllocatedBytes"]),
            "rawDeltaBytes": raw, "observedNonmonotonic": raw < 0,
            "upstreamFix": "https://github.com/dotnet/runtime/pull/131069" if affected else None,
            "scope": "GC configuration is startup policy metadata, not live heap-count history. No clamp, approximate counter, GC policy override or inferred allocation bound."}


def stream_metric_deltas(before_text, after_text, endpoint, expected_requests):
    """Cumulative server histograms measure stream admission to server events, not wire delivery."""
    names = {"providerFirstChunk": "conduitllm_gateway_streaming_conduit_stream_time_to_provider_first_chunk",
             "clientFirstFlush": "conduitllm_gateway_streaming_conduit_stream_time_to_client_first_flush"}
    def values(text, name):
        result = {"sum": {}, "count": {}}
        pattern = re.compile(r"^" + re.escape(name) + r"_(sum|count)(\{.*\})?\s+(\S+)(?:\s+\S+)?$")
        for line in text.splitlines():
            match = pattern.match(line)
            if match:
                kind, labels, value = match.groups()
                labels = labels or ""
                require(labels not in result[kind], "Duplicate streaming metric series")
                result[kind][labels] = money(value)
                require(result[kind][labels] >= 0, "Negative cumulative streaming metric")
        require(result["sum"].keys() == result["count"].keys(), "Incomplete streaming histogram sum/count series")
        require(all(value == value.to_integral_value() for value in result["count"].values()), "Nonintegral streaming histogram count")
        return result
    expected = expected_requests if endpoint in ("standard-sse", "enhanced-sse") else 0
    output = {}
    for label, name in names.items():
        before, after = values(before_text, name), values(after_text, name)
        require(before["count"].keys() <= after["count"].keys(), "Streaming histogram disappeared/reset")
        for kind in ("sum", "count"):
            require(all(value >= before[kind].get(series, 0) for series, value in after[kind].items()), "Streaming histogram reset/decreased")
        count = sum(after["count"].values()) - sum(before["count"].values())
        seconds = sum(after["sum"].values()) - sum(before["sum"].values())
        require(count == expected and (count > 0 or seconds == 0), "Streaming first-event histogram count does not match measured workload")
        output[label] = {"deltaCount": int(count), "deltaSumSeconds": str(seconds),
                         "meanMilliseconds": float(seconds * 1000 / count) if count else None,
                         "applicable": expected > 0,
                         "scope": "Stream admission to first provider chunk/server awaited first flush; first flush can precede content (e.g.role event). Not per-request wire delivery or histogram-derived p99."}
    return output


def run_statistics(entry, report, server, identity, cohort, architecture):
    rows = report_rows(report, entry["requests"], allow_legacy=bool(entry.get("knownProductionFinding")))
    require(not report["warmup"]["requests"] and report["configuration"]["warmupRequests"] == 0,
            "Measured report contains internal warmups; matched runner uses separate invocations")
    require(report["configuration"]["caseId"] == entry.get("logicalCaseId", entry["caseId"]), "Manifest/client logical case correlation differs")
    require(entry["sourceSha"] == cohort["sourceSha"], "Manifest/cohort source differs")
    require(report["configuration"]["requests"] == entry["requests"] and entry["passed"] == report["passed"], "Manifest/client requested count or outcome differs")
    require(report["runtime"]["architecture"] == architecture, "Client architecture differs")
    window = server["window"]
    start, end, delta = window["start"], window["end"], window["deltas"]
    require(window["id"] == entry["caseId"] and start["processId"] == end["processId"] == entry["gatewayProcessId"], "Observer window/process identity differs")
    require(start["runtimeVersion"] == end["runtimeVersion"] == cohort["expectedRuntime"] and start["architecture"] == end["architecture"] == architecture, "Gateway runtime/architecture differs")
    require(identity["snapshot"]["processId"] == entry["gatewayProcessId"], "Gateway identity snapshot differs")
    for field in ("cpuMilliseconds", "elapsedSeconds", "gcPauseMilliseconds", "gen0Collections", "gen1Collections", "gen2Collections"):
        require(number(delta[field]) >= 0, "Negative observer delta")
    allocation = allocation_evidence(start, end, delta)
    require(not start["inducedFullGc"] and not end["inducedFullGc"], "Measured window contains an induced GC")
    config = report["configuration"]
    fingerprint = dict(config)
    for key in ("caseId", "baseUrl", "providerUrl"):
        fingerprint.pop(key, None)
    if entry.get("logicalCaseId"):
        fingerprint["logicalCaseId"] = entry["logicalCaseId"]
    fingerprint["externalWarmups"] = entry["warmups"]
    signature = hashlib.sha256(json.dumps(fingerprint, sort_keys=True, default=str).encode()).hexdigest()
    valid = [row for row in rows if row["validated"]]
    failures = len(rows) - len(valid)
    known = bool(entry.get("knownProductionFinding"))
    if known:
        require(config["endpoint"] == "responses-json" and len(rows) == 1 and failures == 1 and rows[0]["status"] == 502 and rows[0]["provider"]["completed"] and "cannot be represented by the text-only Responses subset" in (rows[0].get("errorResponseBody") or ""), "Known legacy failure signature changed")
    cancellation = config["cancel"] != "none"
    kind = "known-legacy-failure" if known else "failed" if failures else "cancellation-scenario" if cancellation else "completed-response"
    elapsed = number(report["measured"]["elapsedMilliseconds"])
    require(elapsed > 0, "Missing measured elapsed duration")
    values = [number(row["fullyValidatedMilliseconds"]) for row in valid]
    first = [number(row["timeToFirstContentMilliseconds"]) for row in valid if row["timeToFirstContentMilliseconds"] is not None]
    relay = [number(row["providerWriteBeginToClientContentMilliseconds"]) for row in valid if row.get("providerWriteBeginToClientContentMilliseconds") is not None]
    disconnect = [number(row["cancellationToProviderObservedMilliseconds"]) for row in valid if row.get("cancellationToProviderObservedMilliseconds") is not None]
    provider_spans = [number(row["providerFirstContentToLastFlushMilliseconds"]) for row in valid if row.get("providerFirstContentToLastFlushMilliseconds") is not None]
    count = len(valid)
    metrics = {} if failures else {
        "validatedRatePerSecond": count * 1000 / elapsed,
        "p50Milliseconds": quantile(values, .50), "p95Milliseconds": quantile(values, .95),
        "p99Milliseconds": quantile(values, .99), "firstContentP50Milliseconds": quantile(first, .50),
        "firstContentP95Milliseconds": quantile(first, .95), "firstContentP99Milliseconds": quantile(first, .99),
        "providerWriteBeginToClientContentP50Milliseconds": quantile(relay, .50),
        "providerWriteBeginToClientContentP95Milliseconds": quantile(relay, .95),
        "providerWriteBeginToClientContentP99Milliseconds": quantile(relay, .99),
        "cancellationToProviderObservedP50Milliseconds": quantile(disconnect, .50),
        "cancellationToProviderObservedP95Milliseconds": quantile(disconnect, .95),
        "cancellationToProviderObservedP99Milliseconds": quantile(disconnect, .99),
        "providerFirstContentToLastFlushP50Milliseconds": quantile(provider_spans, .50),
        "responseBytesPerValidated": sum(integer(row["bytesRead"]) for row in valid) / count,
        "serverWindowCpuMillisecondsPerValidated": number(delta["cpuMilliseconds"]) / count,
        "serverWindowAllocatedBytesPerValidated": number(delta["allocatedBytes"]) / count if allocation["valid"] else None,
        "serverWindowSampledPeakWorkingSetBytes": number(window["sampledPeakWorkingSetBytes"]),
        "serverWindowGcPauseMilliseconds": number(delta["gcPauseMilliseconds"]),
        "serverWindowGen0Collections": integer(delta["gen0Collections"]),
        "serverWindowGen1Collections": integer(delta["gen1Collections"]),
        "serverWindowGen2Collections": integer(delta["gen2Collections"])}
    result = {"cohort": entry["cohort"], "case": entry["case"], "caseId": entry["caseId"], "run": entry["run"],
            "kind": kind, "sourceSha": entry["sourceSha"], "runtime": cohort["expectedRuntime"],
            "logicalCaseId": entry.get("logicalCaseId"), "providerInstanceId": entry.get("providerInstanceId"),
            "sourceSdk": cohort["sourceSdk"], "architecture": architecture, "requests": len(rows),
            "validated": count, "failures": failures, "externalWarmups": entry["warmups"],
            "configurationSignature": signature, "caseIdCharacters": len(config["caseId"]),
            "metrics": metrics, "clientRuntime": report["runtime"], "allocationEvidence": allocation,
            "serverWindowScope": entry["serverWindowIncludes"],
            "frameEvidence": "Frozen strict client validator passed per request; counters/provider boundaries cross-checked. DONE/EOF are indirectly covered by that validator, not replayed from raw frames."}
    if config.get("receiveBufferBytes", 0):
        controls = {key: value for key, value in config.items() if key not in ("caseId", "baseUrl", "providerUrl", "slowReadBytes", "slowReadDelayMilliseconds")}
        result["pressureTransport"] = {"requestedReceiveBufferBytes": config["receiveBufferBytes"], "actualReceiveBufferBytes": sorted({socket["actualReceiveBufferBytes"] for socket in report["sockets"]}), "noDelay": True, "connections": len(report["sockets"]), "slowReadBytes": config["slowReadBytes"], "slowReadDelayMilliseconds": config["slowReadDelayMilliseconds"], "controlSignature": hashlib.sha256(json.dumps(controls, sort_keys=True, default=str).encode()).hexdigest(), "scope": "Actual receive buffer is OS reported, not assumed equal to requested. Provider span is first application content-write begin to last awaited application flush, not network completion."}
    return result


def pressure_contrasts(runs):
    groups = defaultdict(dict)
    for row in runs:
        match = re.fullmatch(r"pressure-(standard|enhanced)-(fast|paced)", row["case"])
        if match and "pressureTransport" in row:
            groups[(row["cohort"], match[1], row["run"])][match[2]] = row
    outputs = defaultdict(list)
    for (cohort, protocol, run), arms in sorted(groups.items()):
        require(arms.keys() == {"fast", "paced"}, "Pressure fast/paced control missing")
        fast, paced = arms["fast"], arms["paced"]
        require(fast["pressureTransport"]["controlSignature"] == paced["pressureTransport"]["controlSignature"], "Pressure controls differ beyond explicit read pacing and request markers")
        require(fast["pressureTransport"]["slowReadDelayMilliseconds"] == 0 and paced["pressureTransport"]["slowReadDelayMilliseconds"] == 2 and fast["pressureTransport"]["slowReadBytes"] == 16384 and paced["pressureTransport"]["slowReadBytes"] == 4096, "Pressure read controls changed")
        values = {}
        for metric in ("providerFirstContentToLastFlushP50Milliseconds", "p50Milliseconds", "serverWindowCpuMillisecondsPerValidated", "serverWindowAllocatedBytesPerValidated", "serverWindowSampledPeakWorkingSetBytes"):
            left, right = fast["metrics"].get(metric), paced["metrics"].get(metric)
            values[metric] = {"fast": left, "paced": right, "pacedMinusFast": right - left if left is not None and right is not None else None, "pacedOverFast": right / left if left is not None and left > 0 and right is not None else None}
        outputs[(cohort, protocol)].append({"run": run, "fastCaseId": fast["caseId"], "pacedCaseId": paced["caseId"], "fastActualReceiveBufferBytes": fast["pressureTransport"]["actualReceiveBufferBytes"], "pacedActualReceiveBufferBytes": paced["pressureTransport"]["actualReceiveBufferBytes"], "sameActualBuffer": fast["pressureTransport"]["actualReceiveBufferBytes"] == paced["pressureTransport"]["actualReceiveBufferBytes"], "bothValidated": fast["kind"] == paced["kind"] == "completed-response", "values": values})
    result = []
    for (cohort, protocol), rows in sorted(outputs.items()):
        metrics = {}
        for metric in rows[0]["values"]:
            ratios = [row["values"][metric]["pacedOverFast"] for row in rows if row["bothValidated"] and row["values"][metric]["pacedOverFast"] is not None]
            metrics[metric] = {"medianPairedRatio": median(ratios) if ratios else None, "minimumPairedRatio": min(ratios) if ratios else None, "maximumPairedRatio": max(ratios) if ratios else None, "pairs": len(ratios)}
        result.append({"cohort": cohort, "protocol": protocol, "runs": rows, "metrics": metrics, "acceptanceGranted": False,
                       "scope": "Within-runtime paired fast/paced exploratory controls; single-request samples provide no population p99/capacity proof. Changed/adverse provider spans and memory contrasts are retained. Markers differ between fast/paced cases, while remaining provider controls match. Longer provider spans can support pressure propagation but no automatic causal conclusion is granted."})
    return result


def compare_runs(before, after, budgets=None):
    budgets = DEFAULT_BUDGETS | (budgets or {})
    require(budgets["minimumP99SamplesPerRun"] >= 1000, "Small-sample p99 cannot be enabled")
    require(budgets["minimumRuns"] >= 3, "At least three independent paired runs required")
    b, a = {row["run"]: row for row in before}, {row["run"]: row for row in after}
    require(len(b) == len(before) and len(a) == len(after), "Duplicate comparison run IDs")
    paired = sorted(b.keys() & a.keys())
    diagnostics, ratios = [], {}
    if b.keys() != a.keys():
        diagnostics.append("Run sets differ; only explicit same-run pairs are shown")
    eligible = []
    for run in paired:
        left, right = b[run], a[run]
        if left["kind"] not in ("completed-response", "cancellation-scenario") or left["kind"] != right["kind"]:
            diagnostics.append(f"Run{run}: failure/complete/cancellation categories are not comparable")
            continue
        require(left["configurationSignature"] == right["configurationSignature"], "Matched workload configuration differs")
        require(left["architecture"] == right["architecture"], "Cross-architecture ratio forbidden")
        require(left["caseIdCharacters"] == right["caseIdCharacters"], "Cohort IDs change deterministic payload length")
        eligible.append(run)
    definitions = {
        "validatedRatePerSecond": ("throughputMinimumRatio", "minimum"),
        "p50Milliseconds": ("p50MaximumRatio", "maximum"),
        "p95Milliseconds": ("p95MaximumRatio", "maximum"),
        "p99Milliseconds": ("p99MaximumRatio", "maximum"),
        "serverWindowCpuMillisecondsPerValidated": ("cpuMaximumRatio", "maximum"),
        "serverWindowAllocatedBytesPerValidated": ("allocationMaximumRatio", "maximum"),
        "serverWindowSampledPeakWorkingSetBytes": ("sampledPeakMaximumRatio", "maximum")}
    for metric, (limit_name, direction) in definitions.items():
        if metric == "serverWindowAllocatedBytesPerValidated" and any(
                row.get("allocationEvidence", {}).get("valid") is not True
                for run in eligible for row in (b[run], a[run])):
            ratios[metric] = {"pairs": 0, "medianPairedRatio": None,
                             "minimumPairedRatio": None, "maximumPairedRatio": None,
                             "workingThreshold": budgets[limit_name], "thresholdDirection": direction,
                             "status": "unavailable", "budgetEvaluated": False,
                             "reason": "At least one matched window has unavailable allocation evidence; no selective positive-window ratios or budget pass"}
            continue
        samples = [a[run]["metrics"][metric] / b[run]["metrics"][metric] for run in eligible
                   if b[run]["metrics"].get(metric) is not None and b[run]["metrics"][metric] > 0 and a[run]["metrics"].get(metric) is not None]
        result = {"pairs": len(samples), "medianPairedRatio": median(samples) if samples else None,
                  "minimumPairedRatio": min(samples) if samples else None, "maximumPairedRatio": max(samples) if samples else None,
                  "workingThreshold": budgets[limit_name], "thresholdDirection": direction,
                  "status": "insufficient-evidence"}
        latency = metric in ("p50Milliseconds", "p95Milliseconds", "p99Milliseconds")
        budget_ratios = []
        if latency:
            require(budgets["latencyAbsoluteAllowanceMilliseconds"] >= 0, "Negative latency allowance")
            budget_ratios = [a[run]["metrics"][metric] / max(b[run]["metrics"][metric] * budgets[limit_name], b[run]["metrics"][metric] + budgets["latencyAbsoluteAllowanceMilliseconds"]) for run in eligible if b[run]["metrics"].get(metric) is not None and b[run]["metrics"][metric] > 0 and a[run]["metrics"].get(metric) is not None]
            result.update(workingThresholdRule="after<=max(before*ratio,before+absoluteAllowanceMilliseconds)", workingAbsoluteAllowanceMilliseconds=budgets["latencyAbsoluteAllowanceMilliseconds"], medianPairedBudgetRatio=median(budget_ratios) if budget_ratios else None)
        sample_minimum = budgets["minimumP99SamplesPerRun"] if metric == "p99Milliseconds" else budgets["minimumP95SamplesPerRun"] if metric == "p95Milliseconds" else 1
        tail_ok = all(min(a[run]["validated"], b[run]["validated"]) >= sample_minimum for run in eligible)
        if samples and len(samples) >= budgets["minimumRuns"] and tail_ok:
            outside = median(budget_ratios) > 1 if latency else result["medianPairedRatio"] < budgets[limit_name] if direction == "minimum" else result["medianPairedRatio"] > budgets[limit_name]
            result["status"] = "outside-working-threshold" if outside else "within-working-threshold"
        if not tail_ok:
            result["sampleLimitation"] = f"Each independent run needs at least{sample_minimum} validated observations for this tail diagnostic"
        ratios[metric] = result
    # First-content/proxy/server-event values are compared descriptively. Phase0
    # did not assign them independent production thresholds; do not invent one.
    for metric in ("firstContentP50Milliseconds", "firstContentP95Milliseconds", "firstContentP99Milliseconds",
                   "providerWriteBeginToClientContentP50Milliseconds", "providerWriteBeginToClientContentP95Milliseconds", "providerWriteBeginToClientContentP99Milliseconds",
                   "serverAdmissionToFirstFlushMeanMilliseconds", "serverAdmissionToProviderFirstChunkMeanMilliseconds", "providerFirstContentToLastFlushP50Milliseconds"):
        values = [a[run]["metrics"][metric] / b[run]["metrics"][metric] for run in eligible if b[run]["metrics"].get(metric) is not None and b[run]["metrics"][metric] > 0 and a[run]["metrics"].get(metric) is not None]
        minimum = budgets["minimumP99SamplesPerRun"] if "P99" in metric else budgets["minimumP95SamplesPerRun"] if "P95" in metric else 1
        sufficient = len(values) >= budgets["minimumRuns"] and all(min(a[run]["validated"], b[run]["validated"]) >= minimum for run in eligible)
        ratios[metric] = {"pairs": len(values), "medianPairedRatio": median(values) if values else None,
                          "minimumPairedRatio": min(values) if values else None, "maximumPairedRatio": max(values) if values else None,
                          "status": "descriptive-only" if sufficient else "insufficient-evidence", "workingThreshold": None,
                          "scope": "Request-start/client content, shared-clock provider-write/client-read proxy, or aggregate server-event mean as named; not equivalent clock boundaries or a wire flush proof."}
    return {"pairedRuns": eligible, "metrics": ratios, "diagnostics": diagnostics,
            "allocationGateSatisfied": False,
            "allocationGateStatus": "open-unavailable" if ratios["serverWindowAllocatedBytesPerValidated"]["status"] == "unavailable" else "open-diagnostic-policy-only",
            "thresholdsAreDiagnosticsOnly": True, "acceptanceGranted": False,
            "rateMeaning": "Validated cancellation scenarios/s, not completed responses/s" if eligible and b[eligible[0]]["kind"] == "cancellation-scenario" else "Validated completed responses/s"}


def summarize(directory, configuration, catalog=None):
    directory = Path(directory)
    manifest, bindings, execution = read(directory / "manifest.json"), read(directory / "bindings.json"), read(directory / "execution.json")
    cohorts = {row["id"]: row for row in configuration["cohorts"]}
    for row in cohorts.values():
        require(isinstance(row.get("sourceSdk"), str) and row["sourceSdk"], "Each cohort must record its compilation SDK")
    catalog_path = Path(catalog or configuration["catalog"])
    cases = read(catalog_path)["cases"]
    catalog_bindings = [row for row in bindings["files"] if row["path"] == configuration["catalog"]]
    require(len(catalog_bindings) == 1 and catalog_bindings[0]["sha256"].lower() == sha256(catalog_path), "Catalog bytes differ from execution binding")
    bound_cohorts = {row["id"]: row for row in bindings["cohorts"]}
    require(bound_cohorts.keys() == cohorts.keys(), "Execution/cohort identity set differs")
    for name, cohort in cohorts.items():
        require(bound_cohorts[name]["sourceSha"] == cohort["sourceSha"] and bound_cohorts[name]["expectedRuntime"] == cohort["expectedRuntime"], "Execution/source/runtime cohort binding differs")
        if bound_cohorts[name].get("sourceSdk"):
            require(bound_cohorts[name]["sourceSdk"] == cohort["sourceSdk"], "Execution/compiler cohort binding differs")
    require(execution["executionCompleted"] and not execution["failure"] and not execution["cleanupFailures"], "Execution/cleanup incomplete")
    require(len(manifest) == execution["rows"] == execution["expectedRows"] == configuration["runs"] * len(cohorts) * len(cases), "Matrix inventory incomplete")
    require(bindings["nativeArchitecture"] == configuration["architecture"], "Architecture binding mismatch")
    inventory = {(row["cohort"], row["case"], row["run"]) for row in manifest}
    expected = {(cohort, case["id"], run) for cohort in cohorts for case in cases for run in range(1, configuration["runs"] + 1)}
    require(len(inventory) == len(manifest) and inventory == expected, "Matrix run/case/cohort identities differ")
    runs, sources, global_ids, client_runtimes, instances = [], [], set(), set(), {}
    for entry in manifest:
        path = directory / entry["result"]
        report = read(path)
        register_scoped_ids(global_ids, instances, entry, report["measured"]["requests"])
        client_runtimes.add((report["runtime"]["runtime"], report["runtime"]["architecture"]))
        if entry["warmups"]:
            warm_path = directory / (entry["caseId"] + "-warm.stdout.log")
            warm = read(warm_path)
            warm_rows = report_rows(warm, entry["warmups"])
            require(warm["passed"] and all(row["validated"] for row in warm_rows), "External warmup protocol failed")
            require(warm["configuration"]["caseId"] == entry.get("logicalCaseId", entry["caseId"]) + "-warm", "External warmup logical case identity differs")
            register_scoped_ids(global_ids, instances, entry, warm_rows)
            client_runtimes.add((warm["runtime"]["runtime"], warm["runtime"]["architecture"]))
            sources.append({"file": warm_path.name, "sha256": sha256(warm_path), "phase": "external-warmup"})
        server = read(directory / (entry["caseId"] + "-server-end.json"))
        identity = read(directory / (host_prefix(configuration, entry["cohort"], entry["run"]) + "-identity.json"))
        require(bool(entry.get("knownProductionFinding")) == (bool(cohorts[entry["cohort"]].get("knownResponsesFailure")) and report["configuration"]["endpoint"] == "responses-json"), "Known-finding classification differs from cohort configuration")
        case = next(case for case in cases if case["id"] == entry["case"])
        known = bool(entry.get("knownProductionFinding"))
        require(entry["requests"] == (1 if known else case["requests"]) and entry["warmups"] == (0 if known else case["warmupRequests"]), "Catalog/manifest measured or warmup counts differ")
        for key, value in case.items():
            if key not in ("id", "requests", "warmupRequests"):
                require(report["configuration"][key] == (1 if known and key == "concurrency" else value), "Catalog/client field differs: " + key)
        row = run_statistics(entry, report, server, identity, cohorts[entry["cohort"]], configuration["architecture"])
        before_metrics, after_metrics = directory / (entry["caseId"] + "-before.prometheus"), directory / (entry["caseId"] + "-after.prometheus")
        row["serverStreamFirstEvents"] = stream_metric_deltas(before_metrics.read_text(encoding="utf-8-sig"), after_metrics.read_text(encoding="utf-8-sig"), report["configuration"]["endpoint"], entry["requests"])
        if row["metrics"]:
            row["metrics"]["serverAdmissionToFirstFlushMeanMilliseconds"] = row["serverStreamFirstEvents"]["clientFirstFlush"]["meanMilliseconds"]
            row["metrics"]["serverAdmissionToProviderFirstChunkMeanMilliseconds"] = row["serverStreamFirstEvents"]["providerFirstChunk"]["meanMilliseconds"]
        retained_path = directory / (entry["caseId"] + "-retained-gc.json")
        retained = read(retained_path)["snapshot"]
        require(retained["processId"] == entry["gatewayProcessId"] and retained["inducedFullGc"] is True, "Post-window retained memory snapshot identity/forced collection differs")
        row["retainedMemoryLatestGc"] = {key: retained[key] for key in ("workingSetBytes", "gcHeapSizeBytes", "gcFragmentedBytes", "gcTotalCommittedBytes", "gcIndex", "gcGeneration", "gcCompacted", "gcConcurrent", "generations", "inducedGcElapsedTicks")}
        row["retainedMemoryLatestGc"]["scope"] = "Post-window forced-GC snapshot; latest-GC reported generation sizes and sampled working set, not pure live memory/per-request retention. Snapshot fields sampled sequentially."
        runs.append(row)
        sources.append({"file": entry["result"], "sha256": sha256(path)})
        sources.append({"file": entry["caseId"] + "-server-end.json", "sha256": sha256(directory / (entry["caseId"] + "-server-end.json")), "phase": "server-observer"})
        sources.extend({"file": path.name, "sha256": sha256(path), "phase": "server-metrics-or-retained-memory"} for path in (before_metrics, after_metrics, retained_path))
    require(len(client_runtimes) == 1, "Provider/client runtime is not held constant")
    require(next(iter(client_runtimes))[0] == "10.0.15", "Fixed.NET10client/provider runtime changed")
    lifecycle = []
    for cohort in cohorts:
        for run in range(1, configuration["runs"] + 1):
            entries = [row for row in manifest if row["cohort"] == cohort and row["run"] == run]
            require(len({row["readyMilliseconds"] for row in entries}) == 1, "Host readiness differs within one process")
            prefix = host_prefix(configuration, cohort, run)
            shutdown = read(directory / (prefix + "-shutdown.json"))
            drain_path = directory / (prefix + "-financial-drain.json")
            lifecycle.append({"cohort": cohort, "run": run, "gatewayProcessId": entries[0]["gatewayProcessId"],
                              "readyMilliseconds": number(entries[0]["readyMilliseconds"]),
                              "shutdownMilliseconds": number(shutdown["shutdownMilliseconds"]),
                              "knownSpendDrainElapsedMilliseconds": number(read(drain_path)["elapsedMs"]) if configuration.get("drainProbe") else None,
                              "scope": "One fresh host process; readiness, pre-shutdown known-spend drain and graceful shutdown are outside measured workload observer windows."})
    grouped, cohort_summaries = defaultdict(list), []
    for row in runs:
        grouped[(row["cohort"], row["case"])].append(row)
    for (cohort, case), rows in sorted(grouped.items()):
        successful = [row for row in rows if row["metrics"]]
        metrics = {}
        if successful:
            for name in successful[0]["metrics"]:
                values = [row["metrics"][name] for row in successful if row["metrics"][name] is not None]
                metrics[name] = {"medianOfRunStatistics": median(values), "minimumRunStatistic": min(values), "maximumRunStatistic": max(values)} if values else None
        cohort_summaries.append({"cohort": cohort, "case": case, "runs": len(rows), "requests": sum(row["requests"] for row in rows),
                                 "validated": sum(row["validated"] for row in rows), "failures": sum(row["failures"] for row in rows),
                                 "kind": sorted({row["kind"] for row in rows}), "metrics": metrics})
    comparisons = []
    pairs = configuration.get("comparisons", [{"baseline": "fixed", "candidate": "candidate"}, {"baseline": "legacy", "candidate": "fixed"}])
    for pair in pairs:
        for case in cases:
            left, right = grouped.get((pair["baseline"], case["id"]), []), grouped.get((pair["candidate"], case["id"]), [])
            if left and right:
                comparisons.append({"baseline": pair["baseline"], "candidate": pair["candidate"], "case": case["id"],
                                    **compare_runs(left, right, configuration.get("diagnosticBudgets"))})
    return {"schemaVersion": 1, "collectionValidated": True, "allWorkloadsPassed": all(row["failures"] == 0 for row in runs),
            "collectionValidationScope": "Evidence collection and unaffected metric invariants; allocation availability is explicit per window and never implied by collection success",
            "acceptanceGranted": False, "performanceAcceptanceGranted": False, "allocationGateSatisfied": False,
            "allocationGateStatus": "open-unavailable" if any(not row["allocationEvidence"]["valid"] for row in runs) else "open-diagnostic-policy-only",
            "aggregation": "Median/range of independent per-run statistics and paired ratios; no pooled request quantiles",
            "cohorts": list(cohorts.values()), "architecture": configuration["architecture"], "bindings": bindings,
            "catalogSha256": sha256(catalog_path), "profileDirectoryScope": str(directory.resolve()),
            "inputEvidence": {name: sha256(directory / name) for name in ("manifest.json", "bindings.json", "execution.json")},
            "profile": configuration.get("profile"), "measurementScope": configuration.get("scope"),
            "runs": runs, "cohortSummaries": cohort_summaries, "comparisons": comparisons, "reportSources": sources, "lifecycle": lifecycle,
            "knownPersistedSpendDrain": validate_drains(directory, manifest, configuration, next(iter(client_runtimes))[0]),
            "pressureContrasts": pressure_contrasts(runs),
            "providerInstances": validate_provider_instances(directory, manifest, configuration),
            "budgetPolicy": "Working diagnostic thresholds only; violations and low evidence require review, never automatic tradeoff acceptance",
            "financialQualification": "Requires separate exact reconciliation; validated HTTP/cancellation is not durable settlement proof",
            "limitations": ["Synthetic provider, local fixture and observed server windows are not production capacity.",
                            "Observer windows include client startup/finalization and server background work; no idle subtraction.",
                            "Retained/sampled memory is not an instantaneous allocation peak; CPU/allocation divided by requests are window-normalized.",
                            "No p99 acceptance is granted for small samples; per-run exploratory values remain visible.",
                            ".NET10.0.15 ServerGC precise allocation counters omit retired DATAS heaps; all affected windows retain raw counters but allocation metrics/ratios/budgets are unavailable, even for positive deltas. No production-default allocation acceptance is granted."]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", required=True)
    parser.add_argument("--configuration", required=True)
    parser.add_argument("--catalog", help="Explicit local copy of a catalog whose recorded path belongs to the execution container")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        configuration = read(args.configuration)
        result = summarize(args.directory, configuration, args.catalog)
        matches = sha256(args.configuration) == result["bindings"]["configurationSha256"]
        require(matches or configuration.get("replayMetadata", {}).get("originalConfigurationSha256") == result["bindings"]["configurationSha256"], "Analysis configuration bytes differ from execution binding; historical replay must declare its original hash")
        result["configurationEvidence"] = {"file": str(Path(args.configuration).resolve()), "sha256": sha256(args.configuration), "matchesExecutionConfigurationHash": matches, "historicalMetadataReplayOnly": not matches}
        result["analysisIdentity"] = analysis_identity()
        write(args.output, result)
        return 0
    except (EvidenceError, KeyError, TypeError, ValueError, OSError) as error:
        write(args.output, {"schemaVersion": 1, "collectionValidated": False, "evidenceError": str(error)})
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
