using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Gateway.Billing;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Http.Billing;

public class SpendReservationServiceTests
{
    [Fact]
    public async Task ReserveMapsInsufficientBalanceWithoutThrowing()
    {
        var batch = new Mock<IBatchSpendUpdateService>();
        batch.Setup(x => x.TryReserveSpendAsync(7, 1.25m, "request-1"))
            .ReturnsAsync(false);
        var service = new SpendReservationService(
            batch.Object,
            Mock.Of<ILogger<SpendReservationService>>());

        var result = await service.ReserveAsync(7, "request-1", 1.25m);

        Assert.Equal(SpendReservationOutcome.InsufficientBalance, result.Outcome);
    }

    [Fact]
    public async Task SettleDelegatesToAtomicBatchSettlement()
    {
        var expected = new SpendReservationSettlementResult(
            SpendReservationSettlementStatus.Settled,
            0.75m);
        var batch = new Mock<IBatchSpendUpdateService>();
        batch.Setup(x => x.SettleSpendReservationAsync(7, "request-2", 0.75m, null))
            .ReturnsAsync(expected);
        var service = new SpendReservationService(
            batch.Object,
            Mock.Of<ILogger<SpendReservationService>>());

        var result = await service.SettleAsync(7, "request-2", 0.75m);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task RecordKnownIndeterminateSpend_DelegatesKnownAmountAndOriginalTimestamp()
    {
        var billedAt = new DateTime(2026, 10, 10, 23, 59, 0, DateTimeKind.Utc);
        var expected = new SpendReservationSettlementResult(SpendReservationSettlementStatus.Settled, 0.05m);
        var batch = new Mock<IBatchSpendUpdateService>();
        batch.Setup(x => x.RecordIndeterminateSpendReservationAsync(7, "partial", 0.05m, "usage missing", billedAt))
            .ReturnsAsync(expected);
        var service = new SpendReservationService(batch.Object, Mock.Of<ILogger<SpendReservationService>>());

        Assert.Same(expected, await service.RecordKnownIndeterminateSpendAsync(
            7, "partial", 0.05m, "usage missing", billedAt));
        batch.VerifyAll();
    }

    [Fact]
    public async Task MarkIndeterminate_PersistsEvidenceWithoutReplacingAcceptedKnownAmount()
    {
        var batch = new Mock<IBatchSpendUpdateService>();
        batch.Setup(x => x.RecordIndeterminateSpendReservationAsync(7, "partial", null, "usage missing", null))
            .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.AlreadySettled, 0.05m));
        var service = new SpendReservationService(batch.Object, Mock.Of<ILogger<SpendReservationService>>());

        await service.MarkIndeterminateAsync(7, "partial", "usage missing");

        batch.VerifyAll();
    }

    [Fact]
    public async Task MarkIndeterminate_PersistenceNotAccepted_ThrowsInsteadOfReportingSuccess()
    {
        var batch = new Mock<IBatchSpendUpdateService>();
        batch.Setup(x => x.RecordIndeterminateSpendReservationAsync(7, "partial", null, "usage missing", null))
            .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.Missing, 0m));
        var service = new SpendReservationService(batch.Object, Mock.Of<ILogger<SpendReservationService>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MarkIndeterminateAsync(7, "partial", "usage missing"));
    }
}
