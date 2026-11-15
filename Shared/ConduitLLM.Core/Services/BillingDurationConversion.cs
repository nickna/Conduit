using System.Globalization;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Keeps duration-based charges and historical refunds on their published precision policy.
/// </summary>
internal static class BillingDurationConversion
{
    // Decimal.MaxValue rounds to 2^96 as a double. The previous conversion rejected
    // this boundary before rounding to 15 significant digits; G15 parsing alone does not.
    private const double ExclusiveDecimalLimit = 79228162514264337593543950336d;

    public static decimal ToDecimal(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds >= ExclusiveDecimalLimit || seconds <= -ExclusiveDecimalLimit)
        {
            throw new OverflowException("The duration is outside the range of Decimal.");
        }

        // .NET 11 casts retain the binary double's extra digits. The documented G15
        // compatibility policy preserves .NET 10 quantities before existing monetary
        // formulas and rounding, without allocating a temporary formatted string.
        Span<char> formatted = stackalloc char[32];
        if (!seconds.TryFormat(formatted, out var length, "G15", CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("The duration could not be formatted.");
        }

        var quantity = decimal.Parse(formatted[..length], NumberStyles.Float, CultureInfo.InvariantCulture);
        return quantity == 0m ? 0m : quantity;
    }
}
