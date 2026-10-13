using System.Net;
using System.Text.Json;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ConduitLLM.Tests.Functions.Services;

public sealed class FunctionExecutionServicePersistenceTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ProviderHttpFailure_UpdatesPersistedExecutionWithUpstreamStatus(HttpStatusCode status)
    {
        await using var environment = await FunctionExecutionTestEnvironment.CreateAsync(status);
        var exception = await Assert.ThrowsAsync<FunctionCommunicationException>(() =>
            environment.CreateService().ExecuteAsync(7, 1, new Dictionary<string, object> { ["query"] = "test" }));

        await using var db = environment.Database.CreateContext();
        var persisted = await db.FunctionExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(ExecutionState.Failed, persisted.State);
        Assert.Equal(exception.Message, persisted.ErrorMessage);
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(0.25m, persisted.EstimatedCost);
        Assert.Equal(0m, persisted.ActualCost);
        Assert.NotNull(persisted.CompletedAt);
        Assert.Equal(2, persisted.Version);
        using var details = JsonDocument.Parse(persisted.CostCalculationDetails);
        Assert.Equal((int)status, details.RootElement.GetProperty("httpStatusCode").GetInt32());
        Assert.Equal(1, environment.ProviderRequests);

        // A fresh service/repository/context reads the saved status rather than the original entity.
        var loaded = await environment.CreateService().GetExecutionAsync(persisted.Id);
        Assert.Equal(persisted.ErrorMessage, loaded.ErrorMessage);
        Assert.Equal(persisted.CostCalculationDetails, loaded.CostCalculationDetails);
        Assert.Equal(FunctionProviderType.Exa, loaded.FunctionConfiguration.ProviderType);
    }

    [Fact]
    public async Task CommunicationFailureBeforeExecutionCreation_PersistsStatusOnFallbackRecord()
    {
        await using var environment = await FunctionExecutionTestEnvironment.CreateAsync();
        var diagnostic = new FunctionCommunicationException("Exa", FunctionExecutionTestEnvironment.Diagnostic,
            HttpStatusCode.ServiceUnavailable, FunctionExecutionTestEnvironment.Diagnostic);
        environment.Costs.Setup(service => service.EstimateCostAsync(
                7, It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(diagnostic);

        var exception = await Assert.ThrowsAsync<FunctionCommunicationException>(() =>
            environment.CreateService().ExecuteAsync(7, 1, new Dictionary<string, object> { ["query"] = "test" }));

        await using var db = environment.Database.CreateContext();
        var persisted = await db.FunctionExecutions.AsNoTracking().SingleAsync();
        Assert.Same(diagnostic, exception);
        Assert.Equal(ExecutionState.Failed, persisted.State);
        Assert.Equal(FunctionExecutionTestEnvironment.Diagnostic, persisted.ErrorMessage);
        Assert.Equal(0m, persisted.ActualCost);
        Assert.Equal(1, persisted.Version);
        using var details = JsonDocument.Parse(persisted.CostCalculationDetails);
        Assert.Equal(503, details.RootElement.GetProperty("httpStatusCode").GetInt32());
        Assert.Equal(0, environment.ProviderRequests);
    }
}
