WITH scope_keys AS (
 SELECT "Id", "VirtualKeyGroupId" FROM "VirtualKeys" WHERE "KeyName"='Native AOT parity key'
), scope_groups AS (
 SELECT g.* FROM "VirtualKeyGroups" g WHERE g."Id" IN (SELECT "VirtualKeyGroupId" FROM scope_keys)
), logs AS (
 SELECT r.* FROM "RequestLogs" r WHERE r."VirtualKeyId" IN (SELECT "Id" FROM scope_keys)
), ledger AS (
 SELECT t.* FROM "VirtualKeyGroupTransactions" t WHERE t."VirtualKeyGroupId" IN (SELECT "Id" FROM scope_groups)
)
SELECT json_build_object(
 'database',current_database(), 'readOnly',current_setting('transaction_read_only'),
 'keyCount',(SELECT count(*) FROM scope_keys), 'groupCount',(SELECT count(*) FROM scope_groups),
 'keyId',(SELECT min("Id") FROM scope_keys), 'groupId',(SELECT min("Id") FROM scope_groups),
 'logCount',(SELECT count(*) FROM logs), 'logCost',(SELECT coalesce(sum("Cost"),0)::text FROM logs),
 'unbilledOrNonpositiveLogs',(SELECT count(*) FROM logs WHERE "BilledAtUtc" IS NULL OR "Cost"<=0),
 'ledgerDebit',(SELECT coalesce(sum("Amount") FILTER(WHERE "TransactionType"=2 AND NOT "IsDeleted"),0)::text FROM ledger),
 'deletedOrUnsupportedLedger',(SELECT count(*) FROM ledger WHERE "IsDeleted" OR "TransactionType" NOT IN (1,2,3)),
 'balance',(SELECT "Balance"::text FROM scope_groups),
 'spent',(SELECT "LifetimeSpent"::text FROM scope_groups),
 'credits',(SELECT "LifetimeCreditsAdded"::text FROM scope_groups));
