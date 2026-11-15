import copy
import unittest
from collections import Counter
from decimal import Decimal
from pathlib import Path
from unittest.mock import patch
from qualification import EvidenceError, loads, money, register_scoped_ids, report_rows, validate_drain_report, validate_provider_instances
from reconcile import reconcile
from summarize import allocation_evidence, compare_runs, pressure_contrasts, quantile, run_statistics, stream_metric_deltas


def financial_fixture(legacy=False):
    definitions = [("normal", "standard-sse", "none", "10", "5", ".000075", "chat"),
                   ("before", "standard-sse", "before-usage", "202", "75", ".001255", "chat"),
                   ("after", "standard-sse", "after-usage", "10", "5", ".000075", "chat"),
                   ("audio", "audio", "none", "0", "0", ".00125", "transcription")]
    clients, requests = [], []
    cohorts = Counter()
    for index, (name, endpoint, cancel, prompt, output, cost, kind) in enumerate(definitions, 1):
        clients.append({"endpoint": endpoint, "cancel": cancel,
                        "row": {"id": name, "requestBillingId": name, "validated": True, "status": 200, "provider": {"completed": cancel == "none"}}})
        requests.append({"id": index, "virtualKeyId": 1, "requestType": kind, "status": 200,
                         "inputTokens": int(prompt), "outputTokens": int(output), "cost": cost,
                         "billedAtUtc": "2026-11-15T00:00:00Z", "billingRequestId": None})
        cohorts[(kind, 200, int(prompt), int(output), cost)] += 1
    total = sum((Decimal(row["cost"]) for row in requests), Decimal(0))
    balance = str(Decimal(100) - total)
    snapshot = {"schemaVersion": 1, "postgres": {"schemaVersion": 1, "database": "owned", "transactionReadOnly": "on",
        "keys": [{"id": 1, "groupId": 1}], "groups": [{"id": 1, "balance": balance, "lifetimeSpent": str(total), "lifetimeCreditsAdded": "100"}],
        "pricing": [{"inputPerMillion": "2.5", "outputPerMillion": "10", "audioPerMinute": ".06"}],
        "requests": requests, "requestCohorts": [{"requestType": k[0], "status": k[1], "inputTokens": k[2], "outputTokens": k[3], "cost": k[4], "count": v} for k, v in cohorts.items()],
        "requestTotals": {"count": 4, "cost": str(total), "inputTokens": 222, "outputTokens": 85, "billedRows": 4, "zeroCostRows": 0, "metadataBillingIds": 0},
        "ledgerTotals": {"count": 1, "debit": str(total), "credit": "0", "refund": "0", "deletedRows": 0, "unhandledTypes": 0,
                         "duplicateIdempotencyKeys": 0, "missingOriginalAmountForIdempotent": 0, "originalAmountMismatches": 0},
        "ledger": [{"id": 1, "groupId": 1, "type": 2, "amount": str(total), "originalAmount": str(total), "idempotencyKey": "batch-1", "balanceAfter": balance, "isDeleted": False}],
        "ledgerContinuityMismatches": 0, "duplicateRequestMetadataBillingIds": 0, "historyTotals": {"count": 0, "amount": "0"}},
        "redis": {"database": 2, "unitScale": 100000000, "groups": [{"groupId": 1,
        "scalars": {"reserved_spend": ".019925", "pending_spend": None, "pending_spend_units": None, "pending_spend_window_total_units": None},
        "hashes": {"spend_reservations": {}, "spend_reservations_started": {"after": ".019925"},
                   "spend_reservations_settled": {"normal": "7500", "before": "125500"},
                   "spend_reservation_owners": {"normal": "1", "before": "1", "after": "1"},
                   "spend_reservation_bounds": {"normal": ".02", "before": ".02", "after": ".02"},
                   "spend_reservations_indeterminate": {"after": {"knownUnits": "7500", "heldAmount": ".019925", "virtualKeyId": "1", "resolved": False, "reasonPresent": True}}},
        "pendingAndProcessingValues": []}]}}
    expectation = {"cohort": "candidate", "postgresDatabase": "owned", "redisDatabase": 2, "seedBalance": "100", "seedSpent": "0", "seedCredits": "100",
                   "chatCost": ".000075", "audioCost": ".00125", "beforeUsageCancellationState": "settled-bounded-estimate", "legacyResponsesDefect": legacy}
    if legacy:
        expectation["cohort"] = "legacy"
        clients.append({"endpoint": "responses-json", "cancel": "none", "row": {"id": "legacy", "requestBillingId": "legacy", "validated": False, "status": 502,
                        "errorResponseBody": "cannot be represented by the text-only Responses subset", "provider": {"completed": True}}})
        group = snapshot["redis"]["groups"][0]
        group["scalars"]["reserved_spend"] = ".039925"
        group["hashes"]["spend_reservations_started"]["legacy"] = ".02"
        group["hashes"]["spend_reservation_owners"]["legacy"] = "1"
        group["hashes"]["spend_reservation_bounds"]["legacy"] = ".02"
        group["hashes"]["spend_reservations_indeterminate"]["legacy"] = {"knownUnits": "0", "heldAmount": ".02", "virtualKeyId": "1", "resolved": False}
    return snapshot, clients, expectation


