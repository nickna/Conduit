# Unresolved provider usage and pricing

When a chat provider stream ends without usage and bounded estimation fails or
times out, Gateway marks accounting indeterminate. Completed function results are
retained before their SSE notification is attempted, including failed notification
writes and cancellation. A notification does not determine whether an invocation
incurred a cost.

Pricing failures use the same reconciliation path for streaming and nonstreaming
requests. Each provider call and provider-hosted tool is priced independently;
successful prices remain known even when another call or tool has no price. A
failed complete-price calculation is recorded as unknown, rather than zero. The
request log records the known total, and function metadata uses null `llmCost` or
`providerToolCost` for an unresolved component, with `knownLlmCost` or
`knownProviderToolCost` identifying its independently priced portion. Response
summaries omit monetary totals that cannot be established completely.

For requests admitted with a parent spend reservation, finalization atomically
queues cumulative known provider, hosted-tool and function spend and retains the unused reservation as an
invocation-started hold. For example, a 0.25 reservation with 0.05 known function
cost queues 0.05 and holds 0.20. The held 0.20 is a bound, not a measured provider
charge. The request remains open and indeterminate; ordinary settlement and
reservation release cannot discard that state.
For example, known provider calls of 0.05, configured hosted tools of 0.03, and
functions of 0.05 accept 0.13 once and retain 0.12 of a 0.25 reservation when another
provider call or hosted tool cannot be priced. No unresolved price is substituted
with the reservation bound or a different call's reported price.

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
Reservations now bind their original Virtual Key atomically in
`spend_reservation_owners:group:{groupId}`, indexed by request ID. Reserve replays,
invocation start, settlement, release, reconciliation recording, and typed queries
verify that identity. Ownership survives started, indeterminate, and settled states,
including zero-cost holds. Ordinary pre-invocation release or expiry removes the
owner; settled owners remain for the same replay horizon as their settlement records.

The immutable original maximum is stored separately in
`spend_reservation_bounds:group:{groupId}`. Reserve replay requires both the same
owner and the same original maximum, even when known spend has reduced the held
amount or the request has settled. Decimal values with equivalent numeric values
match. A changed maximum is rejected without changing the hold or accepting a new
invocation bound. Bound metadata follows owner retention and pre-invocation cleanup.

Legacy numeric holds and settled hashes remain readable without a data-format
migration, but ordinary lifecycle operations reject records with no owner. They
never infer ownership from a supplied sibling key in the same group. Before recovering
a legacy record, confirm its original Virtual Key and request ID from request audit
evidence, and check any existing reconciliation record, whose key identity cannot be
replaced. Restricted operator tooling may then call
`IBatchSpendUpdateService.RecoverLegacySpendReservationOwnerAsync(keyId, requestId,
auditActor, auditNote)`. Provide the operator identity and a note identifying the
verified request/provider or ledger evidence. This only binds ownership: it does
not invoke a provider, change the held amount, extend its expiry, or queue a charge.

Recovery returns `Recovered` once, or `AlreadyRecovered` for the same owner and
exact audit actor/note. An absent record or an expired pre-invocation hold returns
`Missing`. Conflicting ownership, an existing reconciliation key, or different audit
evidence returns `Conflict` without changes. A normally owned reservation cannot be
relabelled through recovery. The same immutable recovery evidence can explicitly
restore a missing owner, returning `Recovered` while retaining the original audit
timestamp; conflicting ownership or evidence still fails closed.
Legacy records with no original-bound metadata continue to reject reserve replay
after ownership recovery. Start, settlement, and audited reconciliation remain
available to complete their existing accounting. Do not infer the original maximum
from a reduced hold, add a fabricated bound, or replay the provider invocation.
The immutable recovery hash `spend_reservation_owner_recovery:group:{groupId}` retains
the owner, actor, evidence note, and observation time. Back up ownership and recovery
hashes and original-bound metadata with reservation state and retain recovery evidence
for the replay/audit horizon.

Drain older processes before enabling owner-enforcing lifecycle operations: old
writers do not maintain ownership metadata and old code does not reject sibling
keys. Inventory existing holds and recover verified legacy records before normal
settlement or release. Unverified started holds remain held for investigation;
unstarted legacy holds may expire through the ordinary reservation cleanup.

- With authoritative provider evidence, calculate only the unresolved provider
  portion using the request's applicable pricing and call reconciliation with
  `AuthoritativeProviderCost`, that amount, the operator identity, and a note
  identifying the evidence. Exclude every provider, hosted-tool and function
  amount already included in the record's cumulative known spend. Supplying the
  full original request cost would charge those known portions again.
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
known provider, hosted-tool and function costs through the normal spend path and
still mark accounting indeterminate; they have no parent hold to
reconcile.
