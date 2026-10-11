# Billing correctness alerting

Conduit runs an internal cost canary for every enabled model mapping. The canary does not call a
provider or debit a virtual key; it sends synthetic positive usage through the same model-cost lookup
and calculation service used by request billing.

## Configuration

Set these environment variables on the Admin and Grafana services:

```text
BillingCostCanary__Enabled=true
BillingCostCanary__IntervalMinutes=5
# Optional: enables outbound Grafana notifications when set
CONDUIT_ALERT_WEBHOOK_URL=https://your-alert-receiver.example/conduit
```

`CONDUIT_ALERT_WEBHOOK_URL` is optional. Grafana always provisions and evaluates the supplied alert
rules. When the variable is set, Grafana also provisions the `conduit-oncall` webhook contact point
and notification policy. When it is unset or empty, outbound webhook notifications are disabled and
firing alerts remain visible in Grafana.

The supplied Grafana rule treats a canary older than 15 minutes as stale. If the interval is increased,
update the stale threshold in `grafana/provisioning/alerting/billing-alerting-rules.yml` as well.

## Alerts

- `Billing Revenue Loss Event` fires for any revenue-loss counter increase.
- `Unexpected Zero-Cost Billing` fires when a request is calculated at zero cost.
- `Model Cost Canary Failed` fires when an active mapping has no usable cost, invalid pricing JSON,
  an expired/inactive cost, a calculation error, or a non-positive result.
- `Model Cost Canary Stale` fires when the canary is absent or has not completed for 15 minutes.
- `Token Counting Degraded to Character Heuristic` fires when any token estimate falls back to the
  chars/4 heuristic (`conduit_token_count_estimates_total{fidelity="character_heuristic"}`). That
  only happens when tokenizer vocabulary data cannot be loaded, which means spend reservations and
  streaming-fallback billing are running on estimates that under-count by 20-40%. Larger safety
  buffers are applied automatically on this tier, but the underlying cause — look for
  `Failed to load encoding` in Gateway/Admin logs — should be fixed promptly.

## Token-count fidelity metric

`conduit_token_count_estimates_total{fidelity=...}` counts every token estimate by how it was
produced: `exact` (the model's own vocabulary), `approximate_vocabulary` (a documented tiktoken
stand-in, normal for non-OpenAI models), or `character_heuristic` (vocabulary unavailable — the
alarm condition above). The exact-to-approximate ratio is expected to reflect your provider mix;
`character_heuristic` is expected to be permanently zero.

A count also lands in `approximate_vocabulary` when the request contains an image whose geometry
cannot be determined without fetching it (a remote `image_url` rather than a base64 data URL):
the image is charged a conservative high-detail default instead of the exact tile formula, so
vision-heavy traffic raises this tier's share. That is expected behaviour, not an alarm.

## Response

1. Query `BillingAuditEvents` for `ModelCostCanaryFailed` and note the mapping ID, model-cost ID,
   pricing model, and normalized reason in `MetadataJson`.
2. Confirm that the mapping, provider, and provider-type association should remain enabled.
3. Restore a positive, active ModelCost whose effective/expiry dates cover the current time and whose
   pricing configuration passes validation.
4. Wait for the next canary run and confirm `conduit_billing_cost_canary_status` returns to `1` and the
   Grafana alert resolves.
5. For production revenue-loss events, reconcile affected request logs and ledger entries before
   closing the incident.

## Financial precision and historical reconciliation

The `PreserveEightDecimalFinancialAmounts` migration retains eight fractional digits in request
costs, spend history, billing and pricing audit costs, ledger amounts, and ledger balances. It widens
`numeric(10,6)` to `numeric(12,8)` and `numeric(18,6)` to `numeric(20,8)`, preserving the existing four
and twelve integral digits. Group balances and function execution costs already retain eight digits.

The migration preserves historical stored values. PostgreSQL already rounded earlier reporting
rows to six digits; widening a column cannot recover the discarded digits. Reconciliation across
the upgrade must retain that rounding as historical uncertainty and record the migration cutover
time with the release evidence. Delayed writes and pending spend can mix old six-digit request
rows with new eight-digit ledger rows in the same billing window; treat those transition windows
as historical too. Compare fully settled windows written under the expanded schema at eight digits.
For earlier rows, corroborate discrepancies with execution amounts or provider evidence before making a correction;
the rounded log or ledger value alone does not establish the original charge. Do not rewrite
historical rows or debit customers merely to make aggregate totals match. Any verified adjustment
should use a separate, audited ledger entry. Existing reconciliation thresholds remain unchanged.

Replaying an old idempotent debit can also conflict when its ledger amount was rounded before the
upgrade. Retain the pending claim and its original idempotency key until the committed debit is
verified against independent evidence. Giving the same claim a new key can debit the customer twice.

An application rollback can keep these wider columns. Reverting this migration's schema rounds
new eight-decimal rows to six digits again; preserve the wider schema when rolling back code.

`conduit_billing_revenue_loss_dollars_total` currently counts revenue-loss events; despite its legacy
name, its value is not a dollar estimate.
