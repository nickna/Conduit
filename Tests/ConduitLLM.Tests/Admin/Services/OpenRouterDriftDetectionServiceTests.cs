using System.Net;

using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Tests.TestInfrastructure;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;
using Moq.Protected;

using Xunit;

namespace ConduitLLM.Tests.Admin.Services
{
    /// <summary>
    /// Tests for OpenRouter drift detection: pricing/capability drift, model-removed, and idempotent
    /// re-detection. Uses an in-memory DbContext and a mocked catalog fetch.
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Component", "Admin")]
    public class OpenRouterDriftDetectionServiceTests : IDisposable
    {
        private readonly DbContextOptions<ConduitDbContext> _dbOptions;
        private readonly Mock<IDbContextFactory<ConduitDbContext>> _dbFactory;
        private readonly SqliteTestDatabase _database;

        public OpenRouterDriftDetectionServiceTests()
        {
            _database = new SqliteTestDatabase();
            _dbOptions = _database.Options;
            _dbFactory = new Mock<IDbContextFactory<ConduitDbContext>>();
            _dbFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _database.CreateContext());
        }

        private void SeedOpenRouterMapping(bool withCost = true)
        {
            using var db = new ConduitDbContext(_dbOptions);
            db.Providers.Add(new Provider { Id = 1, ProviderType = ProviderType.OpenRouter, ProviderName = "OpenRouter", IsEnabled = true });
            db.ModelAuthors.Add(new ModelAuthor { Id = 1, Name = "Anthropic" });
            db.ModelSeries.Add(new ModelSeries { Id = 1, AuthorId = 1, Name = "Claude", Parameters = "{}" });
            db.Models.Add(new Model { Id = 1, Name = "Claude 3.5 Sonnet", ModelSeriesId = 1, SupportsVision = false, SupportsFunctionCalling = false, SupportsImageGeneration = false });
            if (withCost)
            {
                db.ModelCosts.Add(new ModelCost { Id = 1, CostName = "claude", InputCostPerMillionTokens = 3.0m, OutputCostPerMillionTokens = 15.0m, IsActive = true });
            }
            db.ModelProviderTypeAssociations.Add(new ModelProviderTypeAssociation
            {
                Id = 1,
                ModelId = 1,
                ModelCostId = withCost ? 1 : null,
                Identifier = "anthropic/claude-3.5-sonnet"
            });
            db.ModelProviderMappings.Add(new ModelProviderMapping
            {
                Id = 1,
                ProviderId = 1,
                ProviderModelId = "anthropic/claude-3.5-sonnet",
                ModelAlias = "claude",
                ModelProviderTypeAssociationId = 1,
                IsEnabled = true
            });
            db.SaveChanges();
        }

        private OpenRouterDriftDetectionService CreateService(string catalogJson)
        {
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(catalogJson) });
            var httpFactory = new Mock<IHttpClientFactory>();
            httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));

            var options = Options.Create(new OpenRouterSyncOptions { ModelsEndpoint = "https://test.invalid/models" });
            var logger = new Mock<ILogger<OpenRouterDriftDetectionService>>();
            return new OpenRouterDriftDetectionService(_dbFactory.Object, httpFactory.Object, options, logger.Object);
        }

        [Fact]
        public async Task RunSyncAsync_DetectsPricingAndCapabilityDrift()
        {
            // Arrange — provider raised prompt price to $4/M and now reports image input (vision)
            SeedOpenRouterMapping();
            var catalog = "{\"data\":[{\"id\":\"anthropic/claude-3.5-sonnet\"," +
                          "\"pricing\":{\"prompt\":\"0.000004\",\"completion\":\"0.000015\"}," +
                          "\"architecture\":{\"input_modalities\":[\"text\",\"image\"],\"output_modalities\":[\"text\"]}," +
                          "\"supported_parameters\":[\"tools\"]}]}";
            var service = CreateService(catalog);

            // Act
            var run = await service.RunSyncAsync("Manual");

            // Assert
            run.Status.Should().Be("Completed");
            using var db = new ConduitDbContext(_dbOptions);
            var items = db.ProviderMetadataDriftItems.ToList();
            items.Select(i => i.DriftType).Should().Contain(DriftType.Pricing);
            items.Select(i => i.DriftType).Should().Contain(DriftType.Capabilities);
            items.Should().OnlyContain(i => i.Status == DriftStatus.Pending);
        }

        [Fact]
        public async Task RunSyncAsync_ModelNotInCatalog_CreatesModelRemovedDrift()
        {
            // Arrange — catalog does not contain the mapped model
            SeedOpenRouterMapping();
            var service = CreateService("{\"data\":[{\"id\":\"some/other-model\",\"pricing\":{\"prompt\":\"0.000001\",\"completion\":\"0.000002\"}}]}");

            // Act
            await service.RunSyncAsync("Manual");

            // Assert
            using var db = new ConduitDbContext(_dbOptions);
            db.ProviderMetadataDriftItems.Select(i => i.DriftType).Should().Contain(DriftType.ModelRemoved);
        }

        [Fact]
        public async Task RunSyncAsync_RunTwice_DoesNotDuplicatePendingItems()
        {
            // Arrange
            SeedOpenRouterMapping();
            var catalog = "{\"data\":[{\"id\":\"anthropic/claude-3.5-sonnet\"," +
                          "\"pricing\":{\"prompt\":\"0.000004\",\"completion\":\"0.000015\"}}]}";
            var service = CreateService(catalog);

            // Act
            await service.RunSyncAsync("Manual");
            var secondRun = await service.RunSyncAsync("Manual");

            // Assert — the second run refreshes the existing pending pricing item, not duplicates it
            using var db = new ConduitDbContext(_dbOptions);
            db.ProviderMetadataDriftItems.Count(i => i.DriftType == DriftType.Pricing && i.Status == DriftStatus.Pending)
                .Should().Be(1);
            secondRun.ItemsUpdated.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task RunSyncAsync_NoModelCost_DetectsMissingCost()
        {
            // Arrange — mapping has no ModelCost but the provider publishes pricing
            SeedOpenRouterMapping(withCost: false);
            var service = CreateService("{\"data\":[{\"id\":\"anthropic/claude-3.5-sonnet\",\"pricing\":{\"prompt\":\"0.000003\",\"completion\":\"0.000015\"}}]}");

            // Act
            await service.RunSyncAsync("Manual");

            // Assert
            using var db = new ConduitDbContext(_dbOptions);
            db.ProviderMetadataDriftItems.Select(i => i.DriftType).Should().Contain(DriftType.MissingCost);
        }

        [Fact]
        public async Task RunSyncAsync_CancelledCatalogFetch_PersistsCancelledWithoutDriftMutations()
        {
            SeedOpenRouterMapping();
            using var cancellation = new CancellationTokenSource();
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Returns(async (HttpRequestMessage _, CancellationToken token) =>
                {
                    cancellation.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(service => service.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));
            var service = new OpenRouterDriftDetectionService(_dbFactory.Object, factory.Object,
                Options.Create(new OpenRouterSyncOptions { ModelsEndpoint = "https://test.invalid/models" }),
                Mock.Of<ILogger<OpenRouterDriftDetectionService>>());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunSyncAsync("Manual", cancellation.Token));
            await using var verification = _database.CreateContext();
            var run = await verification.ProviderMetadataSyncRuns.SingleAsync();
            run.Status.Should().Be("Cancelled");
            run.CompletedAt.Should().NotBeNull();
            (await verification.ProviderMetadataDriftItems.CountAsync()).Should().Be(0);
        }

        public void Dispose() => _database.Dispose();
    }
}
