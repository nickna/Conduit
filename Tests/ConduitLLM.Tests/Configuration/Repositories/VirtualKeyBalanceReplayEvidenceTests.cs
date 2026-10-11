using System.Globalization;

using ConduitLLM.Persistence;

namespace ConduitLLM.Tests.Configuration.Repositories;

public sealed class VirtualKeyBalanceReplayEvidenceTests
{
    [Theory]
    [InlineData("0.125000", null, "-0.12500001", false)]
    [InlineData("0.125000", null, "-0.125000", true)]
    [InlineData("0.125000", "0.12500001", "-0.12500001", true)]
    [InlineData("0.125000", "0.12500001", "-0.12500002", false)]
    [InlineData("0.12500001", "0.12500001", "-0.12500001", true)]
    [InlineData("0.12500002", "0.12500001", "-0.12500001", false)]
    [InlineData("0.125000", "0.12500050", "-0.12500050", false)]
    [InlineData("0.125001", "0.12500050", "-0.12500050", true)]
    [InlineData("0", "0.00000001", "-0.00000001", true)]
    [InlineData("0", null, "-0.00000001", false)]
    public void AmountMatches_ExactEvidenceAndPostgresRoundingBoundaries_PreserveStrictIdentity(
        string stored, string? original, string requested, bool expected)
    {
        Assert.Equal(expected, VirtualKeyBalanceReplayEvidence.AmountMatches(
            decimal.Parse(stored, CultureInfo.InvariantCulture),
            original is null ? null : decimal.Parse(original, CultureInfo.InvariantCulture),
            decimal.Parse(requested, CultureInfo.InvariantCulture)));
    }
}
