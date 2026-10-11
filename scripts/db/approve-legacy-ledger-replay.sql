-- Execute with psql -X --set=ON_ERROR_STOP=1 and every named variable supplied.
-- This authorizes ONE verified historical batch debit. It never changes balances,
-- the historical Amount, or the claim/idempotency key. See the recovery runbook.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
CREATE TEMP TABLE legacy_replay_approval_input ON COMMIT DROP AS SELECT
    :'ledger_id'::bigint AS ledger_id,
    :'idempotency_key'::text AS idempotency_key,
    :'group_id'::integer AS group_id,
    :'transaction_type'::integer AS transaction_type,
    :'reference_type'::integer AS reference_type,
    :'reference_id'::text AS reference_id,
    NULLIF(:'billing_window_start_utc', '')::timestamptz AS billing_window_start_utc,
    :'created_at_utc'::timestamptz AS created_at_utc,
    :'balance_after'::numeric AS balance_after,
    :'stored_amount'::numeric AS stored_amount,
    :'original_amount'::numeric AS original_amount,
    :'verified_by'::text AS verified_by,
    :'evidence_reference'::text AS evidence_reference;

DO $approval$
DECLARE
    expected pg_temp.legacy_replay_approval_input%ROWTYPE;
    existing "VirtualKeyGroupTransactions"%ROWTYPE;
    affected integer;
BEGIN
    SELECT * INTO STRICT expected FROM pg_temp.legacy_replay_approval_input;
    IF expected.transaction_type <> 2 OR expected.reference_type <> 3 OR
       expected.idempotency_key <> 'batch-spend:' || expected.reference_id OR
       expected.reference_id !~ '[^[:space:]]' OR
       expected.verified_by !~ '[^[:space:]]' OR length(expected.verified_by) > 100 OR
       expected.evidence_reference !~ '[^[:space:]]' OR length(expected.evidence_reference) > 500 OR
       expected.original_amount <= 0 OR expected.original_amount >= 1000000000000 OR
       expected.original_amount <> trunc(expected.original_amount, 8) OR
       expected.original_amount = expected.stored_amount OR
       round(expected.original_amount, 6) <> expected.stored_amount THEN
        RAISE EXCEPTION 'Invalid bounded legacy batch-debit approval evidence';
    END IF;

    SELECT * INTO STRICT existing FROM "VirtualKeyGroupTransactions"
    WHERE "Id" = expected.ledger_id FOR UPDATE;
    IF existing."IdempotencyKey" IS DISTINCT FROM expected.idempotency_key OR
       existing."VirtualKeyGroupId" IS DISTINCT FROM expected.group_id OR
       existing."TransactionType" IS DISTINCT FROM expected.transaction_type OR
       existing."ReferenceType" IS DISTINCT FROM expected.reference_type OR
       existing."ReferenceId" IS DISTINCT FROM expected.reference_id OR
       existing."BillingWindowStartUtc" IS DISTINCT FROM expected.billing_window_start_utc OR
       existing."CreatedAt" IS DISTINCT FROM expected.created_at_utc OR
       existing."BalanceAfter" IS DISTINCT FROM expected.balance_after OR
       existing."Amount" IS DISTINCT FROM expected.stored_amount OR
       existing."IsDeleted" OR existing."DeletedAt" IS NOT NULL THEN
        RAISE EXCEPTION 'Ledger identity changed or does not match the verified historical debit';
    END IF;

    IF existing."OriginalAmount" IS NOT NULL THEN
        IF existing."OriginalAmount" = expected.original_amount AND
           existing."OriginalAmountVerifiedBy" = expected.verified_by AND
           existing."OriginalAmountEvidence" = expected.evidence_reference AND
           existing."OriginalAmountVerifiedAtUtc" IS NOT NULL THEN
            RAISE NOTICE 'Identical historical replay approval already recorded for ledger %', expected.ledger_id;
            RETURN;
        END IF;
        RAISE EXCEPTION 'Existing original-amount evidence cannot be replaced';
    END IF;
    IF existing."OriginalAmountVerifiedBy" IS NOT NULL OR
       existing."OriginalAmountEvidence" IS NOT NULL OR
       existing."OriginalAmountVerifiedAtUtc" IS NOT NULL THEN
        RAISE EXCEPTION 'Incomplete existing approval evidence requires investigation';
    END IF;

    UPDATE "VirtualKeyGroupTransactions" SET
        "OriginalAmount" = expected.original_amount,
        "OriginalAmountVerifiedBy" = expected.verified_by,
        "OriginalAmountEvidence" = expected.evidence_reference,
        "OriginalAmountVerifiedAtUtc" = CURRENT_TIMESTAMP
    WHERE "Id" = expected.ledger_id AND "OriginalAmount" IS NULL;
    GET DIAGNOSTICS affected = ROW_COUNT;
    IF affected <> 1 THEN
        RAISE EXCEPTION 'Expected exactly one historical ledger approval, updated %', affected;
    END IF;
END;
$approval$;
COMMIT;
