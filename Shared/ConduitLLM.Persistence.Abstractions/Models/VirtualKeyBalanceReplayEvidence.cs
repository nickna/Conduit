namespace ConduitLLM.Persistence;

/// <summary>Strict replay validation for exact adjustments and older rounded ledger rows.</summary>
public static class VirtualKeyBalanceReplayEvidence
{
    /// <summary>
    /// A rounded ledger value proves a replay only when its separately recorded exact
    /// original amount matches. Rows without that evidence retain exact comparison.
    /// </summary>
    public static bool AmountMatches(decimal storedAmount, decimal? originalAmount, decimal requestedAmount)
    {
        var expected = Math.Abs(requestedAmount);
        return originalAmount.HasValue
            ? originalAmount.Value == expected &&
                (storedAmount == expected ||
                 storedAmount == Math.Round(expected, 6, MidpointRounding.AwayFromZero))
            : storedAmount == expected;
    }

    /// <summary>Prevent rounding the exact original amount on new idempotent writes.</summary>
    public static void ValidateAmount(decimal amount, string? idempotencyKey)
    {
        if (idempotencyKey is not null && amount != Math.Round(amount, 8))
        {
            throw new ArgumentOutOfRangeException(nameof(amount),
                "Idempotent balance adjustments support at most eight decimal places.");
        }
    }
}
