import unittest
from unittest.mock import patch
import summarize as subject


class MeasurementPolicyTests(unittest.TestCase):
    def rows(self, metric, value):
        return [{"run": run, "metrics": {metric: value}} for run in range(1, 6)]

    def test_latency_uses_absolute_allowance_and_ratio(self):
        definitions = {"p95Milliseconds": ("p95MaximumRatio", "maximum")}
        boundary = subject.paired(self.rows("p95Milliseconds", 5), self.rows("p95Milliseconds", 7), definitions, 100)
        over = subject.paired(self.rows("p95Milliseconds", 5), self.rows("p95Milliseconds", 7.01), definitions, 100)
        self.assertEqual("within-working-threshold", boundary["metrics"]["p95Milliseconds"]["status"])
        self.assertEqual("outside-working-threshold", over["metrics"]["p95Milliseconds"]["status"])

    def test_p99_small_sample_and_smoke_never_accept(self):
        definitions = {"p99Milliseconds": ("p99MaximumRatio", "maximum")}
        result = subject.paired(self.rows("p99Milliseconds", 5), self.rows("p99Milliseconds", 5), definitions, 999)
        smoke = subject.paired(self.rows("p99Milliseconds", 5), self.rows("p99Milliseconds", 5), definitions, 1000, True)
        self.assertEqual("insufficient-evidence", result["metrics"]["p99Milliseconds"]["status"])
        self.assertEqual("insufficient-evidence", smoke["metrics"]["p99Milliseconds"]["status"])
        self.assertFalse(smoke["performanceAcceptanceGranted"])

    def test_counter_decrease_invalidates_positive_net_delta(self):
        snapshot = {"timestamp": 0, "cpuMilliseconds": 10, "allocatedBytes": 100,
                    "gcPauseMilliseconds": 0, "gen0": 0, "gen1": 0, "gen2": 0}
        end = dict(snapshot, timestamp=3, cpuMilliseconds=30, allocatedBytes=120)
        report = {"processId": 42, "framework": ".NET 10.0.15", "architecture": "X64",
                  "start": snapshot, "end": end, "allocationCounterSamples": [
                      {"timestamp": 0, "allocatedBytes": 100}, {"timestamp": 1, "allocatedBytes": 150},
                      {"timestamp": 3, "allocatedBytes": 120}], "sampledPeakWorkingSetBytes": 1000,
                  "periodicSamples": 1, "serverGC": True, "gcConfigurationVariables": [],
                  "scope": "whole process", "allocationCounterSource": "precise raw"}
        with patch.object(subject, "read", return_value=report):
            result = subject.microprocess("micro.json", 42, "10.0.15", "X64", 10)
        self.assertEqual(20, result["rawAllocatedDelta"])
        self.assertEqual(1, len(result["allocationCounterDecreases"]))
        self.assertIsNone(result["metrics"]["wholeInvocationAllocatedBytesPerAllOperation"])
        self.assertEqual(2, result["metrics"]["wholeInvocationCpuMillisecondsPerAllOperation"])

    def test_admin_normalizer_includes_internal_warmups(self):
        before = {"processId": 42, "runtimeVersion": "11.0.0", "architecture": "X64",
                  "totalCpuMilliseconds": 100, "totalAllocatedBytes": 1000,
                  "gen0Collections": 0, "gen1Collections": 0, "gen2Collections": 0}
        after = dict(before, totalCpuMilliseconds=210, totalAllocatedBytes=111000)
        report = {"window": {"start": before, "end": after, "samplingIntervalMs": 100,
                             "sampledPeakWorkingSetBytes": 5000, "deltas": {"cpuMilliseconds": 110,
                             "allocatedBytes": 110000, "gen0Collections": 0, "gen1Collections": 0,
                             "gen2Collections": 0, "gcPauseMilliseconds": 0}}}
        with patch.object(subject, "read", return_value=report):
            metrics, issues = subject.observer_window("admin.json", 42, "11.0.0", "X64", 1100)
        self.assertEqual(.1, metrics["serverWindowCpuMillisecondsPerValidated"])
        self.assertEqual(100, metrics["serverWindowAllocatedBytesPerValidated"])
        self.assertEqual([], issues)

    def test_unavailable_allocation_disables_all_paired_ratios(self):
        metric = "serverWindowAllocatedBytesPerValidated"
        before, after = self.rows(metric, 100), self.rows(metric, 50)
        before[0]["metrics"][metric] = None
        result = subject.paired(before, after, {metric: ("allocationMaximumRatio", "maximum")})["metrics"][metric]
        self.assertEqual("unavailable", result["status"])
        self.assertEqual(0, result["pairs"])
        self.assertIsNone(result["medianPairedRatio"])

    def test_known_counter_scope_rejects_positive_allocation_ratios_too(self):
        start = {"timestamp": 0, "cpuMilliseconds": 1, "allocatedBytes": 100,
                 "gcPauseMilliseconds": 0, "gen0": 0, "gen1": 0, "gen2": 0}
        end = dict(start, timestamp=1, allocatedBytes=200)
        report = {"processId": 42, "framework": ".NET 10.0.15", "architecture": "X64",
                  "serverGC": True, "gcConfigurationVariables": [], "start": start, "end": end,
                  "allocationCounterSamples": [start, end], "sampledPeakWorkingSetBytes": 1000,
                  "periodicSamples": 1, "scope": "whole process", "allocationCounterSource": "precise raw"}
        with patch.object(subject, "read", return_value=report):
            result = subject.microprocess("micro.json", 42, "10.0.15", "X64", 10)
        self.assertEqual(100, result["rawAllocatedDelta"])
        self.assertIsNone(result["metrics"]["wholeInvocationAllocatedBytesPerAllOperation"])
        report["serverGC"] = "false"
        with patch.object(subject, "read", return_value=report), self.assertRaises(ValueError):
            subject.microprocess("micro.json", 42, "10.0.15", "X64", 10)
        report["serverGC"] = True
        report["framework"] = ".NET 11.0.0"
        end["allocatedBytes"] = 50
        with patch.object(subject, "read", return_value=report), self.assertRaises(ValueError):
            subject.microprocess("micro.json", 42, "11.0.0", "X64", 10)


if __name__ == "__main__":
    unittest.main()
