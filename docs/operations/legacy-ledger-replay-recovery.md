# Recover one verified historical batch-spend replay

A ledger debit recorded with the historical `numeric(18,6)` amount can conflict
with its original eight-decimal pending claim after schema widening. The old
rounded amount does not uniquely identify the original adjustment. Batch ledger
descriptions record contributing key IDs or counts, not the original amount;
current aggregate balances also cannot establish that amount.

The additive `AddExactOriginalBalanceAdjustmentEvidence` migration leaves old
rows unverified. New idempotent writes atomically retain their unsigned exact
`OriginalAmount` in `numeric(20,8)`, preserving twelve integral digits. Inputs
with more than eight decimal places are rejected before the debit. The replay
validator compares the exact original amount and all existing group, direction,
reference and billing-window identity fields. Without original evidence it
continues to require exact equality to the recorded ledger amount.

This migration works with both the historical six-decimal ledger and the
expanded eight-decimal reporting schema from #1574. Apply the release migration
before deploying the new stores. It does not restore old reporting precision.

## Verify the original operation

Keep the existing Redis processing claim and its `batch-spend:<claim-id>` key.
Obtain contemporaneous evidence of the original adjustment payload (exact amount,
claim ID, group and billing window) and independent evidence that this ledger
row committed that operation before claim acknowledgement. Record the incident
or evidence reference and the accountable operator. A current claim alone,
an amount from the same rounding bucket, or an aggregate/request total without
an unambiguous operation correlation does not establish the original payload.
If that evidence is unavailable, leave the claim pending for investigation.

Read the exact ledger ID, idempotency key, group, transaction/reference types,
reference ID, nullable billing window, creation timestamp, stored amount and
balance after. The approval script requires this entire snapshot to match the
locked row. It only permits active system batch debits whose exact original
amount rounds to the old stored value at six decimals and actually differs from
it. The script does not infer the missing amount.

## Record the bounded approval

Use a trusted PostgreSQL operator connection. Supply every variable below from
the verified operation and ledger snapshot; an empty billing-window string means
SQL NULL. This example uses illustrative values:

```powershell
psql -X --set=ON_ERROR_STOP=1 $env:CONDUIT_RECOVERY_DATABASE_URL `
  --set=ledger_id=123 --set=group_id=7 `
  --set=idempotency_key=batch-spend:verified-claim --set=reference_id=verified-claim `
  --set=transaction_type=2 --set=reference_type=3 `
  --set=billing_window_start_utc=2026-10-01T12:00:00Z `
  --set=created_at_utc=2026-10-01T12:01:02.123456Z `
  --set=balance_after=99.875000 --set=stored_amount=0.125000 `
  --set=original_amount=0.12500001 --set=verified_by=operator-identity `
  --set=evidence_reference=incident-1607-verified-original-operation `
  --file=scripts/db/approve-legacy-ledger-replay.sql
```

The transaction writes only `OriginalAmount`, `OriginalAmountVerifiedBy`,
`OriginalAmountEvidence` and `OriginalAmountVerifiedAtUtc` on that one ledger row.
The historical `Amount`, `BalanceAfter`, group balance/spend, claim and key remain
unchanged. A mismatched snapshot or existing different evidence aborts. Repeating
the identical approval is a no-op; it preserves the first actor, evidence and
timestamp. Row locking serializes concurrent approvals and bounds lock and query
waits. The audit stays on the original durable ledger row.

Then let the normal flusher retry the original claim. Both EF and typed Npgsql
stores recognize that exact original adjustment as already applied, return
`Applied = false`, and allow the normal acknowledgement to remove the durable
claim. A changed amount, including another amount with the same six-decimal
rounding, remains a conflict. Verify one ledger row, unchanged balance/spend, the
recorded approval audit and removal of the original processing claim. Keep the
external evidence record with the incident.
