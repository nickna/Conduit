-- Read-only fixture snapshot. No request bodies, credentials, key hashes or raw metadata.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '15s';
WITH scope_keys AS (
  SELECT "Id", "VirtualKeyGroupId" FROM "VirtualKeys" WHERE "KeyName" = 'Native AOT parity key'
), scope_groups AS (
  SELECT g.* FROM "VirtualKeyGroups" g WHERE g."Id" IN (SELECT "VirtualKeyGroupId" FROM scope_keys)
), requests AS (
  SELECT r.* FROM "RequestLogs" r WHERE r."VirtualKeyId" IN (SELECT "Id" FROM scope_keys)
), ledger AS (
  SELECT t.* FROM "VirtualKeyGroupTransactions" t WHERE t."VirtualKeyGroupId" IN (SELECT "Id" FROM scope_groups)
), history AS (
  SELECT h.* FROM "VirtualKeySpendHistory" h WHERE h."VirtualKeyId" IN (SELECT "Id" FROM scope_keys)
), duplicate_keys AS (
  SELECT "IdempotencyKey", count(*) AS n FROM ledger WHERE "IdempotencyKey" IS NOT NULL GROUP BY "IdempotencyKey" HAVING count(*) > 1
), duplicate_request_billing_ids AS (
  SELECT "Metadata"->>'billingRequestId' AS id, count(*) AS n FROM requests
  WHERE "Metadata"->>'billingRequestId' IS NOT NULL GROUP BY "Metadata"->>'billingRequestId' HAVING count(*) > 1
), ordered_ledger AS (
  SELECT *, lag("BalanceAfter") OVER (PARTITION BY "VirtualKeyGroupId" ORDER BY "Id") AS previous_balance FROM ledger
)
SELECT json_build_object(
  'schemaVersion',1,'capturedAtUtc',clock_timestamp(),'database',current_database(),
  'transactionReadOnly',current_setting('transaction_read_only'),'postgresVersion',current_setting('server_version'),
  'keys',coalesce((SELECT json_agg(json_build_object('id',"Id",'groupId',"VirtualKeyGroupId") ORDER BY "Id") FROM scope_keys),'[]'::json),
  'groups',coalesce((SELECT json_agg(json_build_object('id',"Id",'balance',"Balance"::text,'lifetimeSpent',"LifetimeSpent"::text,'lifetimeCreditsAdded',"LifetimeCreditsAdded"::text) ORDER BY "Id") FROM scope_groups),'[]'::json),
  'pricing',coalesce((SELECT json_agg(json_build_object('id',"Id",'inputPerMillion',"InputCostPerMillionTokens"::text,'outputPerMillion',"OutputCostPerMillionTokens"::text,'audioPerMinute',"AudioCostPerMinute"::text)) FROM "ModelCosts" WHERE "Id"=-9005),'[]'::json),
  'requestTotals',(SELECT json_build_object('count',count(*),'cost',coalesce(sum("Cost"),0)::text,'inputTokens',coalesce(sum("InputTokens"),0),'outputTokens',coalesce(sum("OutputTokens"),0),'zeroCostRows',count(*) FILTER(WHERE "Cost"=0),'billedRows',count(*) FILTER(WHERE "BilledAtUtc" IS NOT NULL),'metadataBillingIds',count(*) FILTER(WHERE "Metadata"->>'billingRequestId' IS NOT NULL)) FROM requests),
  'requestCohorts',coalesce((SELECT json_agg(row_to_json(c)) FROM (SELECT "RequestType" AS "requestType","StatusCode" AS status,"InputTokens" AS "inputTokens","OutputTokens" AS "outputTokens","Cost"::text AS cost,count(*) AS count FROM requests GROUP BY "RequestType","StatusCode","InputTokens","OutputTokens","Cost" ORDER BY "RequestType","StatusCode","Cost") c),'[]'::json),
  'requestMetadataShape',coalesce((SELECT json_agg(json_build_object('jsonType',kind,'count',n)) FROM (SELECT jsonb_typeof("Metadata") AS kind,count(*) AS n FROM requests GROUP BY jsonb_typeof("Metadata")) s),'[]'::json),
  'requests',coalesce((SELECT json_agg(json_build_object('id',"Id",'virtualKeyId',"VirtualKeyId",'timestamp',"Timestamp",'billedAtUtc',"BilledAtUtc",'requestType',"RequestType",'status',"StatusCode",'cost',"Cost"::text,'inputTokens',"InputTokens",'outputTokens',"OutputTokens",'billingRequestId',"Metadata"->>'billingRequestId') ORDER BY "Id") FROM requests),'[]'::json),
  'ledgerTotals',(SELECT json_build_object('count',count(*),'debit',coalesce(sum("Amount") FILTER(WHERE "TransactionType"=2 AND NOT "IsDeleted"),0)::text,'credit',coalesce(sum("Amount") FILTER(WHERE "TransactionType"=1 AND NOT "IsDeleted"),0)::text,'refund',coalesce(sum("Amount") FILTER(WHERE "TransactionType"=3 AND NOT "IsDeleted"),0)::text,'deletedRows',count(*) FILTER(WHERE "IsDeleted"),'unhandledTypes',count(*) FILTER(WHERE "TransactionType" NOT IN (1,2,3)),'duplicateIdempotencyKeys',(SELECT count(*) FROM duplicate_keys),'missingOriginalAmountForIdempotent',count(*) FILTER(WHERE "IdempotencyKey" IS NOT NULL AND "OriginalAmount" IS NULL),'originalAmountMismatches',count(*) FILTER(WHERE "OriginalAmount" IS NOT NULL AND "OriginalAmount"<>"Amount")) FROM ledger),
  'ledger',coalesce((SELECT json_agg(json_build_object('id',"Id",'groupId',"VirtualKeyGroupId",'type',"TransactionType",'amount',"Amount"::text,'originalAmount',"OriginalAmount"::text,'balanceAfter',"BalanceAfter"::text,'idempotencyKey',"IdempotencyKey",'billingWindowStartUtc',"BillingWindowStartUtc",'createdAt',"CreatedAt",'isDeleted',"IsDeleted") ORDER BY "Id") FROM ledger),'[]'::json),
  'ledgerContinuityMismatches',(SELECT count(*) FROM ordered_ledger WHERE previous_balance IS NOT NULL AND "TransactionType" IN (1,2,3) AND "BalanceAfter"<>previous_balance+CASE WHEN "TransactionType"=2 THEN -"Amount" ELSE "Amount" END),
  'duplicateRequestMetadataBillingIds',(SELECT count(*) FROM duplicate_request_billing_ids),
  'historyTotals',(SELECT json_build_object('count',count(*),'amount',coalesce(sum("Amount"),0)::text) FROM history),
  'billingAuditEventCounts',coalesce((SELECT json_agg(row_to_json(a)) FROM (SELECT "EventType" AS "eventType","HttpStatusCode" AS status,count(*) AS count FROM "BillingAuditEvents" WHERE "VirtualKeyId" IN (SELECT "Id" FROM scope_keys) GROUP BY "EventType","HttpStatusCode" ORDER BY "EventType","HttpStatusCode") a),'[]'::json)
);
COMMIT;
