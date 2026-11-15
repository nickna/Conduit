"""Read-only helpers for frozen phase-2 provider/client reports."""
from __future__ import annotations
import hashlib
import json
import math
import platform
import sys
from decimal import Decimal, getcontext
from pathlib import Path
from urllib.parse import unquote, urlsplit

getcontext().prec = 80  # Exact SQL decimal(20,8), rates, units and finite fixture sums.


class EvidenceError(ValueError):
    pass


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise EvidenceError(f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def loads(text):
    return json.loads(text, parse_float=Decimal, object_pairs_hook=_object,
                      parse_constant=lambda value: (_ for _ in ()).throw(EvidenceError(f"Nonfinite JSON: {value}")))


def read(path):
    return loads(Path(path).read_text(encoding="utf-8-sig"))


def write(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False,
                                    default=lambda item: str(item) if isinstance(item, Decimal) else (_ for _ in ()).throw(TypeError(type(item).__name__))) + "\n", encoding="utf-8")


def require(condition, message):
    if not condition:
        raise EvidenceError(message)


def number(value):
    require(not isinstance(value, bool) and isinstance(value, (int, float, Decimal)), "Expected numeric evidence")
    result = float(value)
    require(math.isfinite(result), "Nonfinite numeric evidence")
    return result


def money(value):
    # Never accept an already-rounded binary float at an accounting boundary.
    require(not isinstance(value, (bool, float)) and isinstance(value, (str, int, Decimal)), "Money must be an exact string/Decimal/integer")
    try:
        result = Decimal(value)
    except Exception as error:
        raise EvidenceError("Invalid exact monetary value") from error
    require(result.is_finite(), "Nonfinite money")
    return result


def integer(value):
    result = money(value)
    require(result == result.to_integral_value(), "Nonintegral units/count")
    return int(result)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def analysis_identity():
    root = Path(__file__).resolve().parent
    return {"python": sys.version, "implementation": platform.python_implementation(), "os": platform.platform(), "architecture": platform.machine(),
            "sources": [{"file": name, "sha256": sha256(root / name)} for name in ("qualification.py", "summarize.py", "reconcile.py")]}


def host_prefix(configuration, cohort, run):
    return ((configuration["profile"] + "-") if configuration.get("profile") else "") + cohort.ljust(16, "-") + f"-r{run}"


def register_scoped_ids(seen, instances, entry, rows):
    instance = entry.get("providerInstanceId", "historical-profile-provider")
    if "providerInstanceId" in entry:
        require(isinstance(instance, str) and instance and entry.get("logicalCaseId"), "New provider scope requires explicit logical case identity")
        owner = (entry["cohort"], entry["run"], entry.get("providerProcessId"))
        require(instance not in instances or instances[instance] == owner, "Provider instance reused across different cohort/run/process")
        instances[instance] = owner
    scoped = {(instance, row["id"]) for row in rows}
    require(len(scoped) == len(rows) and not (seen & scoped), "Request markers repeated within one provider instance")
    seen.update(scoped)


def validate_pressure_report(report, rows):
    config = report["configuration"]
    require(config["receiveBufferBytes"] == 8192, "Pressure controls must request the declared8192byte receive buffer")
    address = urlsplit(config["baseUrl"])
    sockets = report["sockets"]
    require(sockets and len(sockets) <= len(rows), "Pressure socket observations missing/excessive")
    by_id = {row["id"]: row for row in rows}
    initial_ids = set()
    for socket in sockets:
        require(socket["requestedReceiveBufferBytes"] == 8192 and integer(socket["actualReceiveBufferBytes"]) > 0 and socket["noDelay"] is True, "Pressure socket setup was not observed")
        require(socket["host"] == address.hostname and socket["port"] == address.port, "Pressure socket is not the measured Gateway connection")
        identity = socket["initialRequestId"]
        require(identity in by_id and identity not in initial_ids, "Pressure socket request correlation missing/duplicated")
        initial_ids.add(identity)
        require(by_id[identity]["requestStartTicks"] <= socket["connectedTicks"] <= by_id[identity]["headersTicks"], "Pressure connection timestamps inconsistent")
    for row in rows:
        if not row["validated"]:
            continue
        provider = row["provider"]
        require(row["firstBodyReadTicks"] <= row["lastBodyReadTicks"] <= row["validatedEndTicks"] and provider["lastFlushEndTicks"] >= provider["firstContentWriteBeginTicks"] > 0, "Pressure body-read/provider-write span invalid")
        derived = (integer(provider["lastFlushEndTicks"]) - integer(provider["firstContentWriteBeginTicks"])) * 1000.0 / integer(report["stopwatchFrequency"])
        require(math.isclose(number(row["providerFirstContentToLastFlushMilliseconds"]), derived, rel_tol=1e-12, abs_tol=1e-9), "Pressure reported provider span differs from monotonic timestamps")


