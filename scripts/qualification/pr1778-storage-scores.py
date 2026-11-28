"""Score the two fixture placements independently using the registered evaluator."""
import itertools
import json
import sys
from collections import defaultdict
from pathlib import Path
from summarize import compare_runs


def load(path):
    return json.loads(path.read_text())


root = Path(sys.argv[1])
report = load(root / "summary.json")
state = load(root / "qualification-state.json")
assert report["collectionValidated"] and report["allWorkloadsPassed"]
assert load(root / "published-input-integrity.json")["passed"]
assert len(state["financial"]) == 3
assert all(r["financialInvariantsPassed"] and r["recordedSpendConservationPassed"] and r["fullyReconciled"]
           for r in state["financial"])
manifest = load(root / "measure/manifest.json")
assert len(manifest) == 180
arms = ("legacy", "fixed", "candidate")
catalog = ["audio-1MiB-c1", "audio-8MiB-c1", "audio-1MiB-c4", "audio-8MiB-c4", "audio-1MiB-q4"]
groups = defaultdict(dict)
placement = {}
for row in manifest:
    run, arm = row["run"], row["cohort"]
    groups[run].setdefault(arm, []).append(row["case"])
    trace = load(root / "measure" / (row["caseId"] + "-io.json"))
    assert int(trace["pids"][0]) == row["gatewayProcessId"] and int(trace["pids"][1]) == row["providerProcessId"]
    assert placement.setdefault(run, trace["mode"]) == trace["mode"]
    assert len(trace["samples"]) >= 2
for run in groups:
    assert all(v == catalog for v in groups[run].values())
for r in range(1, 13, 2):
    assert list(groups[r]) == list(groups[r + 1]) and placement[r] != placement[r + 1]
scores = []
misses = []
for mode in ("normal", "memory"):
    runs = [r for r, m in placement.items() if m == mode]
    assert len(runs) == 6
    assert {tuple(groups[r]) for r in runs} == set(itertools.permutations(arms))
    assert len(report["comparisons"]) == 20
    for c in report["comparisons"]:
        rows = [[r for r in report["runs"] if r["case"] == c["case"] and r["run"] in runs and r["cohort"] == arm]
                for arm in (c["baseline"], c["candidate"])]
        assert all(len(part) == 6 for part in rows)
        m = compare_runs(*rows)
        scores.append(dict(mode=mode, case=c["case"], baseline=c["baseline"], candidate=c["candidate"], **m))
        for key, metric in m["metrics"].items():
            if metric["status"] == "outside-working-threshold":
                misses.append([mode, c["case"], c["baseline"], c["candidate"], key, metric["medianPairedRatio"]])
(root / "storage-comparisons.json").write_text(json.dumps({"placements": placement, "scores": scores, "misses": misses}, indent=2))
for miss in misses:
    print(*miss)
print(f"Scored {len(scores)} comparisons and found {len(misses)} budget misses")
sys.exit(bool(misses))

