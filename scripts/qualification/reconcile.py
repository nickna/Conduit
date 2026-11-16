"""Exact, read-only conservation and reservation checks for one owned cohort."""
from __future__ import annotations
import argparse
from collections import Counter
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from qualification import EvidenceError, analysis_identity, integer, load_clients, money, read, require, sha256, write

ZERO = Decimal(0)
SCALE = Decimal(100000000)
QUANTUM = Decimal("0.00000001")


def reconcile(snapshot, clients, expectation):
    pg, redis = snapshot["postgres"], snapshot["redis"]
    checks, diagnostics, states = {}, [], []
    def check(name, condition):
        checks[name] = bool(condition)
    require(snapshot["schemaVersion"] == 1 and pg["schemaVersion"] == 1, "Unknown financial snapshot schema")
    require(pg["transactionReadOnly"] == "on", "SQL snapshot was not read-only")
    require(pg["database"] == expectation["postgresDatabase"] and redis["database"] == expectation["redisDatabase"], "Owned fixture identity mismatch")
    require(integer(redis["unitScale"]) == int(SCALE), "Unknown spend unit scale")
    require(len(pg["keys"]) == len(pg["groups"]) == len(redis["groups"]) == 1, "Expected one independently seeded fixture group/key")
    key, group, rg = pg["keys"][0], pg["groups"][0], redis["groups"][0]
    require(key["groupId"] == group["id"] == rg["groupId"], "SQL/Redis group identity differs")
    require(len(pg["pricing"]) == 1, "Expected one fixture pricing row")
    rate = pg["pricing"][0]
    input_rate, output_rate = money(rate["inputPerMillion"]), money(rate["outputPerMillion"])
    chat = (input_rate * 10 + output_rate * 5) / Decimal(1000000)
    audio = money(rate["audioPerMinute"]) * Decimal("1.25") / 60
    require(chat == money(expectation["chatCost"]) == Decimal("0.000075") and audio == money(expectation["audioCost"]) == Decimal("0.00125"), "Fixture pricing changed")
    require(expectation["beforeUsageCancellationState"] == "settled-bounded-estimate", "Unsupported before-usage expectation; declare a separate reviewed policy")

    hashes, scalars = rg["hashes"], rg["scalars"]
    names = ("spend_reservations", "spend_reservations_started", "spend_reservations_settled", "spend_reservation_owners", "spend_reservation_bounds", "spend_reservations_indeterminate")
    require(all(name in hashes and isinstance(hashes[name], dict) for name in names), "Incomplete Redis reservation snapshot")
    all_reservations = set().union(*(hashes[name] for name in names))
    expected_reservations, correlation_ids = set(), set()
    expected_cohorts, before_costs = Counter(), Counter()
    known_missing = ZERO
    known_indeterminate = ZERO
    legacy_count = before_count = after_count = audio_count = 0
    protocol_failures = 0
    expected_cost = ZERO
    for item in clients:
        row, endpoint, cancel = item["row"], item["endpoint"], item["cancel"]
        if expectation.get("sourceSha"):
            require(item.get("sourceSha") == expectation["sourceSha"], "Financial expectation/client source differs")
        correlation = row.get("requestBillingId")  # Historical field name; it is the wire x-request-id.
        check_id = correlation or item.get("caseId", "") + ":" + row["id"]
        state_identity = {"requestId": row["id"], "caseId": item.get("caseId"), "providerInstanceId": item.get("providerInstanceId"), "profileDirectoryScope": item.get("profileDirectoryScope")}
        if correlation:
            require(correlation not in correlation_ids, "Duplicate wire request correlation")
            correlation_ids.add(correlation)
        legacy = expectation.get("knownResponsesFailure", expectation.get("legacyResponsesDefect")) is True and endpoint == "responses-json" and row["status"] == 502 and not row["validated"] and row["provider"]["completed"] and "cannot be represented by the text-only Responses subset" in (row.get("errorResponseBody") or "")
        if not row["validated"]:
            protocol_failures += 1
        if endpoint == "audio":
            # Source contract: audio has group batch spend, not a parent chat reservation.
            audio_count += 1
            check("audioHasNoParentReservation:" + check_id, not correlation or correlation not in all_reservations)
            expected_cohorts[("transcription", 200, 0, 0, audio)] += 1
            expected_cost += audio
            states.append({**state_identity, "state": "audio-group-batch-no-parent-reservation", "cost": audio})
            continue
        # Chat/Responses explicitly write Accounting.BillingRequestId into x-request-id.
        # Do not generalize that source contract to audio or arbitrary client headers.
        require(correlation, "Chat/Responses reservation correlation is missing")
        expected_reservations.add(correlation)
        check("reservationOwner:" + check_id, hashes["spend_reservation_owners"].get(correlation) == str(key["id"]))
        require(correlation in hashes["spend_reservation_bounds"], "Reservation bound absent")
        bound = money(hashes["spend_reservation_bounds"][correlation])
        require(bound >= 0, "Negative reservation bound")
        pending = correlation in hashes["spend_reservations"]
        started = correlation in hashes["spend_reservations_started"]
        settled = correlation in hashes["spend_reservations_settled"]
        record = hashes["spend_reservations_indeterminate"].get(correlation)
        request_type = "responses" if endpoint.startswith("responses-") else "chat"
        if legacy:
            legacy_count += 1
            known_missing += chat
            expected_known = ZERO
            cost, state = ZERO, "legacy-completed-provider-known-charge-missing"
        elif cancel == "before-usage":
            before_count += 1
            require(settled, "Before-usage cancellation is not the declared settled bounded-estimate policy")
            cost = Decimal(integer(hashes["spend_reservations_settled"][correlation])) / SCALE
            require(cost > 0, "Before-usage cancellation cannot be assumed free")
            check("beforeUsageBound:" + check_id, cost <= bound)
            before_costs[cost] += 1
            check("beforeUsageReservation:" + check_id, not pending and not started and record is None)
            expected_cost += cost
            states.append({**state_identity, "state": "settled-bounded-estimate", "cost": cost})
            continue
        elif cancel == "after-usage":
            after_count += 1
            cost, expected_known, state = chat, chat, "known-provider-cost-with-unresolved-hold"
        else:
            cost, state = chat, "completed-provider-usage-settled"
            check("completedReservation:" + check_id, settled and not pending and not started and record is None)
            if settled:
                check("settledUnits:" + check_id, integer(hashes["spend_reservations_settled"][correlation]) == int(chat * SCALE))
            expected_cohorts[(request_type, 200, 10, 5, cost)] += 1
            expected_cost += cost
            states.append({**state_identity, "state": state, "cost": cost})
            continue
        check("indeterminateState:" + check_id, started and not pending and not settled and isinstance(record, dict))
        if record is not None:
            known = Decimal(integer(record["knownUnits"])) / SCALE
            held = money(record["heldAmount"])
            known_indeterminate += known
            check("knownSpend:" + check_id, known == expected_known)
            check("unresolvedHold:" + check_id, record["resolved"] is False and held > 0 and held == max(ZERO, bound - known) and held == money(hashes["spend_reservations_started"][correlation]))
            check("indeterminateOwner:" + check_id, integer(record["virtualKeyId"]) == key["id"])
            if "reasonPresent" in record:
                check("indeterminateReason:" + check_id, record["reasonPresent"] is True)
        if not legacy:
            expected_cohorts[(request_type, 200, 10, 5, cost)] += 1
        expected_cost += cost
        states.append({**state_identity, "state": state, "cost": cost})

    check("allRedisReservationsHaveClientEvidence", all_reservations == expected_reservations)
    check("noUnstartedReservations", not hashes["spend_reservations"])
    held_total = sum((money(value) for name in ("spend_reservations", "spend_reservations_started") for value in hashes[name].values()), ZERO)
    check("reservedScalarEqualsExactHolds", money(scalars["reserved_spend"] or "0") == held_total)
    pending_values = [scalars[name] for name in ("pending_spend", "pending_spend_units", "pending_spend_window_total_units")]
    pending_values += [item["value"] for item in rg["pendingAndProcessingValues"]]
    check("noPendingOrProcessingSpend", all(value is None or money(value) == ZERO for value in pending_values))

    actual_cohorts = Counter()
    all_sql_cohorts = Counter()
    estimate_cohorts = Counter()
    for row in pg["requestCohorts"]:
        cost = money(row["cost"])
        identity = (row["requestType"], row["status"], row["inputTokens"], row["outputTokens"], cost)
        require(row["count"] > 0, "Nonpositive SQL cohort count")
        all_sql_cohorts[identity] += row["count"]
        if row["requestType"] == "chat" and row["status"] == 200 and (row["inputTokens"], row["outputTokens"]) != (10, 5):
            formula = ((input_rate * integer(row["inputTokens"]) + output_rate * integer(row["outputTokens"])) / Decimal(1000000)).quantize(QUANTUM, rounding=ROUND_HALF_UP)
            check("boundedEstimateFormula:" + repr(identity), formula == cost and row["inputTokens"] >= 0 and row["outputTokens"] >= 0)
            estimate_cohorts[cost] += row["count"]
        else:
            actual_cohorts[identity] += row["count"]
    check("completedLogCohortsExact", actual_cohorts == expected_cohorts)
    check("boundedEstimateCohortsExact", estimate_cohorts == before_costs and sum(estimate_cohorts.values()) == before_count)
    rt, lt = pg["requestTotals"], pg["ledgerTotals"]
    log_rows = pg["requests"]
    raw_cohorts = Counter((row["requestType"], row["status"], row["inputTokens"], row["outputTokens"], money(row["cost"])) for row in log_rows)
    check("rawRequestsEqualSqlCohorts", raw_cohorts == all_sql_cohorts)
    check("requestRowsAndTotalsAgree", len(log_rows) == rt["count"] == len(clients) - legacy_count)
    check("requestIdsUnique", len({row["id"] for row in log_rows}) == len(log_rows))
    check("requestScopeExact", all(row["virtualKeyId"] == key["id"] for row in log_rows))
    check("requestTokenTotalsExact", sum(integer(row["inputTokens"]) for row in log_rows) == rt["inputTokens"] and sum(integer(row["outputTokens"]) for row in log_rows) == rt["outputTokens"])
    check("requestCostsAndTotalExact", sum((money(row["cost"]) for row in log_rows), ZERO) == money(rt["cost"]) == expected_cost)
    check("allPositiveRowsBilled", rt["billedRows"] == rt["count"] and rt["zeroCostRows"] == 0 and all(row["billedAtUtc"] is not None and money(row["cost"]) > 0 for row in log_rows))
    check("ledgerDebitEqualsLogs", money(lt["debit"]) == money(rt["cost"]))
    check("ledgerNoCreditsRefundsDeletedUnknown", money(lt["credit"]) == money(lt["refund"]) == ZERO and lt["deletedRows"] == lt["unhandledTypes"] == 0)
    check("ledgerIdempotencyAndOriginalAmounts", lt["duplicateIdempotencyKeys"] == lt["missingOriginalAmountForIdempotent"] == lt["originalAmountMismatches"] == 0)
    check("ledgerContinuity", pg["ledgerContinuityMismatches"] == 0)
    check("metadataBillingIdsNotDuplicated", pg["duplicateRequestMetadataBillingIds"] == 0)
    ledger = pg["ledger"]
    idempotency_keys = [row["idempotencyKey"] for row in ledger if row["idempotencyKey"] is not None]
    check("rawLedgerIdsAndKeysUnique", len({row["id"] for row in ledger}) == len(ledger) and len(set(idempotency_keys)) == len(idempotency_keys))
    check("rawOriginalAmountsExact", all((row["originalAmount"] is not None and money(row["originalAmount"]) == money(row["amount"])) if row["idempotencyKey"] is not None else (row["originalAmount"] is None or money(row["originalAmount"]) == money(row["amount"])) for row in ledger))
    check("ledgerRowsAndTotalsAgree", len(ledger) == lt["count"] and sum((money(row["amount"]) for row in ledger if row["type"] == 2 and not row["isDeleted"]), ZERO) == money(lt["debit"]))
    running = money(expectation["seedBalance"])
    for row in sorted(ledger, key=lambda item: item["id"]):
        require(row["groupId"] == group["id"] and row["type"] in (1, 2, 3) and not row["isDeleted"] and money(row["amount"]) > 0, "Unexpected ledger scope/type/deletion/amount")
        running += money(row["amount"]) * (-1 if row["type"] == 2 else 1)
        check("ledgerBalanceAfter:" + str(row["id"]), money(row["balanceAfter"]) == running)
    check("groupBalanceExact", money(group["balance"]) == running == money(expectation["seedBalance"]) - expected_cost)
    check("groupLifetimeSpentExact", money(group["lifetimeSpent"]) == money(expectation["seedSpent"]) + expected_cost)
    check("groupLifetimeCreditsExact", money(group["lifetimeCreditsAdded"]) == money(expectation["seedCredits"]))
    check("groupBatchDoesNotInventKeyHistory", pg["historyTotals"]["count"] == 0 and money(pg["historyTotals"]["amount"]) == ZERO)
    check("noUnexpectedProtocolFailures", protocol_failures == legacy_count)
    check("noCompletedProviderWithoutKnownCharge", legacy_count == 0 and known_missing == ZERO)
    metadata_ids = [row["billingRequestId"] for row in log_rows if row.get("billingRequestId") is not None]
    check("rawMetadataBillingIdsUnique", len(metadata_ids) == len(set(metadata_ids)) and rt["metadataBillingIds"] == len(metadata_ids))
    if len(metadata_ids) != len(log_rows):
        diagnostics.append("RequestLogs do not universally expose a billing/correlation ID. Database validation is an exact cohort/count/cost multiset, not a universal per-request SQL join.")
    if legacy_count:
        diagnostics.append("Known legacy #1770 failures remain unqualified; matching historical defect shape does not waive missing logs or known charges.")
    failed = [name for name, passed in checks.items() if not passed]
    # Conservation remains separately useful when a historical missing-charge defect is visible.
    conservation_failed = [name for name in failed if name not in ("noCompletedProviderWithoutKnownCharge",)]
    return {"schemaVersion": 1, "cohort": expectation["cohort"], "financialInvariantsPassed": not failed,
            "recordedSpendConservationPassed": not conservation_failed,
            "fullyReconciled": not failed and held_total == 0 and not hashes["spend_reservations_indeterminate"],
            "database": pg["database"], "redisDatabase": redis["database"], "expectedRecordedCost": expected_cost,
            "loggedCost": money(rt["cost"]), "ledgerDebit": money(lt["debit"]), "heldAmount": held_total,
            "knownIndeterminateCost": known_indeterminate, "missingKnownProviderCharge": known_missing,
            "clientsIncludingWarmups": len(clients), "legacyResponsesFailures": legacy_count,
            "beforeUsageCancellations": before_count, "afterUsageCancellations": after_count, "audioRequests": audio_count,
            "boundedEstimateBasis": "Per-correlation settled units independently checked against persisted estimated-token cohort counts and exact rate formula, and each reservation bound. Tokenizer estimates are not independently replayed; missing universal RequestLog billing IDs prevent a fabricated per-request join.",
            "messagingDrainQualified": False,
            "messagingScope": "This immutable PostgreSQL/Redis snapshot schema does not enumerate Wolverine queues. Separate bounded live-host message-drain evidence is required; HTTP validation, Redis zero pending, and exit0 do not prove queue settlement.",
            "checks": checks, "failedChecks": failed, "diagnostics": diagnostics, "clientStates": states,
            "scope": "Exact Decimal conservation and source-backed fixture expectations; never writes or resolves holds; no monetary tolerance or automatic acceptance of tradeoffs"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot", required=True)
    parser.add_argument("--directory", required=True, action="append", help="Repeat for separate profiles captured in this same cohort financial snapshot")
    parser.add_argument("--expectation", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        expectation = read(args.expectation)
        clients, sources, profiles, seen, prior_manifests = [], [], [], set(), set()
        cumulative_logs = 0
        for directory in args.directory:
            require(str(Path(directory).resolve()) not in seen, "Repeated profile directory")
            seen.add(str(Path(directory).resolve()))
            rows, evidence = load_clients(directory, expectation["cohort"])
            manifest = read(Path(directory) / "manifest.json")
            configuration_path = Path(directory) / "configuration.source.json"
            if configuration_path.exists():
                configuration = read(configuration_path)
                initial = configuration.get("initialLogCounts", {}).get(expectation["cohort"], 0)
                require(integer(initial) == cumulative_logs, "Profile initial log count does not match preceding supplied profiles")
                if configuration.get("initialLogCountsSourceManifestSha256"):
                    require(configuration["initialLogCountsSourceManifestSha256"] in prior_manifests, "Profile initial counts refer to an unsupplied manifest")
                matches = [row for row in configuration["cohorts"] if row["id"] == expectation["cohort"]]
                require(len(matches) == 1, "Selected financial cohort configuration missing/duplicated")
                cohort_config = matches[0]
                require(bool(cohort_config.get("knownResponsesFailure")) == bool(expectation.get("knownResponsesFailure", expectation.get("legacyResponsesDefect"))), "Financial known-finding flag differs from cohort configuration")
            clients.extend(rows)
            sources.extend(dict(item, directory=str(Path(directory).resolve())) for item in evidence)
            manifest_hash = sha256(Path(directory) / "manifest.json")
            cumulative_logs += sum(integer(row["warmups"]) + (0 if row.get("knownProductionFinding") else integer(row["requests"])) for row in manifest if row["cohort"] == expectation["cohort"])
            profiles.append({"directory": str(Path(directory).resolve()), "manifestSha256": manifest_hash, "clientsIncludingWarmups": len(rows), "expectedCumulativeLogs": cumulative_logs, "knownPersistedSpendDrainQualified": configuration_path.exists() and bool(configuration.get("drainProbe"))})
            prior_manifests.add(manifest_hash)
        require(len({(row["profileDirectoryScope"], row["providerInstanceId"], row["row"]["id"]) for row in clients}) == len(clients), "Request markers overlap within a profile/provider scope")
        if "profileSources" in expectation:
            require(sorted(row["manifestSha256"] for row in profiles) == sorted(expectation["profileSources"]), "Financial profile manifest inventory differs")
        result = reconcile(read(args.snapshot), clients, expectation)
        result["reportSources"] = sources
        result["profiles"] = profiles
        result["knownPersistedSpendDrainQualified"] = all(row["knownPersistedSpendDrainQualified"] for row in profiles)
        result["inputEvidence"] = {"snapshotSha256": sha256(args.snapshot), "expectationSha256": sha256(args.expectation)}
        result["analysisIdentity"] = analysis_identity()
        write(args.output, result)
        return 0 if result["financialInvariantsPassed"] else 2
    except (EvidenceError, KeyError, TypeError, ValueError, OSError) as error:
        write(args.output, {"schemaVersion": 1, "financialInvariantsPassed": False, "evidenceError": str(error)})
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
