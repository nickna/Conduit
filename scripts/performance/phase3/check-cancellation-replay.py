"""Evaluate before-usage cancellation after validating the mixed workload replay."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "qualification"))
from summarize import compare_runs

CASE = "cancel-before-usage"
CPU = "serverWindowCpuMillisecondsPerValidated"
ORIGINAL_CATALOG_SHA256 = "9888c5b527825241a835987b8d181f6bfda0329c6ebaf619e2a88e69338c2c84"
FOCUS = (3, 4, 5, 6)
PRIOR_ORDERS = (
    "fixed,legacy,candidate", "fixed,candidate,legacy", "legacy,candidate,fixed",
    "legacy,fixed,candidate", "candidate,fixed,legacy", "candidate,legacy,fixed",
)
CONTROLS = (("fixed", "legacy"), ("legacy", "fixed"))
CANDIDATES = (("fixed", "candidate"), ("legacy", "candidate"))


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def evaluate(evidence):
    config = read(evidence / "configuration.json")
    state = read(evidence / "qualification-state.json")
    summary = read(evidence / "summary.json")
    schedule = config["cancelBeforeUsageReplay"]
    require(not schedule.get("perf"), "An instrumented run cannot qualify the CPU comparison")
    orders = schedule["armOrders"]
    require(tuple(schedule["focusRuns"]) == FOCUS and set(orders) == set(PRIOR_ORDERS)
            and len(orders) == 6, "Incomplete balanced replay schedule")
    require(all(orders[i - 1].split(",").index("candidate")
                != PRIOR_ORDERS[i - 1].split(",").index("candidate") for i in FOCUS),
            "Candidate slot was not varied across the target workload histories")
    require(config["architecture"] == "X64" and config["profile"] == "phase0"
            and config["runs"] == 6 and config["interleavedControl"] and config["cpuPlacement"],
            "Expected the calibrated mixed amd64 configuration")
    require(summary["collectionValidated"] and summary["allWorkloadsPassed"]
            and state["primaryCollectionValidated"] and
            {row["cohort"] for row in state["financial"]
             if row["financialInvariantsPassed"] and row["recordedSpendConservationPassed"]}
            == {"fixed", "legacy", "candidate"}, "Protocol or financial evidence is incomplete")

    measure = evidence / "measure"
    catalog_path = measure / "catalog.source.json"
    digest = hashlib.sha256(catalog_path.read_bytes()).hexdigest()
    require(digest == ORIGINAL_CATALOG_SHA256, "Mixed workload catalog differs from both original runs")
    catalog_ids = [row["id"] for row in read(catalog_path)["cases"]]
    manifest_path = measure / "manifest.json"
    manifest = read(manifest_path)
    require(summary["inputEvidence"]["manifest.json"] == hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
            "Validated summary manifest binding differs")
    expected = [
        (index + 1, arm, case)
        for index, order in enumerate(orders)
        for arm in order.split(",")
        for case in catalog_ids[index:] + catalog_ids[:index]
    ]
    require(expected == [(row["run"], row["cohort"], row["case"]) for row in manifest],
            "The actual mixed workload history or arm order differs")

    rows = [row for row in summary["runs"] if row["case"] == CASE]
    focus_rows = [row for row in rows if row["run"] in FOCUS]
    require(len(rows) == 18 and len(focus_rows) == 12
            and all(row["validated"] == 10 and row["kind"] == "cancellation-scenario" for row in rows),
            "Cancellation scope or sample size differs")
    stats, focus_stats = {}, {}
    for before, after in CONTROLS + CANDIDATES:
        for sample, expected_pairs, output in ((rows, 6, stats), (focus_rows, 4, focus_stats)):
            comparison = compare_runs([row for row in sample if row["cohort"] == before],
                                      [row for row in sample if row["cohort"] == after])
            metric = comparison["metrics"][CPU]
            require(metric["pairs"] == expected_pairs and metric["workingThreshold"] == 1.05,
                    "Paired evidence or retained CPU budget differs")
            output[(before, after)] = metric
    require(all(metric["status"] in ("within-working-threshold", "outside-working-threshold")
                for metric in stats.values()), "The full replay did not qualify for CPU budget evaluation")
    if any(stats[pair]["status"] == "outside-working-threshold"
           or focus_stats[pair]["medianPairedRatio"] > 1.05 for pair in CONTROLS):
        result = "inconclusive-control"
    elif all(stats[pair]["status"] == "outside-working-threshold"
             and focus_stats[pair]["medianPairedRatio"] > 1.05 for pair in CANDIDATES):
        result = "overage-reproduced"
    elif all(stats[pair]["status"] == "within-working-threshold"
             and focus_stats[pair]["medianPairedRatio"] <= 1.05 for pair in CANDIDATES):
        result = "within-budget"
    else:
        result = "reference-or-repetition-dependent"

    return {
        "classification": result,
        "scope": "Budget evaluation uses all six repetitions. Repetitions 3–6 are retained as the descriptive target subset.",
        "candidateRevision": state["candidateRevision"],
        "baselineRevision": state["baselineRevision"],
        "validatedWindows": len(manifest),
        "focusRuns": FOCUS,
        "armOrders": orders,
        "cpuPlacement": config["cpuPlacement"],
        "cpuComparisons": {f"{left}->{right}": metric for (left, right), metric in stats.items()},
        "focusCpuComparisons": {f"{left}->{right}": metric for (left, right), metric in focus_stats.items()},
        "cpuMillisecondsPerValidatedCancellation": [
            {"run": row["run"], "arm": row["cohort"], "cpuMs": row["metrics"][CPU],
             "disconnectP95Ms": row["metrics"]["cancellationToProviderObservedP95Milliseconds"]}
            for row in rows
        ],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    path = parser.parse_args().evidence
    result = json.dumps(evaluate(path), indent=2)
    (path / "cancellation-cpu.json").write_text(result + "\n", encoding="utf-8")
    print(result)
