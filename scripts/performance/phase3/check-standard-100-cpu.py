"""Classify the focused CPU comparison after validation of the full mixed history."""
import argparse
from hashlib import sha256
import json
import math
from pathlib import Path

CASE = "standard-100"
METRIC = "serverWindowCpuMillisecondsPerValidated"
ORDERS = (
    ("fixed", "legacy", "candidate"),
    ("fixed", "candidate", "legacy"),
    ("legacy", "candidate", "fixed"),
    ("legacy", "fixed", "candidate"),
    ("candidate", "fixed", "legacy"),
    ("candidate", "legacy", "fixed"),
)
CONTROLS = (("fixed", "legacy"), ("legacy", "fixed"))
CANDIDATES = (("fixed", "candidate"), ("legacy", "candidate"))


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify_history(evidence):
    directory = evidence / "measure"
    cases = read(directory / "catalog.source.json")["cases"]
    ids = [case["id"] for case in cases]
    target = next(case for case in cases if case["id"] == CASE)
    require(len(ids) == len(set(ids)) == 23, "Expected the full primary workload")
    require(target["endpoint"] == "standard-sse" and target["chunks"] == 100
            and target["requests"] == 60 and target["warmupRequests"] == 6
            and target["concurrency"] == 4 and target["chunkCharacters"] == 32
            and target["gapMilliseconds"] == 0,
            "The standard-100 workload or sample size changed")

    expected = []
    history = []
    for index, order in enumerate(ORDERS):
        rotation = ids[index:] + ids[:index]
        history.append(rotation[:rotation.index(CASE)])
        for arm in order:
            expected.extend((index + 1, arm, case) for case in rotation)

    manifest_path = directory / "manifest.json"
    manifest = read(manifest_path)
    observed = [(row["run"], row["cohort"], row["case"]) for row in manifest]
    require(observed == expected, "Workload history or cohort order differs")
    return manifest_path, manifest, history


def classify(evidence):
    config = read(evidence / "configuration.json")
    state = read(evidence / "qualification-state.json")
    require(config["profile"] == "phase0" and config["interleavedControl"]
            and config["runs"] == len(ORDERS) and config["architecture"] == "X64",
            "Expected the native amd64 mixed three-arm configuration")
    require(state["primaryCollectionValidated"] and all(
        row["financialInvariantsPassed"] and row["recordedSpendConservationPassed"]
        for row in state["financial"]
    ) and {row["cohort"] for row in state["financial"]} == set(ORDERS[0]),
            "Incomplete protocol or financial evidence")

    manifest_path, manifest, history = verify_history(evidence)
    summary = read(evidence / "summary.json")
    require(summary["collectionValidated"] and summary["allWorkloadsPassed"]
            and summary["inputEvidence"]["manifest.json"] == sha256(manifest_path.read_bytes()).hexdigest(),
            "The summary did not validate this workload history")

    by_pair = {}
    for row in summary["comparisons"]:
        if row["case"] != CASE:
            continue
        pair = (row["baseline"], row["candidate"])
        require(pair not in by_pair, "Duplicate target comparison")
        metric = row["metrics"][METRIC]
        ratio = metric["medianPairedRatio"]
        require(metric["pairs"] == len(ORDERS) and math.isfinite(ratio) and ratio > 0
                and metric["workingThreshold"] == 1.05
                and metric["status"] == ("outside-working-threshold" if ratio > 1.05
                                         else "within-working-threshold"),
                "Incomplete or inconsistent CPU comparison")
        by_pair[pair] = ratio
    require(set(by_pair) == set(CONTROLS + CANDIDATES), "Incomplete comparison inventory")

    if any(by_pair[pair] > 1.05 for pair in CONTROLS):
        verdict = "inconclusive-control"
    else:
        misses = sum(by_pair[pair] > 1.05 for pair in CANDIDATES)
        verdict = ("within-budget", "reference-dependent", "overage-reproduced")[misses]

    return {
        "case": CASE,
        "metric": METRIC,
        "budgetMaximumRatio": 1.05,
        "classification": verdict,
        "baselineRevision": state["baselineRevision"],
        "candidateRevision": state["candidateRevision"],
        "validatedWindows": len(manifest),
        "workloadsBeforeTargetByRepetition": history,
        "ratios": {f"{before}->{after}": by_pair[(before, after)]
                   for before, after in CONTROLS + CANDIDATES},
        "cpuMillisecondsPerRequest": [
            {"run": row["run"], "arm": row["cohort"], "cpu": row["metrics"][METRIC]}
            for row in summary["runs"] if row["case"] == CASE
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    args = parser.parse_args()
    report = json.dumps(classify(args.evidence), indent=2)
    (args.evidence / "standard-100-cpu.json").write_text(report + "\n", encoding="utf-8")
    print(report)


if __name__ == "__main__":
    main()