def validate_provider_instances(directory, manifest, configuration):
    directory = Path(directory)
    if not any("providerInstanceId" in entry for entry in manifest):
        return {"freshPerCohortRun": False, "scope": "Historical collection used its declared shared provider process", "instances": []}
    require(all("providerInstanceId" in entry and "providerProcessId" in entry for entry in manifest), "Provider identity contract is mixed/incomplete")
    instances = {}
    for entry in manifest:
        prefix = host_prefix(configuration, entry["cohort"], entry["run"])
        require(entry["providerInstanceId"] == prefix, "Provider/artifact scope correlation differs")
        owner = (entry["cohort"], entry["run"], integer(entry["providerProcessId"]))
        require(owner[2] > 0 and owner[2] != entry["gatewayProcessId"], "Provider must be a separate actual process")
        require(prefix not in instances or instances[prefix] == owner, "Provider identity changed within one cohort/run")
        instances[prefix] = owner
    output = []
    for prefix, (cohort, run, process) in sorted(instances.items()):
        path = directory / (prefix + "-provider.exit.json")
        record = read(path)
        require(record["processId"] == process and record["exitCode"] == 0 and record["graceful"] is True, "Fresh provider did not exit gracefully with its declared process identity")
        output.append({"providerInstanceId": prefix, "cohort": cohort, "run": run, "processId": process, "exitSha256": sha256(path)})
    return {"freshPerCohortRun": True, "scope": "Fixed frozen provider/client binary and.NET10runtime; one owned provider process per cohort/run", "instances": output}


def validate_drain_report(report, expected_logs, database, assembly_hash, runtime):
    require(report["schemaVersion"] == 1 and report["passed"] is True and report["scope"] == "known persisted spend drained; not provider evidence completeness", "Known-spend drain did not pass its bounded scope")
    require(report["expectedLogs"] == expected_logs, "Drain cumulative expected log count differs")
    require(report["assemblySha256"].lower() == assembly_hash.lower(), "Drain assembly differs from execution binding")
    require(report["runtime"] == ".NET " + runtime and report["sourceSdk"] == "10.0.404", "Drain fixed SDK/runtime differs")
    require(report["messageTypes"] == ["ConduitLLM.Core.Events.SpendUpdateRequested", "ConduitLLM.Configuration.Events.BatchSpendFlushRequestedEvent"], "Known-spend message inventory changed")
    require(len(report["queryHash"]) == 64 and report["polls"] and report["finalState"] == report["polls"][-1], "Drain final observation/query identity missing")
    poll = report["finalState"]
    require(poll["database"] == database and poll["readOnly"] == "on" and poll["keyCount"] == poll["groupCount"] == 1, "Drain fixture/read-only identity differs")
    require(poll["logCount"] == expected_logs and poll["unbilledOrNonpositiveLogs"] == poll["deletedOrUnsupportedLedger"] == 0 and not poll["failures"], "Drain final log/ledger state is incomplete")
    require(money(poll["logCost"]) == money(poll["ledgerDebit"]) == money(poll["spent"]) and money(poll["balance"]) == money(poll["credits"]) - money(poll["spent"]), "Drain reported success despite exact monetary mismatch")
    required_tables = {("wolverine_conduit_gateway", "wolverine_incoming_envelopes"), ("wolverine_conduit_gateway", "wolverine_outgoing_envelopes"), ("wolverine_queues", "wolverine_queue_spend_update_events"), ("wolverine_queues", "wolverine_queue_spend_update_events_scheduled")}
    tables = {(row["schema"], row["table"]) for row in poll["queues"]}
    require(len(tables) == len(poll["queues"]) and required_tables <= tables, "Drain message table inventory is incomplete/duplicated")
    require(all(row["activeKnownSpendRows"] == 0 and row["retainedHandledKnownSpendRows"] >= 0 and (row["table"] == "wolverine_incoming_envelopes" or row["retainedHandledKnownSpendRows"] == 0) for row in poll["queues"]), "Drain left active/dead-letter known spend")
    times = [number(row["elapsedMilliseconds"]) for row in report["polls"]]
    require(times == sorted(times) and times[0] >= 0 and times[-1] <= number(report["elapsedMs"]), "Drain polling timestamps inconsistent")