def client_report(cancel="none"):
    provider = {"id": "case-m0000", "completed": cancel == "none", "firstContentWriteBeginTicks": 1,
                "firstContentFlushEndTicks": 2, "usageWriteBeginTicks": 0 if cancel == "before-usage" else 3,
                "usageFlushEndTicks": 0 if cancel == "before-usage" else 4,
                "cancellationTicks": 5 if cancel != "none" else 0, "doneWriteBeginTicks": 0}
    row = {"id": provider["id"], "validated": True, "status": 200, "requestStartTicks": 1,
           "validatedEndTicks": 6, "firstContentReadTicks": 3, "cancelInitiatedTicks": 4 if cancel != "none" else 0,
           "usageEvents": 0 if cancel == "before-usage" else 1, "contentChunks": 10, "events": 14,
           "toolArgumentFragments": 0, "provider": provider, "error": None}
    return {"schemaVersion": 1, "passed": True, "configuration": {"endpoint": "standard-sse", "cancel": cancel, "requests": 1, "warmupRequests": 0, "chunks": 10, "toolCalls": False},
            "stopwatchFrequency": 1000, "warmup": {"requests": []}, "measured": {"requests": [row]},
            "providerObservations": {"stopwatchFrequency": 1000, "observations": [provider]}}


def performance_row(run=1, count=100, kind="completed-response", factor=1):
    return {"run": run, "kind": kind, "allocationEvidence": {"valid": True}, "configurationSignature": "same", "architecture": "X64", "caseIdCharacters": 32, "validated": count,
            "metrics": {"validatedRatePerSecond": 100 * factor, "p50Milliseconds": 5 * factor,
                        "p95Milliseconds": 8 * factor, "p99Milliseconds": 10 * factor,
                        "serverWindowCpuMillisecondsPerValidated": 2 * factor,
                        "serverWindowAllocatedBytesPerValidated": 1000 * factor,
                        "serverWindowSampledPeakWorkingSetBytes": 100000 * factor}}


