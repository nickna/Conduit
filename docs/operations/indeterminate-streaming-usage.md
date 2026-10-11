# Unresolved streaming provider usage

When a chat provider stream ends without usage and bounded estimation fails or
times out, Gateway marks accounting indeterminate. Completed function results are
retained before their SSE notification is attempted, including failed notification
writes and cancellation. A notification does not determine whether an invocation
incurred a cost.

For requests admitted with a parent spend reservation, finalization atomically
queues cumulative known function spend and retains the unused reservation as an
invocation-started hold. For example, a 0.25 reservation with 0.05 known function
cost queues 0.05 and holds 0.20. The held 0.20 is a bound, not a measured provider
charge. The request remains open and indeterminate; ordinary settlement and
reservation release cannot discard that state.

The atomic transition persists a reconciliation record in the Redis hash
`spend_reservations_indeterminate:group:{groupId}`, indexed by the billing request
ID returned in `x-request-id`. Records include the Virtual Key identity, known
spend, held amount, original billing window, failure reason, observation time,
and disposition/audit data when resolved. Neither these records nor started
holds expire. Configure Redis persistence and backups with the same requirements
as the pending spend queue. A service restart preserves the record, hold, and
exactly-once acceptance of known spend. Retrying finalization does not invoke the
function or provider again.

To inspect unresolved requests, enumerate this hash and select `resolved=false`;
use `IBatchSpendUpdateService.GetSpendReservationReconciliationAsync(keyId,
requestId)` to read a typed record. Restricted operator tooling can resolve a
record through `ReconcileSpendReservationAsync` after verifying its request and
provider evidence. There is no automatic inference of missing provider charges.
Before creating the first reconciliation record, confirm the original Virtual Key
and request ID from request audit evidence: the older reservation lifecycle stores
holds by group/request ID and does not retain their owning key. Once recorded,
reconciliation enforces that recorded key identity.

- With authoritative provider evidence, calculate only the unresolved provider
  portion using the request's applicable pricing and call reconciliation with
  `AuthoritativeProviderCost`, that amount, the operator identity, and a note
  identifying the evidence. Exclude the function cost already accepted.
- When provider usage cannot be established, an authorized operator may make an
  audited disposition with `ProviderCostWaived`, amount `0`, the operator identity,
  and a reason. This waives the unresolved portion and preserves the known charge.

Reconciliation atomically queues confirmed provider cost in the original billing
window, removes the remaining hold, records full settled spend, and retains the
resolved audit record. A replay with the same amount and audit disposition returns
`AlreadySettled`. A conflicting amount, identity, or audit disposition returns
`Conflict` without a further debit. Neither path repeats the original invocation.
`Conflict` can also mean that a concurrent known-spend update made the inspected
snapshot stale. Read the record again, verify the new evidence, and retry the same
audited resolution when it remains appropriate.
The usual batch flush commits queued spend to the ledger. Retain resolved records
for the same replay and audit horizon as settled reservation records; deleting
them requires a separate reviewed retention process.

If persistence is unavailable, finalization logs a critical failure and keeps the
started reservation held. Restore Redis and recover from request/provider audit
evidence before attempting an audited disposition. Do not delete holds directly,
bill the reservation remainder, or replay a completed invocation to reconstruct
usage. Billing admission modes that did not create a reservation continue to bill
known function cost through the normal spend path; they have no parent hold to
reconcile.