def validate_drains(directory, manifest, configuration, client_runtime="10.0.15"):
    directory = Path(directory)
    if not configuration.get("drainProbe"):
        return {"qualified": False, "scope": "Historical collection has no live-host drain receipts; not retroactively qualified", "receipts": []}
    bindings = read(directory / "bindings.json")
    bound = [row for row in bindings["files"] if row["path"] == configuration["drainProbe"]]
    require(len(bound) == 1, "Drain assembly binding missing/duplicated")
    evidence, query_hashes = [], set()
    for cohort in configuration["cohorts"]:
        cumulative = configuration.get("initialLogCounts", {}).get(cohort["id"], 0)
        require(integer(cumulative) >= 0, "Negative initial request log count")
        database = unquote(urlsplit(cohort["databaseUrl"]).path.lstrip("/"))
        require(database.startswith("phase2_"), "Drain cohort database must be an owned URI")
        for run in range(1, configuration["runs"] + 1):
            rows = [row for row in manifest if row["cohort"] == cohort["id"] and row["run"] == run]
            require(rows, "Missing cohort/run for cumulative drain")
            cumulative += sum(integer(row["warmups"]) + (0 if row.get("knownProductionFinding") else integer(row["requests"])) for row in rows)
            prefix = host_prefix(configuration, cohort["id"], run)
            path = directory / (prefix + "-financial-drain.json")
            report = read(path)
            validate_drain_report(report, cumulative, database, bound[0]["sha256"], client_runtime)
            require(report["architecture"] == configuration["architecture"], "Drain architecture differs")
            query_hashes.add(report["queryHash"])
            exit_record = read(directory / (prefix + "-financial-drain.exit.json"))
            require(exit_record["exitCode"] == 0 and exit_record["graceful"] is False, "Drain subprocess failed")
            shutdown = read(directory / (prefix + "-shutdown.json"))
            host_exit = read(directory / (prefix + "-gateway.exit.json"))
            require(shutdown["cohort"] == cohort["id"] and shutdown["run"] == run and shutdown["exitCode"] == host_exit["exitCode"] == 0 and host_exit["graceful"] is True, "Gateway shutdown did not complete gracefully after drain")
            require(all(row["gatewayProcessId"] == host_exit["processId"] for row in rows), "Drained Gateway/shutdown process differs")
            evidence.append({"file": path.name, "sha256": sha256(path), "cohort": cohort["id"], "run": run, "expectedCumulativeLogs": cumulative, "scope": report["scope"]})
    require(len(query_hashes) == 1, "Drain query changed within one profile")
    return {"qualified": True, "scope": "Known persisted spend drained before each host shutdown; separate final SQL/Redis and provider-evidence reconciliation required", "receipts": evidence}