class AllocationEvidenceTests(unittest.TestCase):
    @staticmethod
    def snapshots(runtime="10.0.15", server=True, raw_delta=1000):
        start = {"observerVersion": 2, "runtimeVersion": runtime, "isServerGc": server,
                 "gcConfiguration": {"ServerGC": str(server), "GCDynamicAdaptationMode": "1", "HeapCount": "2"},
                 "totalAllocatedBytes": 10000}
        end = copy.deepcopy(start)
        end["totalAllocatedBytes"] += raw_delta
        return start, end, {"allocatedBytes": raw_delta}

    def test_actual_source_generated_v2_metadata_on_both_runtimes(self):
        for name, expected in (("net10", False), ("net11", True)):
            fixture = loads((Path(__file__).parent / "fixtures" / ("observer-v2-" + name + ".json")).read_text())
            window = fixture["window"]
            result = allocation_evidence(window["start"], window["end"], window["deltas"])
            self.assertEqual(expected, result["valid"])
            self.assertTrue(result["isServerGc"])
            self.assertEqual("1", result["gcConfiguration"]["GCDynamicAdaptationMode"])
            self.assertTrue(all(isinstance(value, str) for value in result["gcConfiguration"].values()))

    def test_all_known_servergc_windows_unavailable_positive_and_negative(self):
        for amount in (1000, 0, -1000):
            with self.subTest(amount=amount):
                result = allocation_evidence(*self.snapshots(raw_delta=amount))
                self.assertFalse(result["valid"])
                self.assertEqual(amount, result["rawDeltaBytes"])
                self.assertEqual(amount < 0, result["observedNonmonotonic"])
                self.assertIn("131069", result["reason"])

    def test_other_runtime_or_workstation_negative_is_not_waived(self):
        for runtime, server in (("11.0.0", True), ("10.0.15", False), ("10.0.16", True), ("12.0.0", True)):
            with self.subTest(runtime=runtime, server=server), self.assertRaises(EvidenceError):
                allocation_evidence(*self.snapshots(runtime, server, -1000))
        self.assertTrue(allocation_evidence(*self.snapshots("11.0.0"))["valid"])
        self.assertTrue(allocation_evidence(*self.snapshots(server=False))["valid"])

    def test_missing_changed_or_inconsistent_metadata_cannot_qualify(self):
        start, end, delta = self.snapshots()
        del start["observerVersion"]; del end["observerVersion"]
        self.assertFalse(allocation_evidence(start, end, delta)["valid"])
        delta["allocatedBytes"] = -1; end["totalAllocatedBytes"] = start["totalAllocatedBytes"] - 1
        with self.assertRaises(EvidenceError): allocation_evidence(start, end, delta)
        for mutation in ("mode", "config", "type", "version", "counter"):
            start, end, delta = self.snapshots()
            if mutation == "mode": end["isServerGc"] = False
            elif mutation == "config": end["gcConfiguration"]["HeapCount"] = "1"
            elif mutation == "type": start["gcConfiguration"]["HeapCount"] = 2
            elif mutation == "version": end["observerVersion"] = 1
            else: delta["allocatedBytes"] += 1
            with self.subTest(mutation=mutation), self.assertRaises(EvidenceError): allocation_evidence(start, end, delta)

    def test_unavailable_counter_cannot_enter_ratios_or_pass_budget(self):
        left = [performance_row(i, 1000) for i in range(1, 6)]
        right = copy.deepcopy(left)
        # Retain a superficially favorable positive metric to prove the explicit
        # validity gate, rather than a null metric alone, prevents a budget pass.
        for row in left: row["allocationEvidence"] = {"valid": False}
        result = compare_runs(left, right)
        allocation = result["metrics"]["serverWindowAllocatedBytesPerValidated"]
        self.assertEqual("unavailable", allocation["status"])
        self.assertEqual(0, allocation["pairs"])
        self.assertIsNone(allocation["medianPairedRatio"])
        self.assertFalse(allocation["budgetEvaluated"])
        self.assertFalse(result["allocationGateSatisfied"])
        self.assertEqual("within-working-threshold", result["metrics"]["serverWindowCpuMillisecondsPerValidated"]["status"])

    def test_nonallocation_negative_counters_still_fail_actual_window_validation(self):
        report = client_report()
        report.update(runtime={"architecture": "X64"})
        report["configuration"]["caseId"] = "case"
        entry = {"requests": 1, "sourceSha": "source", "passed": True, "caseId": "case", "gatewayProcessId": 1}
        cohort = {"sourceSha": "source", "expectedRuntime": "10.0.15"}
        start, end, delta = self.snapshots(raw_delta=-1000)
        for snapshot in (start, end): snapshot.update(processId=1, architecture="X64")
        for field in ("cpuMilliseconds", "elapsedSeconds", "gcPauseMilliseconds", "gen0Collections", "gen1Collections", "gen2Collections"):
            bad = dict(delta, cpuMilliseconds=1, elapsedSeconds=1, gcPauseMilliseconds=0, gen0Collections=0, gen1Collections=0, gen2Collections=0)
            bad[field] = -1
            server = {"window": {"id": "case", "start": start, "end": end, "deltas": bad}}
            with self.subTest(field=field), self.assertRaises(EvidenceError):
                run_statistics(entry, report, server, {"snapshot": {"processId": 1}}, cohort, "X64")


