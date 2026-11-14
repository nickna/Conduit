# Duration precision in billing

Audio and video durations remain `double` values in public usage contracts. At the
point where a duration becomes a decimal billable quantity,
`BillingDurationConversion.ToDecimal` preserves the precision policy used by the
.NET 10 release: format with 15 significant digits using invariant culture, then
parse as a decimal. Token counts, decimal provider costs and decimal rates do not
pass through this conversion.

.NET 11 changed floating-point-to-decimal casts to round the exact binary value.
Microsoft documents invariant `G15` formatting followed by decimal parsing as the
compatibility approach for the previous `double` behavior. This matters even when
the public duration is unchanged: binary tails can change which side of a monetary
rounding midpoint a calculated amount occupies. See the
[official .NET 11 conversion guidance](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/decimal-biginteger-floating-point-conversions).

For example, 0.3 video seconds at $0.00000005 per second previously produced
exactly $0.000000015. Existing batch spending rounds that amount away from zero to
two units of $0.00000001. A direct .NET 11 cast produces an amount just below that
midpoint and one spend unit. Historical refunds have their own unchanged rounding:
refunding 0.3 of one second from a recorded $0.00000005 charge rounds to $0.00000002
with `decimal.Round(amount, 8)` (ties to even); the direct .NET 11 cast yields
$0.00000001 instead.

The compatibility helper is used by audio transcription pricing, per-second video
pricing, video quantities in pricing rules, and audio/video historical refund
ratios. Their formulas and operation order stay the same. The helper also rejects
nonfinite values and the exclusive double boundaries at positive and negative
2^96 before formatting. Those boundaries previously overflowed, but `G15` alone
can round them into the decimal range. Zero and underflow results are normalized
to the previous canonical decimal zero.

An isolated paired-runtime investigation compared identical double input bits
under .NET 10.0.15 and .NET 11.0.0. In a deliberately midpoint-focused cohort of
822 pricing and 1,024 historical refund expressions, direct casts changed 370
quantized charges and 15 quantized refunds, by at most one $0.00000001 unit each.
The compatibility conversion matched all 1,846 previous raw monetary results and
quantized results. These counts describe the selected test cohort, not production
incident frequency. Probe source and receipts are retained under
`artifacts/dotnet11-phase1/numerical-compat`.

`BillingDurationCompatibilityTests` checks exact audio/video/rules costs through
the real batch-spending Redis argument conversion, historical refund midpoint
results, nonbinary durations, decimal-range boundaries, nonfinite inputs,
underflow, and canonical zero. Service and finite-conversion cases repeat under
English, French and Arabic cultures. Expected money is supplied as decimal data,
so the compiler's own floating-point conversion cannot change the expectation.
