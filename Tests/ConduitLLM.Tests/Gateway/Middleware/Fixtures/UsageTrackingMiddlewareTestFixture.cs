using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Tests.TestInfrastructure;
using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;

namespace ConduitLLM.Tests.Http.Middleware.Fixtures
{
    /// <summary>
    /// Base fixture for UsageTrackingMiddleware tests.
    /// Provides pre-configured mocks and helper methods for common test scenarios.
    /// </summary>
    public class UsageTrackingMiddlewareTestFixture : IDisposable
    {
        private ConduitDbContext? _dbContext;
        private readonly SqliteTestDatabase _database;

        /// <summary>
        /// Mock for the cost calculation service.
        /// </summary>
        public Mock<ICostCalculationService> CostService { get; }

        /// <summary>
        /// Mock for the batch spend update service.
        /// </summary>
        public Mock<IBatchSpendUpdateService> BatchSpendService { get; }

        /// <summary>
        /// Mock for the request log service.
        /// </summary>
        public Mock<IRequestLogService> RequestLogService { get; }

        /// <summary>
        /// Mock for the virtual key service.
        /// </summary>
        public Mock<IVirtualKeyService> VirtualKeyService { get; }

        /// <summary>
        /// Mock for the billing audit service.
        /// </summary>
        public Mock<IBillingAuditService> BillingAuditService { get; }

        /// <summary>
        /// Mock for the tool cost calculation service.
        /// </summary>
        public Mock<IToolCostCalculationService> ToolCostService { get; }

        /// <summary>
        /// Mock for the middleware logger.
        /// </summary>
        public Mock<ILogger<UsageTrackingMiddleware>> Logger { get; }

        /// <summary>
        /// Captured billing audit events for assertions.
        /// Events are captured when BillingAuditService.LogBillingEvent is called.
        /// </summary>
        public List<BillingAuditEvent> CapturedBillingEvents { get; }

        /// <summary>
        /// Captured request logs for assertions.
        /// Logs are captured when RequestLogService.LogRequestAsync is called.
        /// </summary>
        public List<LogRequestDto> CapturedRequestLogs { get; }

        /// <summary>
        /// Initializes a new instance of the test fixture with default mock configurations.
        /// </summary>
        public UsageTrackingMiddlewareTestFixture()
        {
            _database = new SqliteTestDatabase();
            CapturedBillingEvents = new List<BillingAuditEvent>();
            CapturedRequestLogs = new List<LogRequestDto>();

            // Initialize mocks with default behaviors
            CostService = CreateCostServiceMock();
            BatchSpendService = CreateBatchSpendServiceMock();
            RequestLogService = CreateRequestLogServiceMock();
            VirtualKeyService = CreateVirtualKeyServiceMock();
            BillingAuditService = CreateBillingAuditServiceMock();
            ToolCostService = CreateToolCostServiceMock();
            Logger = new Mock<ILogger<UsageTrackingMiddleware>>();
        }

        /// <summary>
        /// Creates an in-memory database context for tool usage tests.
        /// </summary>
        /// <returns>A ConduitDbContext backed by an in-memory database.</returns>
        public ConduitDbContext GetDbContext()
        {
            if (_dbContext == null)
            {
                _dbContext = _database.CreateContext();
            }
            return _dbContext;
        }

        /// <summary>
        /// Creates a real ToolCostCalculationService backed by the in-memory database.
        /// Use this for tool usage integration tests.
        /// </summary>
        /// <returns>A real IToolCostCalculationService instance.</returns>
        public IToolCostCalculationService GetRealToolCostService()
        {
            var loggerMock = new Mock<ILogger<ToolCostCalculationService>>();
            var factoryMock = new Mock<IDbContextFactory<ConduitDbContext>>();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _database.CreateContext());
            return new ToolCostCalculationService(factoryMock.Object, loggerMock.Object);
        }