class ExactMoneyTests(unittest.TestCase):
    def test_no_binary_float_or_nonfinite_or_duplicate_json(self):
        for value in (0.000075, True, "NaN", "Infinity"):
            with self.subTest(value=value), self.assertRaises(EvidenceError): money(value)
        for text in ('{"x":1,"x":2}', '{"x":NaN}'):
            with self.assertRaises(EvidenceError): loads(text)

    def test_parse_float_uses_exact_decimal(self):
        self.assertEqual(Decimal("0.00000001"), loads('{"x":0.00000001}')["x"])


class FinancialTests(unittest.TestCase):
    def test_exact_conservation_keeps_open_hold_distinct(self):
        result = reconcile(*financial_fixture())
        self.assertTrue(result["financialInvariantsPassed"], result["failedChecks"])
        self.assertFalse(result["fullyReconciled"])
        self.assertEqual(Decimal(".002655"), result["loggedCost"])
        self.assertEqual(1, result["beforeUsageCancellations"])
        self.assertEqual(Decimal(".000075"), result["knownIndeterminateCost"])

    def test_known_legacy_defect_never_qualifies(self):
        result = reconcile(*financial_fixture(legacy=True))
        self.assertFalse(result["financialInvariantsPassed"])
        self.assertTrue(result["recordedSpendConservationPassed"], result["failedChecks"])
        self.assertEqual(Decimal(".000075"), result["missingKnownProviderCharge"])

    def test_known_defect_classification_follows_config_not_cohort_label(self):
        snapshot, clients, expected = financial_fixture(legacy=True)
        expected.update(cohort="fixed", knownResponsesFailure=True)
        result = reconcile(snapshot, clients, expected)
        self.assertEqual(1, result["legacyResponsesFailures"])
        self.assertTrue(result["recordedSpendConservationPassed"])
        self.assertFalse(result["financialInvariantsPassed"])

    def test_financial_source_identity_is_enforced(self):
        snapshot, clients, expected = financial_fixture()
        expected["sourceSha"] = "a" * 40
        for item in clients: item["sourceSha"] = "b" * 40
        with self.assertRaises(EvidenceError): reconcile(snapshot, clients, expected)

    def test_money_and_state_mutations_are_not_tolerated(self):
        def changes(snapshot):
            pg, group = snapshot["postgres"], snapshot["redis"]["groups"][0]
            return {
                "one-spend-unit-balance": lambda: pg["groups"][0].update(balance="99.99734501"),
                "subunit-reservation-drift": lambda: group["scalars"].update(reserved_spend=".019925000000000001"),
                "wrong-settled-unit": lambda: group["hashes"]["spend_reservations_settled"].update(normal="7501"),
                "missing-known-unit": lambda: group["hashes"]["spend_reservations_indeterminate"]["after"].update(knownUnits="7499"),
                "resolved-hold": lambda: group["hashes"]["spend_reservations_indeterminate"]["after"].update(resolved=True),
                "inexact-bound-minus-known": lambda: group["hashes"]["spend_reservation_bounds"].update(after=".02000001"),
                "estimate-over-bound": lambda: group["hashes"]["spend_reservation_bounds"].update(before=".00125499"),
                "pending-spend": lambda: group["scalars"].update(pending_spend_units="1"),
                "processing-spend": lambda: group["pendingAndProcessingValues"].append({"value": ".00000001"}),
                "bad-original": lambda: pg["ledger"][0].update(originalAmount=".00265501"),
                "bad-request-tokens": lambda: pg["requests"][0].update(inputTokens=11),
                "invented-history": lambda: pg["historyTotals"].update(count=1, amount=".002655"),
                "wrong-http": lambda: pg["requestCohorts"][0].update(status=500),
                "missing-billed-row": lambda: pg["requests"][0].update(billedAtUtc=None),
                "misreported-metadata": lambda: pg["requestTotals"].update(metadataBillingIds=1),
            }
        for name in changes(financial_fixture()[0]):
            with self.subTest(name=name):
                snapshot, clients, expected = financial_fixture()
                changes(snapshot)[name]()
                self.assertFalse(reconcile(snapshot, clients, expected)["financialInvariantsPassed"])

    def test_no_free_assumption_for_before_usage(self):
        snapshot, clients, expected = financial_fixture()
        del snapshot["redis"]["groups"][0]["hashes"]["spend_reservations_settled"]["before"]
        with self.assertRaises(EvidenceError): reconcile(snapshot, clients, expected)

    def test_database_identity_is_required(self):
        snapshot, clients, expected = financial_fixture()
        snapshot["postgres"]["database"] = "user-database"
        with self.assertRaises(EvidenceError): reconcile(snapshot, clients, expected)

    def test_reused_logical_marker_across_provider_instances_keeps_billing_ids_distinct(self):
        snapshot, clients, expected = financial_fixture()
        for item in clients:
            item["providerInstanceId"] = "provider-" + item["row"]["id"]
            item["profileDirectoryScope"] = "profile"
            item["row"]["id"] = "logical-m0000"
        result = reconcile(snapshot, clients, expected)
        self.assertTrue(result["financialInvariantsPassed"])
        self.assertEqual(4, len({row["providerInstanceId"] for row in result["clientStates"]}))
        clients[1]["row"]["requestBillingId"] = clients[0]["row"]["requestBillingId"]
        with self.assertRaises(EvidenceError): reconcile(snapshot, clients, expected)


