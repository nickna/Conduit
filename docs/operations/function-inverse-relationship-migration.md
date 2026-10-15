# Function inverse relationship migration

`20261015050045_PairFunctionInverseRelationships` pairs the cost mapping and
execution audit inverse collections with their existing explicit foreign keys:

| Child table | Canonical foreign key | Removed shadow foreign key |
| --- | --- | --- |
| `FunctionCostMappings` | `FunctionCostId` | `FunctionCostId1` |
| `FunctionExecutionAudits` | `FunctionExecutionId` | `FunctionExecutionId1` |

The canonical columns already require a valid parent and cannot be null. A null
shadow value means the child was visible through its explicit relationship only.
An equal shadow value names the same parent through both legacy relationships.
Both cases migrate without changing any child row or canonical reference. The
canonical cascade constraints and the unique configuration/cost mapping index
remain in place.

## Preflight and conflicting references

Run these read-only checks before deployment:

```sql
SELECT "Id", "FunctionConfigurationId", "FunctionCostId", "FunctionCostId1"
FROM "FunctionCostMappings"
WHERE "FunctionCostId1" IS NOT NULL
  AND "FunctionCostId1" IS DISTINCT FROM "FunctionCostId";

SELECT "Id", "FunctionExecutionId", "FunctionExecutionId1"
FROM "FunctionExecutionAudits"
WHERE "FunctionExecutionId1" IS NOT NULL
  AND "FunctionExecutionId1" IS DISTINCT FROM "FunctionExecutionId";
```

A populated shadow value that names a different parent represents two distinct
relationships. The migration cannot determine which one was intended. It takes
transactional exclusive locks on both child tables, checks both relationships,
and aborts with PostgreSQL error `23514` before removing any column if either
check finds a conflict. The failed migration preserves the schema, row contents,
and migration history. Locks also prevent concurrent legacy writes from
introducing a conflict between validation and removal.

Reconcile each conflict with the owner of the function configuration or audit
record. Preserve the affected records and the reconciliation decision in the
normal operational audit process. If the canonical parent is correct, set the
shadow reference to that canonical value or null. If the shadow parent is
correct, update the canonical reference after checking its effects on pricing,
audit ownership, and the unique configuration/cost mapping constraint, then make
the shadow reference agree. If both cost associations are required, preserve them
as separate valid mappings rather than discarding an association. Audit events
require an explicit ownership decision; the migration does not duplicate or
reassign them automatically.

Apply only the reviewed row-specific changes in a transaction, repeat the
preflight checks, and rerun the release migrator. Do not clear every shadow
reference to bypass the check: that would silently discard unresolved
relationships. Stop legacy writers during deployment so they cannot attempt to
use removed columns after the migration completes. The exclusive locks briefly
block access to these two tables while the migration runs.

## Rollback

The down migration recreates the nullable shadow columns, their indexes, and the
original foreign key constraints. It backfills every shadow reference from the
canonical reference, including children created after the upgrade. Legacy
inverse collection loading therefore retains every current relationship after
rollback. The historical distinction between a null and equal shadow value is
not reconstructed; all restored shadow references equal the canonical parent.

Use the standard release migration tooling and verify the resulting schema
version. Changes to these relationships also require regenerating the checked-in
compiled EF model using the commands in
[the NativeAOT feature matrix](gateway-native-aot-feature-matrix.md).