def report_rows(report, expected_count=None, allow_legacy=False):
    require(report.get("schemaVersion") == 1, "Unknown frozen client schema")
    config = report["configuration"]
    rows = report["warmup"]["requests"] + report["measured"]["requests"]
    require(len(rows) == (expected_count if expected_count is not None else config["requests"] + config["warmupRequests"]), "Client request count mismatch")
    require(len({row["id"] for row in rows}) == len(rows), "Duplicate client request IDs")
    observations = report["providerObservations"]["observations"]
    require(len({row["id"] for row in observations}) == len(observations), "Duplicate provider observations")
    require({row["id"] for row in observations} == {row["id"] for row in rows}, "Provider/client observation set differs")
    by_id = {row["id"]: row for row in observations}
    require(report["stopwatchFrequency"] == report["providerObservations"]["stopwatchFrequency"], "Provider/client clock frequencies differ")
    for row in rows:
        provider = row.get("provider")
        require(provider == by_id[row["id"]], "Embedded provider observation differs from retained report")
        require(number(row["validatedEndTicks"]) >= number(row["requestStartTicks"]), "Invalid client time bounds")
        if not row["validated"]:
            continue
        require(row["status"] == 200 and not row.get("error"), "Validated response has HTTP/protocol failure")
        require(row["firstContentReadTicks"] > 0 and provider["firstContentWriteBeginTicks"] > 0, "Missing validated content evidence")
        cancel = config["cancel"]
        if cancel == "none":
            require(provider["completed"] and provider["firstContentFlushEndTicks"] > 0, "Completed response lacks completed provider evidence")
            if config["endpoint"] == "audio":
                require(provider["uploadBytes"] == config["inputBytes"] and len(provider["uploadSha256"] or "") == 64, "Upload byte/hash proof missing")
            else:
                require(provider["usageWriteBeginTicks"] > 0, "Completed response lacks provider usage")
        else:
            require(row["cancelInitiatedTicks"] > 0 and provider["cancellationTicks"] > 0 and not provider["completed"] and provider["doneWriteBeginTicks"] == 0, "Cancellation/disconnect boundary not proved")
            if cancel == "before-usage":
                require(provider["usageWriteBeginTicks"] == 0 and row["usageEvents"] == 0, "Before-usage cancellation crossed usage")
            else:
                require(cancel == "after-usage" and provider["usageFlushEndTicks"] > 0 and row["usageEvents"] == 1, "After-usage cancellation lacks usage evidence")
        if cancel == "none" and config["endpoint"].endswith("sse"):
            require(row["contentChunks"] == config["chunks"] and row["usageEvents"] == 1, "Validated SSE content/usage counters differ")
            require(row["events"] >= config["chunks"], "Validated SSE event count too small")
            if config["toolCalls"]:
                require(row["toolArgumentFragments"] == 3, "Fragmented tool argument count differs")
    require(report["passed"] == all(row["validated"] for row in rows), "Client passed flag disagrees with rows")
    if config.get("receiveBufferBytes", 0):
        validate_pressure_report(report, rows)
    return rows


def load_clients(directory, cohort):
    directory = Path(directory)
    manifest = read(directory / "manifest.json")
    execution = read(directory / "execution.json")
    require(execution["executionCompleted"] and not execution["failure"] and not execution["cleanupFailures"], "Financial profile execution/cleanup incomplete")
    require(len(manifest) == execution["rows"] == execution["expectedRows"], "Financial profile inventory incomplete")
    configuration_path = directory / "configuration.source.json"
    if configuration_path.exists():
        require(sha256(configuration_path) == read(directory / "bindings.json")["configurationSha256"], "Copied financial profile configuration differs from execution binding")
        validate_provider_instances(directory, manifest, read(configuration_path))
        validate_drains(directory, manifest, read(configuration_path))
    clients, sources = [], []
    seen, instances = set(), {}
    for entry in manifest:
        if entry["cohort"] != cohort:
            continue
        files = [(entry["result"], entry["requests"], "measured")]
        if entry["warmups"]:
            files.append((entry["caseId"] + "-warm.stdout.log", entry["warmups"], "external-warmup"))
        for filename, count, phase in files:
            path = directory / filename
            report = read(path)
            rows = report_rows(report, count, allow_legacy=bool(entry.get("knownProductionFinding")))
            require(report["configuration"]["caseId"] == entry.get("logicalCaseId", entry["caseId"]) + ("-warm" if phase == "external-warmup" else ""), "Financial manifest/report case correlation differs")
            require(not report["warmup"]["requests"] and report["configuration"]["warmupRequests"] == 0 and report["configuration"]["requests"] == count, "Financial profile must use separate warmup invocations")
            if phase == "external-warmup":
                require(report["passed"], "Financial profile external warmup failed")
            sources.append({"file": filename, "sha256": sha256(path), "phase": phase, "count": len(rows)})
            register_scoped_ids(seen, instances, entry, rows)
            for row in rows:
                clients.append({"case": entry["case"], "caseId": entry["caseId"], "phase": phase,
                                "endpoint": report["configuration"]["endpoint"], "cancel": report["configuration"]["cancel"],
                                "sourceSha": entry["sourceSha"], "knownProductionFinding": bool(entry.get("knownProductionFinding")),
                                "profileDirectoryScope": str(directory.resolve()),
                                "providerInstanceId": entry.get("providerInstanceId", "historical-profile-provider"),
                                "row": row})
    require(clients, "No client evidence for selected cohort")
    return clients, sources