class FrozenValidatorTests(unittest.TestCase):
    def test_valid_complete_and_cancellation_are_different_boundaries(self):
        for cancel in ("none", "before-usage", "after-usage"):
            self.assertEqual(1, len(report_rows(client_report(cancel))))

    def test_corrupt_frame_counters_or_provider_boundaries_rejected(self):
        for mutation in ("content", "usage", "completed", "observation", "passed"):
            with self.subTest(mutation=mutation):
                report = client_report()
                if mutation == "content": report["measured"]["requests"][0]["contentChunks"] = 9
                elif mutation == "usage": report["measured"]["requests"][0]["usageEvents"] = 2
                elif mutation == "completed": report["measured"]["requests"][0]["provider"]["completed"] = False
                elif mutation == "observation": report["providerObservations"]["observations"] = []
                else: report["passed"] = False
                with self.assertRaises(EvidenceError): report_rows(report)

    def test_cancel_must_precede_done_and_preserve_usage_boundary(self):
        report = client_report("after-usage")
        report["measured"]["requests"][0]["provider"]["doneWriteBeginTicks"] = 5
        with self.assertRaises(EvidenceError): report_rows(report)
        report = client_report("before-usage")
        report["measured"]["requests"][0]["usageEvents"] = 1
        with self.assertRaises(EvidenceError): report_rows(report)


class ProviderIdentityScopeTests(unittest.TestCase):
    def test_same_payload_marker_allowed_only_in_separate_provider_instances(self):
        seen, instances = set(), {}
        for cohort in ("fixed", "candidate"):
            register_scoped_ids(seen, instances, {"cohort": cohort, "run": 1, "logicalCaseId": "chat-json", "providerInstanceId": cohort + "-r1"}, [{"id": "chat-json-m0000"}])
        self.assertEqual(2, len(seen))
        with self.assertRaises(EvidenceError): register_scoped_ids(seen, instances, {"cohort": "candidate", "run": 1, "logicalCaseId": "chat-json", "providerInstanceId": "candidate-r1"}, [{"id": "chat-json-m0000"}])

    def test_provider_instance_cannot_be_reassigned_to_another_run(self):
        seen, instances = set(), {}
        register_scoped_ids(seen, instances, {"cohort": "fixed", "run": 1, "logicalCaseId": "chat-json", "providerInstanceId": "same"}, [{"id": "one"}])
        with self.assertRaises(EvidenceError): register_scoped_ids(seen, instances, {"cohort": "fixed", "run": 2, "logicalCaseId": "chat-json", "providerInstanceId": "same"}, [{"id": "two"}])

    def test_historical_shared_provider_still_rejects_duplicate_markers(self):
        seen, instances = set(), {}
        register_scoped_ids(seen, instances, {"cohort": "legacy", "run": 1}, [{"id": "one"}])
        with self.assertRaises(EvidenceError): register_scoped_ids(seen, instances, {"cohort": "fixed", "run": 1}, [{"id": "one"}])

    def test_fresh_provider_exit_is_bound_to_actual_manifest_process(self):
        prefix = "phase0-fixed------------r1"
        manifest = [{"cohort": "fixed", "run": 1, "providerInstanceId": prefix, "providerProcessId": 105, "gatewayProcessId": 106}]
        exit_record = {"processId": 105, "exitCode": 0, "graceful": True}
        with patch("qualification.read", return_value=exit_record) as reader, patch("qualification.sha256", return_value="a" * 64):
            self.assertTrue(validate_provider_instances("evidence", manifest, {"profile": "phase0"})["freshPerCohortRun"])
            reader.assert_called_with(Path("evidence") / (prefix + "-provider.exit.json"))
            exit_record["processId"] = 107
            with self.assertRaises(EvidenceError): validate_provider_instances("evidence", manifest, {"profile": "phase0"})


