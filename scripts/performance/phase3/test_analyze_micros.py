import copy
import hashlib
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

import analyze_micros as subject


BASE = "b1d49922b8dfdc6719c6d46d123f2eb5a265dcca"
CANDIDATE = "c" * 40


def wire(text):
    return hashlib.sha256(text.encode()).hexdigest()


def reports(bound, arm, pid):
    shared = {"schemaVersion": 1, "architecture": "X64", "runtime": "11.0.0", "serverGc": False,
              "gcConfiguration": {"ServerGC": "False", "ConcurrentGC": "True", "GCRegionRange": str(pid)}}
    collector = {**shared, "framework": ".NET 11.0.0", "isServerGc": False, "passed": True,
                 "declaredSourceRevision": BASE if arm == "baseline" else CANDIDATE,
                 "productionAssemblySha256": bound["ConduitLLM.Core.dll"]["sha256"],
                 "harnessAssemblySha256": bound["ConduitLLM.Benchmarks.dll"]["sha256"],
                 "currentCallerReader": "GetMetrics.TimeToFirstTokenMs" if arm == "baseline" else "HasRecordedFirstToken",
                 "innerSamplesPerCase": 5, "warmupStreamsPerCase": 1, "stopwatchFrequency": 10_000_000, "processId": pid, "rows": []}
    cases = [(n, m) for n in (10, 100, 1000, 10000) for m in ("snapshot-each-content-chunk", "current-caller-first-token-check", "record-plus-final-snapshot-control")]
    cases.append((100000, "record-plus-final-snapshot-control"))
    for n, mode in cases:
        collector["rows"].append({"contentChunks": n, "mode": mode, "samples": [
            {"startedTicks": 100, "endedTicks": 10100, "microseconds": 1000.0, "contentChunksRecorded": n,
             "managedThreadId": 1, "averageInterTokenLatencyMilliseconds": 0.0,
             "processCpuMilliseconds": 1.0, "threadLocalAllocatedBytes": 256} for _ in range(5)]})
    writer = {**shared, "rows": [], **{key: {"sha256": bound[name + ".dll"]["sha256"],
               "path": f"/native/micros/work/published/{arm}/{name}.dll"} for name, key in (("ConduitLLM.Core", "core"), ("ConduitLLM.Gateway", "gateway"))}}
    for enhanced in (False, True):
        for characters in (32, 1024):
            for frames in (10, 100, 1000, 10000):
                for sample in range(1, 6):
                    h = wire(str((enhanced, characters, frames)))
                    writer["rows"].append({"enhanced": enhanced, "characters": characters, "frames": frames, "run": sample,
                        "observation": {"validated": True, "wireSha256": h, "expectedWireSha256": h,
                            "EventsWritten": frames + (2 if enhanced else 1), "Flushes": frames + (3 if enhanced else 2),
                            "Bytes": frames * characters + 32, "Writes": frames, "sameThread": True,
                            "currentThreadAllocatedBytes": 1000, "processAllocatedBytes": 1100,
                            "elapsedMilliseconds": 1.0, "processCpuMilliseconds": 1.0}})
    provider = {**shared, "processId": pid, "innerSamples": 5, "freshProcessesInThisReport": 1,
                "parserControlsDeployed": False, "assemblies": [{"name": n, "sha256": bound[n + ".dll"]["sha256"]}
                 for n in ("ConduitLLM.Core", "ConduitLLM.Providers", "ConduitLLM.Benchmarks")], "rows": []}
    scenarios = [("groq", p, v) for p in ("content", "hosted-usage") for v in ("groq-public-options", "groq-client-call-path")]
    scenarios += [("parser-controls", "content", v) for v in ("dom-raw-text-model", "dom-model-control", "direct-typed-control")]
    for suite, payload, variant in scenarios:
        for frames in (10, 100, 1000, 10000):
            for sample in range(1, 6):
                provider["rows"].append({"suite": suite, "payload": payload, "variant": variant, "frames": frames,
                    "sample": sample, "fixtureSha256": wire(payload), "observation": {"validated": True,
                        "checksum": frames * 20, "expectedChecksum": frames * 20, "rawProcessAllocatedStartBytes": 100,
                        "rawProcessAllocatedEndBytes": 1200, "rawProcessAllocatedDeltaBytes": 1100,
                        "processAllocationMonotonic": True, "sameThread": True, "currentThreadAllocatedBytes": 1000,
                        "collections": [0, 0, 0], "gcPauseMilliseconds": 0.0, "wireSha256": wire(payload), "hostedTools": None,
                        "elapsedMilliseconds": 1.0, "processCpuMilliseconds": 1.0}})
    return {"collector": collector, "writer": writer, "provider": provider}


class MicroEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.bound = {name + ".dll": {"sha256": wire(name)} for name in
                      ("ConduitLLM.Core", "ConduitLLM.Gateway", "ConduitLLM.Providers", "ConduitLLM.Benchmarks")}
        self.reports = reports(self.bound, "baseline", 123)
        self.entry = {"sourceSha": BASE, "processId": 123}

    def check(self, tool, report=None):
        return subject.report_rows(tool, report or self.reports[tool], self.entry, "X64", self.bound)

    def test_complete_real_producer_shapes(self):
        self.assertEqual([len(self.check(t)[0]) for t in ("collector", "writer", "provider")], [65, 80, 140])

    def test_runtime_and_architecture_rejected(self):
        for tool in self.reports:
            for field, value in (("runtime", "10.0.15"), ("architecture", "Arm64")):
                with self.subTest(tool=tool, field=field):
                    item = copy.deepcopy(self.reports[tool]); item[field] = value
                    with self.assertRaises(subject.EvidenceError): self.check(tool, item)

    def test_gc_mode_and_missing_metadata_rejected(self):
        for tool in self.reports:
            for value in ({}, {"ServerGC": "True"}):
                with self.subTest(tool=tool, value=value):
                    item = copy.deepcopy(self.reports[tool]); item["gcConfiguration"] = value
                    with self.assertRaises(subject.EvidenceError): self.check(tool, item)

    def test_gc_region_addresses_vary_but_policy_drift_does_not(self):
        a = {"ServerGC": "False", "GCRetainVM": "False", "GCRegionRange": "0x123", "HeapBudget": "100"}
        b = {**a, "GCRegionRange": "0x456", "HeapBudget": "200"}
        self.assertEqual(subject.gc_policy(False, a), subject.gc_policy(False, b))
        b["GCRetainVM"] = "True"
        self.assertNotEqual(subject.gc_policy(False, a), subject.gc_policy(False, b))
        self.assertEqual(a["GCRegionRange"], "0x123")

    def test_collector_source_identity_rejected(self):
        self.reports["collector"]["declaredSourceRevision"] = CANDIDATE
        with self.assertRaises(subject.EvidenceError): self.check("collector")

    def test_loaded_assembly_binding_rejected(self):
        self.reports["writer"]["core"]["sha256"] = "0" * 64
        with self.assertRaises(subject.EvidenceError): self.check("writer")

    def test_collector_ticks_and_counts_rejected(self):
        for field, value in (("endedTicks", 50), ("microseconds", 1002), ("contentChunksRecorded", 999), ("threadLocalAllocatedBytes", -1)):
            with self.subTest(field=field):
                item = copy.deepcopy(self.reports["collector"]); item["rows"][0]["samples"][0][field] = value
                with self.assertRaises(subject.EvidenceError): self.check("collector", item)

    def test_missing_or_duplicate_inner_samples_rejected(self):
        self.reports["collector"]["rows"][0]["samples"].pop()
        with self.assertRaises(subject.EvidenceError): self.check("collector")
        self.reports["writer"]["rows"][1] = copy.deepcopy(self.reports["writer"]["rows"][0])
        with self.assertRaises(subject.EvidenceError): self.check("writer")

    def test_writer_wire_and_flush_claim_rejected(self):
        for field, value in (("wireSha256", "0" * 64), ("expectedWireSha256", "f" * 64), ("Flushes", 0), ("validated", False)):
            with self.subTest(field=field):
                item = copy.deepcopy(self.reports["writer"]); item["rows"][0]["observation"][field] = value
                with self.assertRaises(subject.EvidenceError): self.check("writer", item)

    def test_provider_negative_reset_or_forged_delta_rejected(self):
        for field, value in (("rawProcessAllocatedEndBytes", 99), ("rawProcessAllocatedDeltaBytes", 1), ("processAllocationMonotonic", False), ("checksum", 1)):
            with self.subTest(field=field):
                item = copy.deepcopy(self.reports["provider"]); item["rows"][0]["observation"][field] = value
                with self.assertRaises(subject.EvidenceError): self.check("provider", item)

    def test_nonfinite_or_negative_metrics_rejected(self):
        for value in (float("nan"), float("inf"), -1):
            item = copy.deepcopy(self.reports["writer"]); item["rows"][0]["observation"]["elapsedMilliseconds"] = value
            with self.assertRaises(subject.EvidenceError): self.check("writer", item)

    def test_async_thread_allocation_is_unavailable_not_invented(self):
        item = self.reports["writer"]; item["rows"][0]["observation"]["sameThread"] = False
        with self.assertRaises(subject.EvidenceError): self.check("writer", item)
        item["rows"][0]["observation"]["currentThreadAllocatedBytes"] = None
        self.assertIsNone(self.check("writer", item)[0][0]["metrics"]["threadAllocatedBytesPerUnit"])

    def test_digest_and_revision_shapes_rejected(self):
        for function, value in ((subject.digest, "z" * 64), (subject.revision, "not-a-source")):
            with self.assertRaises(subject.EvidenceError): function(value)

    def test_inventory_detects_modification_missing_and_extra(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); file = root / "one.dll"; file.write_bytes(b"one")
            before = subject.file_inventory(root)
            file.write_bytes(b"two"); self.assertNotEqual(before, subject.file_inventory(root))
            file.unlink(); self.assertNotEqual(before, subject.file_inventory(root))
            file.write_bytes(b"one"); (root / "extra.dll").write_bytes(b"extra")
            self.assertNotEqual(before, subject.file_inventory(root))

    def test_archive_cannot_escape_or_replace_unrelated_source(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.tar"
            with tarfile.open(path, "w") as archive:
                member = tarfile.TarInfo("../outside.cs"); member.size = 1; archive.addfile(member, io.BytesIO(b"x"))
            with self.assertRaises(subject.EvidenceError): subject.tar_inventory(path)


if __name__ == "__main__":
    unittest.main()
