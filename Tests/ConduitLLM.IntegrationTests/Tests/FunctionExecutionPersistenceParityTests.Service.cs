using System.Text.Json;

using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class FunctionExecutionPersistenceParityTests
{
    private const decimal ServiceEstimatedCost = 0.75m;
    private const decimal ServiceIncurredCost = 0.12345678m;
    private const string ServiceProviderResponse = """{"results":[{"id":"provider-response"}],"charged":true}""";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InlineProviderResultConcurrencyMissReturnsBillableFailureWithoutChangingWinner(int adapter)
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            FunctionExecution? winner = null;
            var client = new CompetingServiceProvider(async token =>
            {
                winner = await WriteCompetingServiceWinnerAsync(connectionString, token);
            });
            var logger = new ServiceConcurrencyLogger();
            var reader = new Mock<IFunctionExecutionRuntimeReader>(MockBehavior.Strict);
            var service = CreateCompetingExecutionService(adapter, efSource, source, client, reader.Object, logger);

            var result = await service.ExecuteAsync(1, 9007,
                new Dictionary<string, object> { ["query"] = "incurred provider operation" },
                metadata: new Dictionary<string, object> { ["request"] = "service-concurrency" },
                cancellationToken: deadline.Token);

            Assert.NotNull(winner);
            Assert.Equal(winner.Id, result.Id);
            Assert.Equal(ExecutionState.Failed, result.State);
            Assert.Equal(ServiceEstimatedCost, result.EstimatedCost);
            Assert.Equal(ServiceIncurredCost, result.ActualCost);
            Assert.Equal(ServiceProviderResponse, result.ResponseJson);
            Assert.Equal(1, result.Version);
            Assert.NotNull(result.CompletedAt);
            Assert.NotNull(result.Duration);
            Assert.Contains("reconcil", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("concurrency conflict", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("remains billable", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            var details = JsonSerializer.Deserialize(result.CostCalculationDetails!,
                FunctionsJsonContext.Default.FunctionExecutionCostDetails);
            Assert.NotNull(details);
            Assert.Equal(ServiceEstimatedCost, details.EstimatedCost);
            Assert.Equal(ServiceIncurredCost, details.ActualCost);
            Assert.Equal(200, details.HttpStatusCode);
            Assert.Equal(3, details.Usage.ResultCount);
            Assert.Equal(1, client.Calls);
            Assert.Equal(1, client.UsageCalculations);
            reader.VerifyNoOtherCalls();
            await AssertRowAsync(connectionString, winner);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InlineProviderExceptionConcurrencyMissPreservesOriginalExceptionAndDurableWinner(int adapter)
    {
        await InSchemaAsync(async (connectionString, efSource, source) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            FunctionExecution? winner = null;
            var providerError = new InvalidOperationException("Provider transport failed after competing completion.");
            var client = new CompetingServiceProvider(async token =>
            {
                winner = await WriteCompetingServiceWinnerAsync(connectionString, token);
            }, providerError);
            var logger = new ServiceConcurrencyLogger();
            var reader = new Mock<IFunctionExecutionRuntimeReader>(MockBehavior.Strict);
            var service = CreateCompetingExecutionService(adapter, efSource, source, client, reader.Object, logger);

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(1, 9007,
                new Dictionary<string, object> { ["query"] = "provider exception" },
                cancellationToken: deadline.Token));

            Assert.Same(providerError, actual);
            Assert.NotNull(winner);
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.UsageCalculations);
            Assert.Contains(logger.Entries, entry => entry.Level >= LogLevel.Warning &&
                entry.Message.Contains("reconcil", StringComparison.OrdinalIgnoreCase) &&
                entry.Message.Contains("concurrency conflict", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logger.Entries, entry => ReferenceEquals(entry.Exception, providerError));
            reader.VerifyNoOtherCalls();
            await AssertRowAsync(connectionString, winner);
        });
    }

    private static FunctionExecutionService CreateCompetingExecutionService(
        int adapter, NpgsqlDataSource efSource, NpgsqlDataSource source, IFunctionClient client,
        IFunctionExecutionRuntimeReader reader, ILogger<FunctionExecutionService> logger)
    {
        var configuration = new FunctionConfiguration
        {
            Id = 1, ConfigurationName = "controlled concurrency provider", ProviderType = FunctionProviderType.Exa,
            Purpose = FunctionPurpose.Search, IsEnabled = true, DefaultExecutionMode = ExecutionMode.Synchronous
        };
        var credentials = new List<FunctionCredential>
        {
            new() { Id = 1, ProviderType = FunctionProviderType.Exa, IsEnabled = true, ApiKey = "fixture-key" }
        };
        var costs = new Mock<IFunctionCostCalculationService>(MockBehavior.Strict);
        costs.Setup(value => value.EstimateCostAsync(1, It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(ServiceEstimatedCost);
        costs.Setup(value => value.CalculateCostAsync(1,
                It.Is<FunctionExecutionUsage>(usage => usage.ResultCount == 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceIncurredCost);
        var clients = new Mock<IFunctionClientFactory>(MockBehavior.Strict);
        clients.Setup(value => value.GetClientAsync(FunctionProviderType.Exa, 1)).ReturnsAsync(client);
        var protector = new Mock<IFunctionCredentialProtector>(MockBehavior.Strict);
        protector.Setup(value => value.Reveal("fixture-key")).Returns("fixture-key");

        if (adapter == 3)
        {
            var configurations = new Mock<IFunctionConfigurationRepository>(MockBehavior.Strict);
            configurations.Setup(value => value.GetByIdAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(configuration);
            var credentialRepository = new Mock<IFunctionCredentialRepository>(MockBehavior.Strict);
            credentialRepository.Setup(value => value.GetByProviderTypeAsync(FunctionProviderType.Exa,
                    It.IsAny<CancellationToken>())).ReturnsAsync(credentials);
            var repository = new FunctionExecutionRepository(new ContextFactory(efSource),
                NullLogger<FunctionExecutionRepository>.Instance);
            return new FunctionExecutionService(configurations.Object, credentialRepository.Object, repository,
                costs.Object, clients.Object, protector.Object, logger);
        }

        var store = new Mock<IFunctionExecutionConfigurationStore>(MockBehavior.Strict);
        store.Setup(value => value.GetConfigurationAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(configuration);
        store.Setup(value => value.ListCredentialsForProviderAsync(FunctionProviderType.Exa,
                It.IsAny<CancellationToken>())).ReturnsAsync(credentials);
        return FunctionExecutionService.CreateWithRuntimePersistence(store.Object, Writers(efSource, source)[adapter],
            reader, costs.Object, clients.Object, protector.Object, logger);
    }

    private static async Task<FunctionExecution> WriteCompetingServiceWinnerAsync(
        string connectionString, CancellationToken token)
    {
        Guid id;
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                     { Pooling = false, ApplicationName = "execution_service_competitor" }.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE "FunctionExecutions"
                SET "State" = @state, "StatusMessage" = 'independent terminal winner',
                    "Version" = "Version" + 1, "CompletedAt" = @completed,
                    "ResponseJson" = '{"source":"competing-winner"}'::jsonb,
                    "ErrorMessage" = 'competing cancellation', "ActualCost" = 99.87654321,
                    "CostCalculationDetails" = '{"source":"winning-details"}'::jsonb,
                    "Duration" = interval '7 seconds', "RetryCount" = 5,
                    "NextRetryAt" = @nextRetry, "LeasedBy" = 'winning-worker',
                    "LeaseExpiryTime" = @leaseExpiry, "WebhookDelivered" = true,
                    "ProgressPercentage" = 88
                WHERE "State" = @running AND "Version" = 1
                RETURNING "Id"
                """;
            command.Parameters.AddWithValue("state", (int)ExecutionState.Cancelled);
            command.Parameters.AddWithValue("running", (int)ExecutionState.Running);
            command.Parameters.AddWithValue("completed", Instant.AddMinutes(1));
            command.Parameters.AddWithValue("nextRetry", Instant.AddDays(1));
            command.Parameters.AddWithValue("leaseExpiry", Instant.AddHours(1));
            await using var rows = await command.ExecuteReaderAsync(token);
            Assert.True(await rows.ReadAsync(token));
            id = rows.GetGuid(0);
            Assert.False(await rows.ReadAsync(token));
        }
        var winner = await ReadAsync(connectionString, id);
        Assert.NotNull(winner);
        Assert.Equal(ExecutionState.Cancelled, winner.State);
        Assert.Equal("independent terminal winner", winner.StatusMessage);
        Assert.Equal(2, winner.Version);
        Assert.Equal(99.87654321m, winner.ActualCost);
        return winner;
    }

    private sealed class CompetingServiceProvider(
        Func<CancellationToken, Task> compete, Exception? providerError = null) : IFunctionClient
    {
        public int Calls { get; private set; }
        public int UsageCalculations { get; private set; }
        public FunctionProviderType ProviderType => FunctionProviderType.Exa;
        public string ProviderName => "Controlled competing provider";
        public Task<FunctionAuthenticationResult> VerifyAuthenticationAsync(
            string? apiKey = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Execution must not probe provider authentication.");

        public async Task<FunctionExecutionResult> ExecuteAsync(Dictionary<string, object> parameters,
            string? apiKey = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, ++Calls);
            Assert.Equal("fixture-key", apiKey);
            await compete(cancellationToken);
            if (providerError is not null) throw providerError;
            return new FunctionExecutionResult
            {
                IsSuccess = true, ResponseJson = ServiceProviderResponse, HttpStatusCode = 200
            };
        }

        public FunctionExecutionUsage CalculateUsageFromResponse(
            Dictionary<string, object> parameters, FunctionExecutionResult result)
        {
            Assert.Equal(1, ++UsageCalculations);
            Assert.Equal(ServiceProviderResponse, result.ResponseJson);
            return new FunctionExecutionUsage { ResultCount = 3 };
        }
    }

    private sealed class ServiceConcurrencyLogger : ILogger<FunctionExecutionService>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