class PressureTests(unittest.TestCase):
    @staticmethod
    def report():
        report = client_report()
        report["configuration"].update(receiveBufferBytes=8192, baseUrl="http://127.0.0.1:18080")
        report["measured"]["requests"][0].update(firstBodyReadTicks=3, lastBodyReadTicks=5, headersTicks=3, providerFirstContentToLastFlushMilliseconds=4)
        report["measured"]["requests"][0]["provider"]["lastFlushEndTicks"] = 5
        report["sockets"] = [{"initialRequestId": "case-m0000", "host": "127.0.0.1", "port": 18080, "requestedReceiveBufferBytes": 8192, "actualReceiveBufferBytes": 16384, "noDelay": True, "connectedTicks": 2}]
        return report

    def test_actual_linux_socket_value_preserved_without_assuming_requested_equals_actual(self):
        report = self.report()
        self.assertEqual(1, len(report_rows(report)))
        self.assertEqual(16384, report["sockets"][0]["actualReceiveBufferBytes"])

    def test_missing_socket_controls_or_wrong_derived_provider_span_rejected(self):
        for mutation in ("missing", "requested", "actual", "nodelay", "host", "correlation", "span", "bodytime"):
            report = self.report()
            if mutation == "missing": report["sockets"] = []
            elif mutation == "requested": report["sockets"][0]["requestedReceiveBufferBytes"] = 0
            elif mutation == "actual": report["sockets"][0]["actualReceiveBufferBytes"] = 0
            elif mutation == "nodelay": report["sockets"][0]["noDelay"] = False
            elif mutation == "host": report["sockets"][0]["port"] = 18083
            elif mutation == "correlation": report["sockets"][0]["initialRequestId"] = "other"
            elif mutation == "span": report["measured"]["requests"][0]["providerFirstContentToLastFlushMilliseconds"] = 3
            else: report["measured"]["requests"][0]["lastBodyReadTicks"] = 7
            with self.subTest(mutation=mutation), self.assertRaises(EvidenceError): report_rows(report)

    def test_adverse_paced_provider_span_is_retained_without_automatic_pressure_claim(self):
        fast, paced = performance_row(), performance_row()
        for row, arm, span in ((fast, "fast", 100), (paced, "paced", 80)):
            row.update(cohort="candidate", case="pressure-standard-" + arm, caseId=arm)
            row["metrics"]["providerFirstContentToLastFlushP50Milliseconds"] = span
            row["pressureTransport"] = {"controlSignature": "same", "actualReceiveBufferBytes": [16384], "slowReadDelayMilliseconds": 0 if arm == "fast" else 2, "slowReadBytes": 16384 if arm == "fast" else 4096}
        result = pressure_contrasts([fast, paced])[0]
        self.assertEqual(.8, result["metrics"]["providerFirstContentToLastFlushP50Milliseconds"]["medianPairedRatio"])
        self.assertFalse(result["acceptanceGranted"])
        paced["pressureTransport"]["controlSignature"] = "changed"
        with self.assertRaises(EvidenceError): pressure_contrasts([fast, paced])