        /// <summary>
        /// Adds a tool configuration to the in-memory database.
        /// </summary>
        /// <param name="provider">The provider type.</param>
        /// <param name="toolName">The tool name.</param>
        /// <param name="costPerUnit">Cost per usage unit.</param>
        /// <param name="billingUnit">The billing unit (e.g., "requests").</param>
        public async Task AddToolConfigurationAsync(
            ProviderType provider,
            string toolName,
            decimal costPerUnit,
            string billingUnit = "requests")
        {
            var context = GetDbContext();
            context.ProviderTools.Add(new ProviderTool
            {
                Provider = provider,
                ToolName = toolName,
                CostPerUnit = costPerUnit,
                BillingUnit = billingUnit,
                IsActive = true
            });
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Configures the cost service to return a specific cost for any model.
        /// </summary>
        /// <param name="cost">The cost to return.</param>
        public void SetupDefaultCost(decimal cost)
        {
            CostService.Setup(x => x.CalculateCostAsync(
                It.IsAny<string>(),
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(cost);
        }

        /// <summary>
        /// Configures the cost service to return a specific cost for a specific model.
        /// </summary>
        /// <param name="modelName">The model name to match.</param>
        /// <param name="cost">The cost to return.</param>
        public void SetupCostForModel(string modelName, decimal cost)
        {
            CostService.Setup(x => x.CalculateCostAsync(
                modelName,
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(cost);
        }

        /// <summary>
        /// Configures the batch spend service health status.
        /// </summary>
        /// <param name="isHealthy">Whether the service is healthy.</param>
        public void SetupBatchSpendServiceHealth(bool isHealthy)
        {
            BatchSpendService.SetupGet(x => x.IsHealthy).Returns(isHealthy);
        }

        /// <summary>
        /// Resets all captured events and logs.
        /// </summary>
        public void ResetCapturedData()
        {
            CapturedBillingEvents.Clear();
            CapturedRequestLogs.Clear();
        }

        /// <summary>
        /// Resets all mock setups to their default state.
        /// </summary>
        public void ResetMocks()
        {
            CostService.Reset();
            BatchSpendService.Reset();
            RequestLogService.Reset();
            VirtualKeyService.Reset();
            BillingAuditService.Reset();
            ToolCostService.Reset();
            Logger.Reset();

            // Re-apply default behaviors after reset
            SetupDefaultMockBehaviors();
            ResetCapturedData();
        }

        private Mock<ICostCalculationService> CreateCostServiceMock()
        {
            var mock = new Mock<ICostCalculationService>();
            // Default: return 0 cost
            mock.Setup(x => x.CalculateCostAsync(
                It.IsAny<string>(),
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(0m);
            return mock;
        }

        private Mock<IBatchSpendUpdateService> CreateBatchSpendServiceMock()
        {
            var mock = new Mock<IBatchSpendUpdateService>();
            // Default: healthy service
            mock.SetupGet(x => x.IsHealthy).Returns(true);
            return mock;
        }

        private Mock<IRequestLogService> CreateRequestLogServiceMock()
        {
            var mock = new Mock<IRequestLogService>();
            // Capture logged requests
            mock.Setup(x => x.LogRequestAsync(It.IsAny<LogRequestDto>()))
                .Callback<LogRequestDto>(log => CapturedRequestLogs.Add(log))
                .Returns(Task.CompletedTask);
            return mock;
        }

        private Mock<IVirtualKeyService> CreateVirtualKeyServiceMock()
        {
            var mock = new Mock<IVirtualKeyService>();
            // Default: successful spend updates
            mock.Setup(x => x.UpdateSpendAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
                .ReturnsAsync(true);
            return mock;
        }

        private Mock<IBillingAuditService> CreateBillingAuditServiceMock()
        {
            var mock = new Mock<IBillingAuditService>();
            // Capture billing events
            mock.Setup(x => x.LogBillingEvent(It.IsAny<BillingAuditEvent>()))
                .Callback<BillingAuditEvent>(evt => CapturedBillingEvents.Add(evt));
            mock.Setup(x => x.LogBillingEventAsync(It.IsAny<BillingAuditEvent>()))
                .Callback<BillingAuditEvent>(evt => CapturedBillingEvents.Add(evt))
                .Returns(Task.CompletedTask);
            return mock;
        }

        private Mock<IToolCostCalculationService> CreateToolCostServiceMock()
        {
            var mock = new Mock<IToolCostCalculationService>();
            // Default: return 0 tool cost
            mock.Setup(x => x.CalculateToolCostsAsync(
                It.IsAny<ToolUsageData>(),
                It.IsAny<ProviderType>()))
                .ReturnsAsync(new ToolCostResult { TotalCost = 0m });
            mock.Setup(x => x.SerializeToolUsage(It.IsAny<ToolUsageData>()))
                .Returns<ToolUsageData>(data => System.Text.Json.JsonSerializer.Serialize(data));
            return mock;
        }

        private void SetupDefaultMockBehaviors()
        {
            // Re-setup request log capture
            RequestLogService.Setup(x => x.LogRequestAsync(It.IsAny<LogRequestDto>()))
                .Callback<LogRequestDto>(log => CapturedRequestLogs.Add(log))
                .Returns(Task.CompletedTask);

            // Re-setup billing event capture
            BillingAuditService.Setup(x => x.LogBillingEvent(It.IsAny<BillingAuditEvent>()))
                .Callback<BillingAuditEvent>(evt => CapturedBillingEvents.Add(evt));
            BillingAuditService.Setup(x => x.LogBillingEventAsync(It.IsAny<BillingAuditEvent>()))
                .Callback<BillingAuditEvent>(evt => CapturedBillingEvents.Add(evt))
                .Returns(Task.CompletedTask);

            // Default health status
            BatchSpendService.SetupGet(x => x.IsHealthy).Returns(true);
        }

        /// <summary>
        /// Disposes of the test fixture and releases resources.
        /// </summary>
        public void Dispose()
        {
            _dbContext?.Dispose();
            _database.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
