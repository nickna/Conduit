using System.Text.Json;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Functions.Services;

public sealed class FunctionExecutionServiceRuntimeStoreTests
{
    [Fact]
    public void ExistingPublicRepositoryConstructorRemainsTheOnlyPublicConstructor()
    {
        var constructor = typeof(FunctionExecutionService).GetConstructor(
        [
            typeof(IFunctionConfigurationRepository), typeof(IFunctionCredentialRepository),
            typeof(IFunctionExecutionRepository), typeof(IFunctionCostCalculationService),
            typeof(IFunctionClientFactory), typeof(IFunctionCredentialProtector),
            typeof(ILogger<FunctionExecutionService>)
        ]);
        Assert.NotNull(constructor);
        Assert.Single(typeof(FunctionExecutionService).GetConstructors());
    }

    [Theory]
    [InlineData("configurationStore")]
    [InlineData("writer")]
    [InlineData("reader")]
    [InlineData("costCalculationService")]
    [InlineData("clientFactory")]
    [InlineData("credentialProtector")]
    [InlineData("logger")]
    public void RuntimeFactoryRejectsNullCollaboratorsWithTheirParameterNames(string parameter)
    {
        using var fixture = new Fixture(true);
        var error = Assert.Throws<ArgumentNullException>(() => FunctionExecutionService.CreateWithRuntimePersistence(
            parameter == "configurationStore" ? null : fixture.RuntimeConfiguration.Object,
            parameter == "writer" ? null : fixture.Writer,
            parameter == "reader" ? null : fixture.RuntimeReader.Object,
            parameter == "costCalculationService" ? null : fixture.Calculator.Object,
            parameter == "clientFactory" ? null : fixture.ClientFactory.Object,
            parameter == "credentialProtector" ? null : fixture.Protector.Object,
            parameter == "logger" ? null : NullLogger<FunctionExecutionService>.Instance));
        Assert.Equal(parameter, error.ParamName);
        Assert.Empty(fixture.Events);
        fixture.AssertBoundary(0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullParametersFailBeforeAnyPersistenceOrProviderWork(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Service.ExecuteAsync(
            Fixture.ConfigurationId, Fixture.VirtualKeyId, null, cancellationToken: fixture.Token));
        Assert.Empty(fixture.Events);
        fixture.AssertBoundary(0, 0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ProviderResultsPreserveRequestCostWireDataAndInlineExecutionMode(bool runtime, bool success)
    {
        using var fixture = new Fixture(runtime);
        fixture.Configuration.DefaultExecutionMode = ExecutionMode.Asynchronous;
        fixture.Result.IsSuccess = success;
        fixture.Result.ErrorMessage = success ? null : "provider rejected request";
        fixture.Result.HttpStatusCode = success ? 200 : 429;
        var metadata = Dictionary("""{"trace":"trace-52","nested":{"enabled":true},"optional":null}""");
        var result = await fixture.ExecuteAsync(metadata: metadata);

        Assert.Equal(
            new[] { "configuration", "estimate", "create", "credentials", "reveal", "factory", "provider", "usage", "cost", "update" },
            fixture.Events);
        var created = Assert.Single(fixture.Writer.Creates).Snapshot;
        Assert.Equal(ExecutionState.Running, created.State);
        Assert.Equal(ExecutionMode.Asynchronous, created.ExecutionMode);
        Assert.Null(created.CompletedAt);
        Assert.Null(created.ResponseJson);
        Assert.Null(created.ActualCost);
        Assert.Equal(1, created.Version);
        AssertExecutionDefaults(created);
        Assert.Equal(created.Id, result.Id);
        Assert.Equal(success ? ExecutionState.Completed : ExecutionState.Failed, result.State);
        Assert.Equal(ExecutionMode.Asynchronous, result.ExecutionMode);
        Assert.Equal(fixture.Result.ResponseJson, result.ResponseJson);
        Assert.Equal(fixture.Result.ErrorMessage, result.ErrorMessage);
        Assert.Equal(Fixture.Estimate, result.EstimatedCost);
        Assert.Equal(Fixture.ActualCost, result.ActualCost);
        Assert.NotNull(result.CompletedAt);
        Assert.NotNull(result.StartedAt);
        Assert.NotNull(result.Duration);
        Assert.True(result.RequestedAt <= result.StartedAt);
        Assert.True(result.StartedAt <= result.CompletedAt);
        Assert.Equal(2, result.Version);
        Assert.Same(result, Assert.Single(fixture.Writer.Updates).Input);
        Assert.Same(fixture.Parameters, fixture.ProviderParameters);
        Assert.Same(fixture.Parameters, fixture.UsageParameters);
        AssertRequest(result.RequestJson, fixture.Parameters, metadata, null);
        Assert.Equal(created.RequestJson, result.RequestJson);
        AssertCost(result.CostCalculationDetails, Fixture.Estimate, Fixture.ActualCost, fixture.Result.HttpStatusCode);
        AssertExecutionDefaults(result);
        fixture.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnkeyedCallsRemainIndependentAndOmitNullMetadata(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        var first = await fixture.ExecuteAsync();
        var second = await fixture.ExecuteAsync();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, fixture.ProviderCalls);
        Assert.Equal(2, fixture.Writer.Creates.Count);
        AssertRequest(first.RequestJson, fixture.Parameters, null, null);
        AssertRequest(second.RequestJson, fixture.Parameters, null, null);
        fixture.AssertBoundary(2, 2);

        using var omitted = new Fixture(runtime);
        var result = await omitted.ExecuteAsync();
        AssertRequest(result.RequestJson, omitted.Parameters, null, null);
        omitted.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyedCallsWithoutADurableStoreFailClosedBeforeProviderOrPersistenceWork(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        var error = await Assert.ThrowsAsync<FunctionIdempotencyException>(() => fixture.ExecuteAsync("requires-durable-claim"));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("idempotency_unavailable", error.Code);
        Assert.Equal(Guid.Empty, error.ExecutionId);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.ProviderCalls);
        fixture.AssertBoundary(0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidKeysAreRejectedBeforeAnyPersistenceOrProviderWork(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        foreach (var key in new[] { "", "   ", new string('k', 201) })
        {
            var error = await Assert.ThrowsAsync<FunctionIdempotencyException>(() => fixture.ExecuteAsync(key));
            Assert.Equal(400, error.StatusCode);
            Assert.Equal("invalid_idempotency_key", error.Code);
            Assert.Equal(Guid.Empty, error.ExecutionId);
        }
        Assert.Empty(fixture.Events);
        fixture.AssertBoundary(0, 0);
    }

    [Theory]
    [InlineData(false, "scoped-primary", "opaque-scoped-primary")]
    [InlineData(true, "scoped-primary", "opaque-scoped-primary")]
    [InlineData(false, "scoped-first", "opaque-scoped-first")]
    [InlineData(true, "scoped-first", "opaque-scoped-first")]
    [InlineData(false, "global-primary", "opaque-global-primary")]
    [InlineData(true, "global-primary", "opaque-global-primary")]
    [InlineData(false, "global-first", "opaque-global-first")]
    [InlineData(true, "global-first", "opaque-global-first")]
    public async Task CredentialTierAndMcpToolInjectionPreserveOpaqueSecretsAndOriginalUsageArguments(
        bool runtime, string tier, string selectedSecret)
    {
        using var fixture = new Fixture(runtime);
        fixture.Configuration.ProviderType = FunctionProviderType.Mcp;
        fixture.Credentials =
        [
            Credential("opaque-other-owner", Fixture.ConfigurationId + 1, true),
            Credential("opaque-disabled", Fixture.ConfigurationId, true, false),
            Credential("opaque-global-first", null, false),
            Credential("opaque-global-primary", null, true)
        ];
        if (tier.StartsWith("scoped", StringComparison.Ordinal))
        {
            fixture.Credentials.Add(Credential("opaque-scoped-first", Fixture.ConfigurationId, false));
            if (tier == "scoped-primary")
                fixture.Credentials.Add(Credential("opaque-scoped-primary", Fixture.ConfigurationId, true));
        }
        if (tier == "global-first")
            fixture.Credentials.RemoveAll(value => value.ApiKey == "opaque-global-primary");
        fixture.Parameters[McpReservedParameterKeys.ToolName] = "model-supplied-value";
        var original = JsonSerializer.Serialize(fixture.Parameters, FunctionsJsonContext.Default.DictionaryStringObject);

        var result = await fixture.ExecuteAsync(providerToolName: "server.search");

        Assert.Equal(new[] { selectedSecret }, fixture.RevealedSecrets);
        Assert.Equal("revealed:" + selectedSecret, fixture.ProviderSecret);
        Assert.NotSame(fixture.Parameters, fixture.ProviderParameters);
        Assert.Equal("server.search", fixture.ProviderParameters[McpReservedParameterKeys.ToolName]);
        Assert.Same(fixture.Parameters, fixture.UsageParameters);
        Assert.Equal("model-supplied-value", fixture.Parameters[McpReservedParameterKeys.ToolName]);
        Assert.Equal(original, JsonSerializer.Serialize(fixture.Parameters, FunctionsJsonContext.Default.DictionaryStringObject));
        AssertRequest(result.RequestJson, fixture.Parameters, null, null);
        Assert.All(fixture.Credentials, value => Assert.StartsWith("opaque-", value.ApiKey));
        fixture.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "pricing")]
    [InlineData(true, "pricing")]
    [InlineData(false, "credentials")]
    [InlineData(true, "credentials")]
    [InlineData(false, "disabled-credentials")]
    [InlineData(true, "disabled-credentials")]
    public async Task PreProviderRejectionsRecordAnUnbilledFailureAndRethrow(bool runtime, string failure)
    {
        using var fixture = new Fixture(runtime);
        var message = ConfigureRejection(fixture, failure);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync());
        Assert.Equal(message, error.Message);
        var early = failure is "missing" or "disabled" or "pricing";
        var recorded = early
            ? Assert.Single(fixture.Writer.Creates).Snapshot
            : Assert.Single(fixture.Writer.Updates).Snapshot;
        Assert.Equal(ExecutionState.Failed, recorded.State);
        Assert.Equal(0m, recorded.ActualCost);
        Assert.Equal(message, recorded.ErrorMessage);
        Assert.Null(recorded.ResponseJson);
        Assert.Null(recorded.CostCalculationDetails);
        Assert.NotNull(recorded.CompletedAt);
        AssertRequest(recorded.RequestJson, fixture.Parameters, null, null);
        if (early)
        {
            Assert.Null(recorded.StartedAt);
            Assert.Null(recorded.EstimatedCost);
            Assert.Equal(ExecutionMode.Synchronous, recorded.ExecutionMode);
            Assert.Empty(fixture.Writer.Updates);
        }
        else
        {
            Assert.Equal(Fixture.Estimate, recorded.EstimatedCost);
            Assert.Equal(ExecutionState.Running, Assert.Single(fixture.Writer.Creates).Snapshot.State);
            Assert.Equal(2, recorded.Version);
        }
        Assert.Equal(0, fixture.ProviderCalls);
        Assert.Empty(fixture.RevealedSecrets);
        fixture.AssertBoundary(1, early ? 0 : 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ErrorRecordPersistenceFailureNeverReplacesTheOriginalPreProviderException(bool runtime, bool providerStarted)
    {
        using var fixture = new Fixture(runtime);
        var original = new InvalidOperationException("original execution failure");
        var persistence = new InvalidOperationException("error recording failed");
        if (providerStarted)
        {
            fixture.ProviderError = original;
            fixture.Writer.UpdateError = (_, _) => persistence;
        }
        else
        {
            fixture.ConfigurationError = original;
            fixture.Writer.CreateError = (_, _) => persistence;
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync());
        Assert.Same(original, error);
        var attempt = providerStarted ? Assert.Single(fixture.Writer.Updates) : Assert.Single(fixture.Writer.Creates);
        Assert.Equal(0m, attempt.Snapshot.ActualCost);
        Assert.Equal(original.Message, attempt.Snapshot.ErrorMessage);
        AssertRequest(attempt.Snapshot.RequestJson, fixture.Parameters, null, null);
        Assert.Equal(providerStarted ? 1 : 0, fixture.ProviderCalls);
        fixture.AssertBoundary(1, providerStarted ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialCreateStillAttemptsAnUnbilledUpdateAndDoesNotCallTheProvider(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        var original = new InvalidOperationException("create failed");
        fixture.Writer.CreateError = (_, _) => original;
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync()));
        var created = Assert.Single(fixture.Writer.Creates);
        var updated = Assert.Single(fixture.Writer.Updates);
        Assert.Same(created.Input, updated.Input);
        Assert.Equal(ExecutionState.Running, created.Snapshot.State);
        Assert.Equal(ExecutionState.Failed, updated.Snapshot.State);
        Assert.Equal(0m, updated.Snapshot.ActualCost);
        Assert.Equal(2, updated.Input.Version);
        Assert.Equal(0, fixture.ProviderCalls);
        fixture.AssertBoundary(1, 0);
    }

    [Theory]
    [InlineData(false, "usage")]
    [InlineData(true, "usage")]
    [InlineData(false, "cost")]
    [InlineData(true, "cost")]
    [InlineData(false, "update")]
    [InlineData(true, "update")]
    [InlineData(false, "both-updates")]
    [InlineData(true, "both-updates")]
    public async Task PostProviderBookkeepingFailuresReturnThePreservedBillableResult(bool runtime, string failure)
    {
        using var fixture = new Fixture(runtime);
        var original = new InvalidOperationException("bookkeeping failed: " + failure);
        if (failure == "usage") fixture.UsageError = original;
        if (failure == "cost") fixture.CostError = original;
        if (failure is "update" or "both-updates")
            fixture.Writer.UpdateError = (attempt, _) => attempt == 1 || failure == "both-updates" ? original : null;

        var result = await fixture.ExecuteAsync();

        var costWasCalculated = failure is "update" or "both-updates";
        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.Equal(original.Message, result.ErrorMessage);
        Assert.Equal(costWasCalculated ? Fixture.ActualCost : Fixture.Estimate, result.ActualCost);
        Assert.Equal(Fixture.Estimate, result.EstimatedCost);
        Assert.Equal(fixture.Result.ResponseJson, result.ResponseJson);
        Assert.NotNull(result.CompletedAt);
        Assert.NotNull(result.Duration);
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Equal(costWasCalculated ? 2 : 1, fixture.Writer.Updates.Count);
        Assert.Equal(failure == "both-updates" ? 1 : 2, result.Version);
        if (costWasCalculated)
        {
            Assert.Equal(ExecutionState.Completed, fixture.Writer.Updates[0].Snapshot.State);
            AssertCost(result.CostCalculationDetails, Fixture.Estimate, Fixture.ActualCost, 200);
        }
        else
            Assert.Null(result.CostCalculationDetails);
        Assert.Equal(ExecutionState.Failed, fixture.Writer.Updates[^1].Snapshot.State);
        Assert.Same(result, fixture.Writer.Updates[^1].Input);
        fixture.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false, "configuration")]
    [InlineData(true, "configuration")]
    [InlineData(false, "provider")]
    [InlineData(true, "provider")]
    [InlineData(false, "cost")]
    [InlineData(true, "cost")]
    [InlineData(false, "update")]
    [InlineData(true, "update")]
    public async Task CancellationBeforeAndAfterProviderCompletionKeepsTheExistingBillingBoundary(bool runtime, string stage)
    {
        using var source = new CancellationTokenSource();
        using var fixture = new Fixture(runtime, source.Token);
        var cancellation = new OperationCanceledException("caller cancelled", source.Token);
        if (stage == "configuration")
        {
            source.Cancel();
            fixture.ConfigurationError = cancellation;
            fixture.Writer.CreateError = (_, token) => new OperationCanceledException(token);
        }
        else if (stage == "provider")
        {
            fixture.ProviderAction = source.Cancel;
            fixture.ProviderError = cancellation;
            fixture.Writer.UpdateError = (_, token) => new OperationCanceledException(token);
        }
        else
        {
            fixture.ProviderAction = source.Cancel;
            if (stage == "cost") fixture.CostError = cancellation;
            fixture.Writer.UpdateError = (_, _) => cancellation;
        }

        if (stage is "configuration" or "provider")
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.ExecuteAsync());
            Assert.Same(cancellation, error);
            var attempt = stage == "configuration" ? Assert.Single(fixture.Writer.Creates) : Assert.Single(fixture.Writer.Updates);
            Assert.Equal(0m, attempt.Snapshot.ActualCost);
            Assert.Equal(ExecutionState.Failed, attempt.Snapshot.State);
        }
        else
        {
            var result = await fixture.ExecuteAsync();
            Assert.Equal(ExecutionState.Failed, result.State);
            Assert.Equal(stage == "cost" ? Fixture.Estimate : Fixture.ActualCost, result.ActualCost);
            Assert.Equal(fixture.Result.ResponseJson, result.ResponseJson);
            Assert.Equal(cancellation.Message, result.ErrorMessage);
            Assert.Equal(1, result.Version);
        }
        Assert.True(source.IsCancellationRequested);
        Assert.All(fixture.Writer.Creates.Concat(fixture.Writer.Updates), attempt => Assert.Equal(source.Token, attempt.Token));
        fixture.AssertBoundary(1, stage == "configuration" ? 0 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FalseFinalUpdateReturnsAnExplicitFailedBillableResultWithoutRepeatingProviderOrPersistence(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        fixture.Writer.UpdateResult = false;
        var result = await fixture.ExecuteAsync();
        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.Equal(Fixture.ActualCost, result.ActualCost);
        Assert.Equal(Fixture.Estimate, result.EstimatedCost);
        Assert.Equal(fixture.Result.ResponseJson, result.ResponseJson);
        AssertCost(result.CostCalculationDetails, Fixture.Estimate, Fixture.ActualCost, fixture.Result.HttpStatusCode);
        AssertBillableConcurrencyDiagnostic(result);
        Assert.Equal(1, result.Version);
        Assert.NotNull(result.CompletedAt);
        var attempt = Assert.Single(fixture.Writer.Updates);
        Assert.Equal(ExecutionState.Completed, attempt.Snapshot.State);
        Assert.Same(result, attempt.Input);
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Single(fixture.Writer.Creates);
        AssertConflictLogged(fixture, result.Id);
        fixture.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FalseErrorRecordBeforeProviderCompletionPreservesTheOriginalException(bool runtime, bool providerStarted)
    {
        using var fixture = new Fixture(runtime);
        var original = new InvalidOperationException("original pre-completion failure");
        if (providerStarted) fixture.ProviderError = original;
        else fixture.Protector.Setup(value => value.Reveal(It.IsAny<string>())).Throws(original);
        fixture.Writer.UpdateResult = false;

        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync()));

        var attempt = Assert.Single(fixture.Writer.Updates);
        Assert.Equal(ExecutionState.Failed, attempt.Snapshot.State);
        Assert.Equal(0m, attempt.Snapshot.ActualCost);
        Assert.Equal(original.Message, attempt.Snapshot.ErrorMessage);
        Assert.Null(attempt.Snapshot.ResponseJson);
        Assert.Equal(1, attempt.Input.Version);
        Assert.Equal(providerStarted ? 1 : 0, fixture.ProviderCalls);
        Assert.Single(fixture.Writer.Creates);
        AssertConflictLogged(fixture, attempt.Input.Id);
        fixture.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false, "usage")]
    [InlineData(true, "usage")]
    [InlineData(false, "cost")]
    [InlineData(true, "cost")]
    [InlineData(false, "update")]
    [InlineData(true, "update")]
    public async Task FalseErrorRecordAfterProviderCompletionPreservesIncurredCostAndReportsBothFailures(bool runtime, string stage)
    {
        using var fixture = new Fixture(runtime);
        var original = new InvalidOperationException("original bookkeeping failure: " + stage);
        if (stage == "usage") fixture.UsageError = original;
        if (stage == "cost") fixture.CostError = original;
        if (stage == "update") fixture.Writer.UpdateError = (attempt, _) => attempt == 1 ? original : null;
        fixture.Writer.UpdateResult = false;

        var result = await fixture.ExecuteAsync();

        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.Equal(stage == "update" ? Fixture.ActualCost : Fixture.Estimate, result.ActualCost);
        Assert.Equal(Fixture.Estimate, result.EstimatedCost);
        Assert.Equal(fixture.Result.ResponseJson, result.ResponseJson);
        Assert.Contains(original.Message, result.ErrorMessage, StringComparison.Ordinal);
        AssertBillableConcurrencyDiagnostic(result);
        Assert.Equal(1, result.Version);
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Equal(stage == "update" ? 2 : 1, fixture.Writer.Updates.Count);
        Assert.Equal(ExecutionState.Failed, fixture.Writer.Updates[^1].Snapshot.State);
        if (stage == "update") AssertCost(result.CostCalculationDetails, Fixture.Estimate, Fixture.ActualCost, fixture.Result.HttpStatusCode);
        else Assert.Null(result.CostCalculationDetails);
        Assert.Single(fixture.Writer.Creates);
        AssertConflictLogged(fixture, result.Id);
        fixture.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingMcpPricingDefaultsBothCostsToZeroAndStillExecutes(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        fixture.Configuration.ProviderType = FunctionProviderType.Mcp;
        fixture.Credentials[0].ProviderType = FunctionProviderType.Mcp;
        fixture.EstimateError = new InvalidOperationException("no optional MCP estimate");
        fixture.CostError = new InvalidOperationException("no optional MCP actual pricing");
        var result = await fixture.ExecuteAsync();
        Assert.Equal(ExecutionState.Completed, result.State);
        Assert.Equal(0m, result.EstimatedCost);
        Assert.Equal(0m, result.ActualCost);
        AssertCost(result.CostCalculationDetails, 0m, 0m, 200);
        Assert.Equal(1, fixture.ProviderCalls);
        fixture.AssertBoundary(1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsPreserveIdsTokensGraphsOrderingMissingRowsAndReaderExceptions(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        var first = new FunctionExecution { Id = Guid.NewGuid(), State = ExecutionState.Failed };
        var second = new FunctionExecution { Id = Guid.NewGuid(), State = ExecutionState.Pending };
        first.FunctionConfiguration = fixture.Configuration;
        fixture.Configuration.Executions.Add(first);
        var rows = new List<FunctionExecution> { second, first };
        var cancelled = new OperationCanceledException(fixture.Token);
        fixture.ExecutionRepository.Setup(value => value.GetByIdAsync(first.Id, fixture.Token)).ReturnsAsync(first);
        fixture.ExecutionRepository.Setup(value => value.GetByIdAsync(Guid.Empty, fixture.Token)).ReturnsAsync(value: null);
        fixture.ExecutionRepository.Setup(value => value.GetByVirtualKeyIdAsync(Fixture.VirtualKeyId, fixture.Token)).ReturnsAsync(rows);
        fixture.ExecutionRepository.Setup(value => value.GetByVirtualKeyIdAsync(-1, fixture.Token)).ThrowsAsync(cancelled);
        fixture.RuntimeReader.Setup(value => value.GetByIdAsync(first.Id, fixture.Token)).ReturnsAsync(first);
        fixture.RuntimeReader.Setup(value => value.GetByIdAsync(Guid.Empty, fixture.Token)).ReturnsAsync(value: null);
        fixture.RuntimeReader.Setup(value => value.ListByVirtualKeyAsync(Fixture.VirtualKeyId, fixture.Token)).ReturnsAsync(rows);
        fixture.RuntimeReader.Setup(value => value.ListByVirtualKeyAsync(-1, fixture.Token)).ThrowsAsync(cancelled);

        Assert.Same(first, await fixture.Service.GetExecutionAsync(first.Id, fixture.Token));
        Assert.Null(await fixture.Service.GetExecutionAsync(Guid.Empty, fixture.Token));
        var listed = await fixture.Service.ListExecutionsAsync(Fixture.VirtualKeyId, fixture.Token);
        Assert.Equal(2, listed.Count);
        Assert.Same(second, listed[0]);
        Assert.Same(first, listed[1]);
        Assert.Same(first, Assert.Single(listed[1].FunctionConfiguration.Executions));
        Assert.Same(cancelled, await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Service.ListExecutionsAsync(-1, fixture.Token)));
        Assert.Empty(fixture.Writer.Creates);
        Assert.Empty(fixture.Writer.Updates);
        Assert.Empty(fixture.Events);
        if (runtime)
        {
            fixture.RuntimeReader.Verify(value => value.GetByIdAsync(first.Id, fixture.Token), Times.Once);
            fixture.RuntimeReader.Verify(value => value.GetByIdAsync(Guid.Empty, fixture.Token), Times.Once);
            fixture.RuntimeReader.Verify(value => value.ListByVirtualKeyAsync(Fixture.VirtualKeyId, fixture.Token), Times.Once);
            fixture.RuntimeReader.Verify(value => value.ListByVirtualKeyAsync(-1, fixture.Token), Times.Once);
            fixture.RuntimeReader.VerifyNoOtherCalls();
            fixture.ExecutionRepository.VerifyNoOtherCalls();
        }
        else
        {
            fixture.ExecutionRepository.Verify(value => value.GetByIdAsync(first.Id, fixture.Token), Times.Once);
            fixture.ExecutionRepository.Verify(value => value.GetByIdAsync(Guid.Empty, fixture.Token), Times.Once);
            fixture.ExecutionRepository.Verify(value => value.GetByVirtualKeyIdAsync(Fixture.VirtualKeyId, fixture.Token), Times.Once);
            fixture.ExecutionRepository.Verify(value => value.GetByVirtualKeyIdAsync(-1, fixture.Token), Times.Once);
            fixture.ExecutionRepository.VerifyNoOtherCalls();
            fixture.RuntimeReader.VerifyNoOtherCalls();
        }
        fixture.ConfigurationRepository.VerifyNoOtherCalls();
        fixture.CredentialRepository.VerifyNoOtherCalls();
        fixture.RuntimeConfiguration.VerifyNoOtherCalls();
        fixture.Calculator.VerifyNoOtherCalls();
        fixture.ClientFactory.VerifyNoOtherCalls();
        fixture.Client.VerifyNoOtherCalls();
        fixture.Protector.VerifyNoOtherCalls();
    }

    private static string ConfigureRejection(Fixture fixture, string failure)
    {
        switch (failure)
        {
            case "missing":
                fixture.Configuration = null;
                return $"Function configuration {Fixture.ConfigurationId} not found";
            case "disabled":
                fixture.Configuration.IsEnabled = false;
                return $"Function configuration {Fixture.ConfigurationId} is not enabled";
            case "pricing":
                fixture.EstimateError = new InvalidOperationException("missing required pricing");
                return fixture.EstimateError.Message;
            case "credentials":
                fixture.Credentials.Clear();
                return "No credentials configured for provider type Exa";
            case "disabled-credentials":
                fixture.Credentials =
                [
                    Credential("opaque-disabled", null, true, false, FunctionProviderType.Exa),
                    Credential("opaque-other-owner", Fixture.ConfigurationId + 1, true, provider: FunctionProviderType.Exa)
                ];
                return "No enabled credentials found for provider type Exa";
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    private static void AssertExecutionDefaults(FunctionExecution execution)
    {
        Assert.NotEqual(Guid.Empty, execution.Id);
        Assert.Equal(Fixture.ConfigurationId, execution.FunctionConfigurationId);
        Assert.Equal(Fixture.VirtualKeyId, execution.VirtualKeyId);
        Assert.Equal(0, execution.RetryCount);
        Assert.Null(execution.NextRetryAt);
        Assert.Null(execution.LeasedBy);
        Assert.Null(execution.LeaseExpiryTime);
        Assert.Null(execution.WebhookUrl);
        Assert.False(execution.WebhookDelivered);
        Assert.Null(execution.ProgressPercentage);
        Assert.Null(execution.StatusMessage);
        Assert.Null(execution.FunctionConfiguration);
        Assert.Empty(execution.AuditEvents);
    }

    private static void AssertRequest(string json, Dictionary<string, object> parameters, Dictionary<string, object> metadata, string key)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        using var expectedParameters = JsonDocument.Parse(JsonSerializer.Serialize(parameters, FunctionsJsonContext.Default.DictionaryStringObject));
        Assert.True(JsonElement.DeepEquals(expectedParameters.RootElement, root.GetProperty("parameters")));
        Assert.Equal(metadata != null, root.TryGetProperty("metadata", out var actualMetadata));
        if (metadata != null)
        {
            using var expectedMetadata = JsonDocument.Parse(JsonSerializer.Serialize(metadata, FunctionsJsonContext.Default.DictionaryStringObject));
            Assert.True(JsonElement.DeepEquals(expectedMetadata.RootElement, actualMetadata));
        }
        Assert.Equal(key != null, root.TryGetProperty("idempotencyKey", out var actualKey));
        if (key != null) Assert.Equal(key, actualKey.GetString());
        Assert.Equal(1 + (metadata == null ? 0 : 1) + (key == null ? 0 : 1), root.EnumerateObject().Count());
    }

    private static void AssertCost(string json, decimal estimate, decimal actual, int? status)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(estimate, root.GetProperty("estimatedCost").GetDecimal());
        Assert.Equal(actual, root.GetProperty("actualCost").GetDecimal());
        Assert.Equal(status, root.GetProperty("httpStatusCode").GetInt32());
        var usage = root.GetProperty("usage");
        Assert.Equal(3, usage.GetProperty("result_count").GetInt32());
        Assert.Equal("neural", usage.GetProperty("search_type").GetString());
        Assert.Equal(7L, usage.GetProperty("metadata").GetProperty("attempt").GetInt64());
        Assert.True(usage.GetProperty("metadata").GetProperty("nested").GetProperty("cached").GetBoolean());
        Assert.False(usage.TryGetProperty("tokens_consumed", out _));
        Assert.False(usage.TryGetProperty("resultCount", out _));
        Assert.Equal("retained", usage.GetProperty("provider_extension").GetString());
        Assert.Equal(4, root.EnumerateObject().Count());
    }

    private static void AssertBillableConcurrencyDiagnostic(FunctionExecution execution)
    {
        Assert.Contains("concurrency", execution.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("billable", execution.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconcile", execution.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(execution.Id.ToString("D"), execution.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertConflictLogged(Fixture fixture, Guid executionId) =>
        Assert.Contains(fixture.Log.Entries, entry => entry.Level >= LogLevel.Warning &&
            entry.Message.Contains("persistence conflict", StringComparison.OrdinalIgnoreCase) &&
            entry.Message.Contains(executionId.ToString("D"), StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, object> Dictionary(string json) =>
        JsonSerializer.Deserialize(json, FunctionsJsonContext.Default.DictionaryStringObject);

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static FunctionCredential Credential(string secret, int? owner, bool primary, bool enabled = true,
        FunctionProviderType provider = FunctionProviderType.Mcp) => new()
    {
        ProviderType = provider,
        FunctionConfigurationId = owner,
        ApiKey = secret,
        IsPrimary = primary,
        IsEnabled = enabled
    };

    private sealed class Fixture : IDisposable
    {
        public const int ConfigurationId = 9027;
        public const int VirtualKeyId = 4819;
        public const decimal Estimate = 1.25m;
        public const decimal ActualCost = .875m;
        private readonly bool _runtime;
        private readonly CancellationTokenSource _source = new();
        public CancellationToken Token { get; }
        public FunctionExecutionService Service { get; }
        public FunctionConfiguration? Configuration { get; set; } = new()
        {
            Id = ConfigurationId, ConfigurationName = "unit-52-config", ProviderType = FunctionProviderType.Exa,
            IsEnabled = true, DefaultExecutionMode = ExecutionMode.Synchronous
        };
        public List<FunctionCredential> Credentials { get; set; } =
        [new() { ProviderType = FunctionProviderType.Exa, ApiKey = "opaque-global", IsEnabled = true, IsPrimary = true }];
        public Dictionary<string, object> Parameters { get; } = Dictionary(
            """{"query":"native fixture","numResults":3,"enabled":true,"filters":{"sites":["fixture.test"]},"optional":null}""");
        public FunctionExecutionResult Result { get; } = new()
        {
            IsSuccess = true, ResponseJson = """{"results":[{"id":"fixture-result"}],"optional":null}""",
            HttpStatusCode = 200, Duration = TimeSpan.FromDays(1)
        };
        public FunctionExecutionUsage Usage { get; } = new()
        {
            ResultCount = 3, SearchType = "neural", Metadata = new Dictionary<string, object>
            {
                ["attempt"] = 7L,
                ["nested"] = Element("""{"cached":true}""")
            },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["provider_extension"] = Element("\"retained\"")
            }
        };
        public List<string> Events { get; } = [];
        public List<string> RevealedSecrets { get; } = [];
        public Dictionary<string, object> ProviderParameters { get; private set; }
        public Dictionary<string, object> UsageParameters { get; private set; }
        public string ProviderSecret { get; private set; }
        public int ProviderCalls { get; private set; }
        public Exception ConfigurationError { get; set; }
        public Exception EstimateError { get; set; }
        public Exception ProviderError { get; set; }
        public Exception UsageError { get; set; }
        public Exception CostError { get; set; }
        public Action ProviderAction { get; set; }
        public RecordingWriter Writer { get; }
        public Mock<IFunctionConfigurationRepository> ConfigurationRepository { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionCredentialRepository> CredentialRepository { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionExecutionRepository> ExecutionRepository { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionExecutionConfigurationStore> RuntimeConfiguration { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionExecutionRuntimeReader> RuntimeReader { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionCostCalculationService> Calculator { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionClientFactory> ClientFactory { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionClient> Client { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionCredentialProtector> Protector { get; } = new(MockBehavior.Strict);
        public RecordingLogger Log { get; } = new();

        public Fixture(bool runtime, CancellationToken? token = null)
        {
            _runtime = runtime;
            Token = token ?? _source.Token;
            Writer = new RecordingWriter(Events);
            ConfigurationRepository.Setup(value => value.GetByIdAsync(ConfigurationId, Token)).Returns(ReadConfiguration);
            RuntimeConfiguration.Setup(value => value.GetConfigurationAsync(ConfigurationId, Token)).Returns(ReadConfiguration);
            CredentialRepository.Setup(value => value.GetByProviderTypeAsync(It.IsAny<FunctionProviderType>(), Token))
                .Returns((FunctionProviderType provider, CancellationToken _) =>
                {
                    Assert.Equal(Configuration.ProviderType, provider);
                    Events.Add("credentials");
                    return Task.FromResult(Credentials);
                });
            RuntimeConfiguration.Setup(value => value.ListCredentialsForProviderAsync(It.IsAny<FunctionProviderType>(), Token))
                .Returns((FunctionProviderType provider, CancellationToken _) =>
                {
                    Assert.Equal(Configuration.ProviderType, provider);
                    Events.Add("credentials");
                    return Task.FromResult<IReadOnlyList<FunctionCredential>>(Credentials);
                });
            ExecutionRepository.Setup(value => value.CreateAsync(It.IsAny<FunctionExecution>(), Token))
                .Returns((FunctionExecution execution, CancellationToken ct) => Writer.CreateAsync(execution, ct));
            ExecutionRepository.Setup(value => value.UpdateAsync(It.IsAny<FunctionExecution>(), Token))
                .Returns((FunctionExecution execution, CancellationToken ct) => Writer.UpdateAsync(execution, ct));
            Calculator.Setup(value => value.EstimateCostAsync(ConfigurationId, Parameters, Token)).Returns(() =>
            {
                Events.Add("estimate");
                return EstimateError == null ? Task.FromResult(Estimate) : Task.FromException<decimal>(EstimateError);
            });
            Protector.Setup(value => value.Reveal(It.IsAny<string>())).Returns((string secret) =>
            {
                Events.Add("reveal");
                RevealedSecrets.Add(secret);
                return "revealed:" + secret;
            });
            ClientFactory.Setup(value => value.GetClientAsync(It.IsAny<FunctionProviderType>(), ConfigurationId))
                .Returns((FunctionProviderType provider, int _) =>
                {
                    Assert.Equal(Configuration.ProviderType, provider);
                    Events.Add("factory");
                    return Task.FromResult(Client.Object);
                });
            Client.Setup(value => value.ExecuteAsync(It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(), Token))
                .Returns((Dictionary<string, object> parameters, string secret, CancellationToken _) =>
                {
                    Events.Add("provider");
                    ProviderCalls++;
                    ProviderParameters = parameters;
                    ProviderSecret = secret;
                    ProviderAction?.Invoke();
                    return ProviderError == null ? Task.FromResult(Result) : Task.FromException<FunctionExecutionResult>(ProviderError);
                });
            Client.Setup(value => value.CalculateUsageFromResponse(Parameters, Result)).Returns(() =>
            {
                Events.Add("usage");
                UsageParameters = Parameters;
                if (UsageError != null) throw UsageError;
                return Usage;
            });
            Calculator.Setup(value => value.CalculateCostAsync(ConfigurationId, Usage, Token)).Returns(() =>
            {
                Events.Add("cost");
                return CostError == null ? Task.FromResult(ActualCost) : Task.FromException<decimal>(CostError);
            });
            Service = runtime
                ? FunctionExecutionService.CreateWithRuntimePersistence(RuntimeConfiguration.Object, Writer, RuntimeReader.Object,
                    Calculator.Object, ClientFactory.Object, Protector.Object, Log)
                : new FunctionExecutionService(ConfigurationRepository.Object, CredentialRepository.Object, ExecutionRepository.Object,
                    Calculator.Object, ClientFactory.Object, Protector.Object, Log);
        }

        public Task<FunctionExecution> ExecuteAsync(string idempotencyKey = null, Dictionary<string, object> metadata = null, string providerToolName = null) =>
            Service.ExecuteAsync(ConfigurationId, VirtualKeyId, Parameters, idempotencyKey, metadata, providerToolName, Token);

        public void Dispose() => _source.Dispose();

        private Task<FunctionConfiguration?> ReadConfiguration()
        {
            Events.Add("configuration");
            return ConfigurationError == null ? Task.FromResult(Configuration) : Task.FromException<FunctionConfiguration?>(ConfigurationError);
        }

        public void AssertBoundary(int configurationReads, int credentialReads)
        {
            Assert.All(Writer.Creates.Concat(Writer.Updates), value => Assert.Equal(Token, value.Token));
            if (_runtime)
            {
                RuntimeConfiguration.Verify(value => value.GetConfigurationAsync(ConfigurationId, Token), Times.Exactly(configurationReads));
                RuntimeConfiguration.Verify(value => value.ListCredentialsForProviderAsync(It.IsAny<FunctionProviderType>(), Token), Times.Exactly(credentialReads));
                RuntimeConfiguration.VerifyNoOtherCalls();
                ConfigurationRepository.VerifyNoOtherCalls();
                CredentialRepository.VerifyNoOtherCalls();
                ExecutionRepository.VerifyNoOtherCalls();
            }
            else
            {
                ConfigurationRepository.Verify(value => value.GetByIdAsync(ConfigurationId, Token), Times.Exactly(configurationReads));
                CredentialRepository.Verify(value => value.GetByProviderTypeAsync(It.IsAny<FunctionProviderType>(), Token), Times.Exactly(credentialReads));
                ExecutionRepository.Verify(value => value.CreateAsync(It.IsAny<FunctionExecution>(), Token), Times.Exactly(Writer.Creates.Count));
                ExecutionRepository.Verify(value => value.UpdateAsync(It.IsAny<FunctionExecution>(), Token), Times.Exactly(Writer.Updates.Count));
                ConfigurationRepository.VerifyNoOtherCalls();
                CredentialRepository.VerifyNoOtherCalls();
                ExecutionRepository.VerifyNoOtherCalls();
                RuntimeConfiguration.VerifyNoOtherCalls();
            }
            RuntimeReader.VerifyNoOtherCalls();
        }
    }

    private sealed record WriteAttempt(FunctionExecution Input, FunctionExecution Snapshot, CancellationToken Token);

    private sealed class RecordingLogger : ILogger<FunctionExecutionService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    // Both service paths use this recording persistence boundary. It models the
    // writer's temporary version increment and #1569 restoration on failed writes.
    // Attempt snapshots retain the proposed write; Input exposes the restored caller.
    private sealed class RecordingWriter(List<string> events) : IFunctionExecutionRuntimeWriter
    {
        public List<WriteAttempt> Creates { get; } = [];
        public List<WriteAttempt> Updates { get; } = [];
        public Func<int, CancellationToken, Exception> CreateError { get; set; }
        public Func<int, CancellationToken, Exception> UpdateError { get; set; }
        public bool UpdateResult { get; set; } = true;

        public Task<Guid> CreateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
        {
            events.Add("create");
            Assert.Null(execution.FunctionConfiguration);
            Assert.Empty(execution.AuditEvents);
            Creates.Add(new WriteAttempt(execution, Snapshot(execution), cancellationToken));
            var error = CreateError?.Invoke(Creates.Count, cancellationToken);
            return error == null ? Task.FromResult(execution.Id) : Task.FromException<Guid>(error);
        }

        public Task<bool> UpdateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
        {
            events.Add("update");
            Assert.Null(execution.FunctionConfiguration);
            Assert.Empty(execution.AuditEvents);
            var originalVersion = execution.Version;
            var saved = false;
            execution.Version = unchecked(originalVersion + 1);
            try
            {
                Updates.Add(new WriteAttempt(execution, Snapshot(execution), cancellationToken));
                var error = UpdateError?.Invoke(Updates.Count, cancellationToken);
                if (error is not null) return Task.FromException<bool>(error);
                saved = UpdateResult;
                return Task.FromResult(saved);
            }
            finally
            {
                if (!saved) execution.Version = originalVersion;
            }
        }

        private static FunctionExecution Snapshot(FunctionExecution value) => new()
        {
            Id = value.Id, FunctionConfigurationId = value.FunctionConfigurationId, VirtualKeyId = value.VirtualKeyId,
            ExecutionMode = value.ExecutionMode, State = value.State, RequestedAt = value.RequestedAt,
            StartedAt = value.StartedAt, CompletedAt = value.CompletedAt, Duration = value.Duration,
            RequestJson = value.RequestJson, ResponseJson = value.ResponseJson, ErrorMessage = value.ErrorMessage,
            EstimatedCost = value.EstimatedCost, ActualCost = value.ActualCost, CostCalculationDetails = value.CostCalculationDetails,
            RetryCount = value.RetryCount, NextRetryAt = value.NextRetryAt, LeasedBy = value.LeasedBy,
            LeaseExpiryTime = value.LeaseExpiryTime, Version = value.Version, WebhookUrl = value.WebhookUrl,
            WebhookDelivered = value.WebhookDelivered, ProgressPercentage = value.ProgressPercentage, StatusMessage = value.StatusMessage
        };
    }
}