class MatchedStatisticsTests(unittest.TestCase):
    def test_nearest_rank(self):
        self.assertEqual(3, quantile([3, 1, 2], .95))

    def test_small_sample_p99_never_qualifies_despite_pooled_count(self):
        left = [performance_row(i, 200) for i in range(1, 6)]
        result = compare_runs(left, copy.deepcopy(left))
        self.assertEqual("insufficient-evidence", result["metrics"]["p99Milliseconds"]["status"])
        self.assertFalse(result["acceptanceGranted"])
        with self.assertRaises(EvidenceError): compare_runs(left, left, {"minimumP99SamplesPerRun": 2})

    def test_threshold_exceedance_is_diagnostic_not_an_accepted_tradeoff(self):
        left = [performance_row(i, 1000) for i in range(1, 6)]
        right = [performance_row(i, 1000, factor=1.2) for i in range(1, 6)]
        result = compare_runs(left, right)
        self.assertEqual("outside-working-threshold", result["metrics"]["serverWindowCpuMillisecondsPerValidated"]["status"])
        self.assertTrue(result["thresholdsAreDiagnosticsOnly"])
        self.assertFalse(result["acceptanceGranted"])

    def test_latency_budget_uses_larger_of_five_percent_or_two_milliseconds(self):
        left = [performance_row(i, 1000) for i in range(1, 6)]
        right = copy.deepcopy(left)
        for row in right: row["metrics"]["p50Milliseconds"] = 6.9
        result = compare_runs(left, right)
        self.assertEqual("within-working-threshold", result["metrics"]["p50Milliseconds"]["status"])
        self.assertAlmostEqual(6.9 / 7, result["metrics"]["p50Milliseconds"]["medianPairedBudgetRatio"])
        for row in right: row["metrics"]["p50Milliseconds"] = 7.01
        self.assertEqual("outside-working-threshold", compare_runs(left, right)["metrics"]["p50Milliseconds"]["status"])

    def test_latency_budget_percent_dominates_for_large_baselines(self):
        left = [performance_row(i, 1000) for i in range(1, 6)]
        right = copy.deepcopy(left)
        for row in left: row["metrics"]["p99Milliseconds"] = 100
        for row in right: row["metrics"]["p99Milliseconds"] = 105.01
        self.assertEqual("outside-working-threshold", compare_runs(left, right)["metrics"]["p99Milliseconds"]["status"])

    def test_failing_responses_and_cancellation_are_not_success_capacity(self):
        self.assertEqual([], compare_runs([performance_row(kind="known-legacy-failure")], [performance_row()])["pairedRuns"])
        self.assertEqual([], compare_runs([performance_row(kind="cancellation-scenario")], [performance_row()])["pairedRuns"])
        result = compare_runs([performance_row(kind="cancellation-scenario")], [performance_row(kind="cancellation-scenario")])
        self.assertIn("scenarios/s", result["rateMeaning"])

    def test_workload_architecture_or_payload_size_mismatch_rejected(self):
        for field, value in (("architecture", "Arm64"), ("configurationSignature", "other"), ("caseIdCharacters", 33)):
            with self.subTest(field=field):
                other = performance_row()
                other[field] = value
                with self.assertRaises(EvidenceError): compare_runs([performance_row()], [other])

    def test_ratio_is_median_of_matched_run_ratios(self):
        left, right = [performance_row(i, 1000) for i in range(1, 4)], [performance_row(i, 1000) for i in range(1, 4)]
        for row, value in zip(left, [1, 100, 101]): row["metrics"]["validatedRatePerSecond"] = value
        for row, value in zip(right, [50, 51, 1000]): row["metrics"]["validatedRatePerSecond"] = value
        result = compare_runs(left, right, {"minimumRuns": 3})
        self.assertAlmostEqual(1000 / 101, result["metrics"]["validatedRatePerSecond"]["medianPairedRatio"])
        self.assertNotAlmostEqual(51 / 100, result["metrics"]["validatedRatePerSecond"]["medianPairedRatio"])


class ServerMetricTests(unittest.TestCase):
    @staticmethod
    def metrics(count, seconds):
        return "\n".join(f"conduitllm_gateway_streaming_conduit_stream_time_to_{name}_{kind} {value}" for name in ("provider_first_chunk", "client_first_flush") for kind, value in (("count", count), ("sum", seconds)))

    def test_sse_delta_excludes_warmups_and_reports_mean_not_quantile(self):
        result = stream_metric_deltas(self.metrics(20, ".4"), self.metrics(120, "2.4"), "standard-sse", 100)
        self.assertEqual(100, result["clientFirstFlush"]["deltaCount"])
        self.assertEqual(20, result["clientFirstFlush"]["meanMilliseconds"])
        self.assertNotIn("p99", result["clientFirstFlush"])

    def test_missing_sse_metrics_count_mismatch_and_reset_fail(self):
        for before, after in (("", ""), (self.metrics(20, ".4"), self.metrics(119, "2.4")), (self.metrics(20, ".4"), self.metrics(10, ".2")), (self.metrics(20, ".4"), "")):
            with self.subTest(before=before, after=after), self.assertRaises(EvidenceError): stream_metric_deltas(before, after, "enhanced-sse", 100)

    def test_non_sse_absent_or_unchanged_is_na_not_zero_latency(self):
        for before, after in (("", ""), (self.metrics(20, ".4"), self.metrics(20, ".4"))):
            result = stream_metric_deltas(before, after, "responses-sse", 100)
            self.assertIsNone(result["clientFirstFlush"]["meanMilliseconds"])
            self.assertFalse(result["clientFirstFlush"]["applicable"])
        with self.assertRaises(EvidenceError): stream_metric_deltas("", self.metrics(1, ".1"), "audio", 1)

    def test_duplicate_or_incomplete_histogram_fails(self):
        sample = self.metrics(1, ".1")
        with self.assertRaises(EvidenceError): stream_metric_deltas("", sample + "\n" + sample, "standard-sse", 1)
        incomplete = "\n".join(line for line in sample.splitlines() if not line.startswith("conduitllm_gateway_streaming_conduit_stream_time_to_client_first_flush_sum "))
        with self.assertRaises(EvidenceError): stream_metric_deltas("", incomplete, "standard-sse", 1)


class DrainEvidenceTests(unittest.TestCase):
    @staticmethod
    def fixture():
        poll = {"database": "phase2_fixed", "readOnly": "on", "keyCount": 1, "groupCount": 1, "logCount": 18,
                "unbilledOrNonpositiveLogs": 0, "deletedOrUnsupportedLedger": 0, "logCost": ".0084", "ledgerDebit": ".0084", "spent": ".0084", "balance": "99.9916", "credits": "100", "failures": [], "elapsedMilliseconds": 1,
                "queues": [{"schema": schema, "table": table, "activeKnownSpendRows": 0, "retainedHandledKnownSpendRows": 0} for schema, table in (("wolverine_conduit_gateway", "wolverine_incoming_envelopes"), ("wolverine_conduit_gateway", "wolverine_outgoing_envelopes"), ("wolverine_queues", "wolverine_queue_spend_update_events"), ("wolverine_queues", "wolverine_queue_spend_update_events_scheduled"))]}
        return {"schemaVersion": 1, "passed": True, "scope": "known persisted spend drained; not provider evidence completeness", "expectedLogs": 18, "assemblySha256": "a" * 64, "runtime": ".NET 10.0.15", "sourceSdk": "10.0.404", "messageTypes": ["ConduitLLM.Core.Events.SpendUpdateRequested", "ConduitLLM.Configuration.Events.BatchSpendFlushRequestedEvent"], "queryHash": "b" * 64, "polls": [poll], "finalState": poll, "elapsedMs": 2}

    def test_strict_drain_scope_identity_and_exact_state_pass(self):
        report = self.fixture()
        report["finalState"]["queues"][0]["retainedHandledKnownSpendRows"] = 2
        validate_drain_report(report, 18, "phase2_fixed", "a" * 64, "10.0.15")

    def test_forged_success_or_stale_cumulative_identity_is_rejected(self):
        for mutation in ("cost", "queue", "table", "database", "assembly", "expected", "runtime", "query", "polls"):
            report = self.fixture()
            if mutation == "cost": report["finalState"]["ledgerDebit"] = ".00839999"
            elif mutation == "queue": report["finalState"]["queues"][2]["activeKnownSpendRows"] = 2
            elif mutation == "table": report["finalState"]["queues"].pop()
            elif mutation == "database": report["finalState"]["database"] = "phase2_other"
            elif mutation == "assembly": report["assemblySha256"] = "c" * 64
            elif mutation == "expected": report["expectedLogs"] = 17
            elif mutation == "runtime": report["runtime"] = ".NET 11.0.0"
            elif mutation == "query": report["queryHash"] = ""
            else: report["polls"] = []
            with self.subTest(mutation=mutation), self.assertRaises(EvidenceError): validate_drain_report(report, 18, "phase2_fixed", "a" * 64, "10.0.15")


if __name__ == "__main__": unittest.main()
