using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Security;
using ConduitLLM.Core.Decorators;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Providers;
using ConduitLLM.Providers.OpenAI;
using ConduitLLM.Providers.Vertex;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;
using Moq.Protected;

namespace ConduitLLM.Tests.Providers
{
    public class DatabaseAwareLLMClientFactoryTests
    {
        private readonly Mock<IProviderService> _mockCredentialService;
        private readonly Mock<IModelProviderMappingService> _mockMappingService;
        private readonly Mock<ILoggerFactory> _mockLoggerFactory;
        private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
        private readonly Mock<ILogger<DatabaseAwareLLMClientFactory>> _mockLogger;
        private readonly ProviderSecretProtector _protector;
        private readonly DatabaseAwareLLMClientFactory _factory;

        public DatabaseAwareLLMClientFactoryTests()
        {
            _mockCredentialService = new Mock<IProviderService>();
            _mockMappingService = new Mock<IModelProviderMappingService>();
            _mockLoggerFactory = new Mock<ILoggerFactory>();
            _mockHttpClientFactory = new Mock<IHttpClientFactory>();
            _mockLogger = new Mock<ILogger<DatabaseAwareLLMClientFactory>>();

            _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
                .Returns(Mock.Of<ILogger>());
            _mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient());

            _protector = new ProviderSecretProtector(
                DataProtectionProvider.Create(nameof(DatabaseAwareLLMClientFactoryTests)),
                NullLogger<ProviderSecretProtector>.Instance);
            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider
                .Setup(provider => provider.GetService(typeof(IProviderSecretProtector)))
                .Returns(_protector);

            _factory = new DatabaseAwareLLMClientFactory(
                _mockCredentialService.Object,
                _mockMappingService.Object,
                _mockLoggerFactory.Object,
                _mockHttpClientFactory.Object,
                _mockLogger.Object,
                mockServiceProvider.Object);
        }

        [Fact]
        public async Task GetClientAsync_WithNonExistentModel_ThrowsModelNotFoundException()
        {
            // Arrange
            var modelName = "non-existent-model";
            _mockMappingService.Setup(x => x.GetMappingByModelAliasAsync(modelName))
                .ReturnsAsync((ModelProviderMapping?)null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ModelNotFoundException>(
                async () => await _factory.GetClientAsync(modelName)
            );

            Assert.Equal($"The model '{modelName}' does not exist or is not available.", exception.Message);
            Assert.Equal(modelName, exception.ModelName);
        }

        // ---------------------------------------------------------------------
        // GetClientForChatAsync route eligibility (#1191).
        // Administratively disabled routes make the alias unavailable to the client: 404
        // (ModelNotFoundException). Only route *health* exhaustion is a retryable 503.
        // ---------------------------------------------------------------------

        [Fact]
        public async Task GetClientForChatAsync_WithNoMappings_ThrowsModelNotFoundException()
        {
            var request = new ConduitLLM.Core.Models.ChatCompletionRequest
            {
                Model = "unmapped-model",
                Messages = []
            };
            _mockMappingService.Setup(x => x.GetMappingsByModelAliasAsync(request.Model))
                .ReturnsAsync(new List<ModelProviderMapping>());

            var exception = await Assert.ThrowsAsync<ModelNotFoundException>(
                () => _factory.GetClientForChatAsync(request));

            Assert.Equal($"The model '{request.Model}' does not exist or is not available.", exception.Message);
        }

        [Fact]
        public async Task GetClientForChatAsync_WithDisabledMapping_ThrowsModelNotFoundException()
        {
            var request = NewChatRequest();
            _mockMappingService.Setup(x => x.GetMappingsByModelAliasAsync(request.Model))
                .ReturnsAsync([NewRoutableMapping(1, mappingEnabled: false)]);

            var exception = await Assert.ThrowsAsync<ModelNotFoundException>(
                () => _factory.GetClientForChatAsync(request));

            Assert.Equal($"The model '{request.Model}' does not exist or is not available.", exception.Message);
        }

        [Fact]
        public async Task GetClientForChatAsync_WithDisabledProvider_ThrowsModelNotFoundException()
        {
            var request = NewChatRequest();
            _mockMappingService.Setup(x => x.GetMappingsByModelAliasAsync(request.Model))
                .ReturnsAsync([NewRoutableMapping(1, providerEnabled: false)]);

            await Assert.ThrowsAsync<ModelNotFoundException>(
                () => _factory.GetClientForChatAsync(request));
        }

        [Fact]
        public async Task GetClientForChatAsync_WithDisabledTypeAssociation_ThrowsModelNotFoundException()
        {
            var request = NewChatRequest();
            _mockMappingService.Setup(x => x.GetMappingsByModelAliasAsync(request.Model))
                .ReturnsAsync([NewRoutableMapping(1, associationEnabled: false)]);

            await Assert.ThrowsAsync<ModelNotFoundException>(
                () => _factory.GetClientForChatAsync(request));
        }

        [Fact]
        public async Task GetClientForChatAsync_WithEnabledMappingButNoCredential_ThrowsServiceUnavailableException()
        {
            // The route is configured, so this is genuine health exhaustion — 503, not 404.
            var request = NewChatRequest();
            _mockMappingService.Setup(x => x.GetMappingsByModelAliasAsync(request.Model))
                .ReturnsAsync([NewRoutableMapping(1)]);
            _mockCredentialService.Setup(x => x.GetProviderByIdAsync(1))
                .ReturnsAsync((Provider?)null);

            await Assert.ThrowsAsync<ServiceUnavailableException>(
                () => _factory.GetClientForChatAsync(request));
        }

        [Fact]
        public async Task GetClientForChatAsync_UsesRuntimeStoreForPersistedRoutePolicy()
        {
            var request = NewChatRequest();
            _mockMappingService.Setup(x => x.GetMappingsByModelAliasAsync(request.Model))
                .ReturnsAsync([NewRoutableMapping(1)]);
            _mockCredentialService.Setup(x => x.GetProviderByIdAsync(1))
                .ReturnsAsync((Provider?)null);
            var runtimeStore = new Mock<IModelProviderMappingRuntimeStore>();
            runtimeStore.Setup(store => store.GetRoutePolicyAsync(
                    request.Model, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ModelRoutePolicyRuntimeRecord
                {
                    Id = 4,
                    ModelAlias = request.Model,
                    Strategy = "Balanced",
                    CostWeight = 0.6m,
                    SpeedWeight = 0.2m,
                    QualityWeight = 0.2m,
                    CacheAffinityEnabled = false,
                    AffinityTtlSeconds = 600,
                    MaxAffinityScorePenalty = 0.05m,
                    IsEnabled = true
                });
            var factory = new DatabaseAwareLLMClientFactory(
                _mockCredentialService.Object,
                _mockMappingService.Object,
                _mockLoggerFactory.Object,
                _mockHttpClientFactory.Object,
                _mockLogger.Object,
                Mock.Of<IServiceProvider>(),
                runtimeMappingStore: runtimeStore.Object);

            await Assert.ThrowsAsync<ServiceUnavailableException>(
                () => factory.GetClientForChatAsync(request));

            runtimeStore.Verify(store => store.GetRoutePolicyAsync(
                request.Model, It.IsAny<CancellationToken>()), Times.Once);
        }

        private static ConduitLLM.Core.Models.ChatCompletionRequest NewChatRequest() => new()
        {
            Model = "test-model",
            Messages = []
        };

        private static ModelProviderMapping NewRoutableMapping(
            int id,
            bool mappingEnabled = true,
            bool providerEnabled = true,
            bool associationEnabled = true) => new()
            {
                Id = id,
                ModelAlias = "test-model",
                ProviderId = id,
                ProviderModelId = "gpt-4",
                IsEnabled = mappingEnabled,
                ModelProviderTypeAssociationId = id,
                Provider = new Provider
                {
                    Id = id,
                    ProviderName = "TestProvider",
                    ProviderType = ProviderType.OpenAI,
                    IsEnabled = providerEnabled
                },
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation
                {
                    Id = id,
                    IsEnabled = associationEnabled
                }
            };

        [Fact]
        public async Task GetClientAsync_WithDisabledProvider_ThrowsServiceUnavailableException()
        {
            // Arrange
            var modelName = "test-model";
            var mapping = new ModelProviderMapping
            {
                Id = 1,
                ModelAlias = modelName,
                ModelProviderTypeAssociationId = 1,
                ProviderId = 1,
                ProviderModelId = "gpt-4"
            };

            var provider = new Provider
            {
                Id = 1,
                ProviderName = "TestProvider",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = false // Disabled provider
            };

            _mockMappingService.Setup(x => x.GetMappingByModelAliasAsync(modelName))
                .ReturnsAsync(mapping);

            _mockCredentialService.Setup(x => x.GetProviderByIdAsync(1))
                .ReturnsAsync(provider);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ServiceUnavailableException>(
                async () => await _factory.GetClientAsync(modelName)
            );

            Assert.Equal($"Provider 'TestProvider' is currently disabled.", exception.Message);
            Assert.Equal("TestProvider", exception.ServiceName);
        }

        [Fact]
        public async Task GetClientAsync_WithNoApiKey_ThrowsConfigurationException()
        {
            // Arrange
            var modelName = "test-model";
            var mapping = new ModelProviderMapping
            {
                Id = 1,
                ModelAlias = modelName,
                ModelProviderTypeAssociationId = 1,
                ProviderId = 1,
                ProviderModelId = "gpt-4"
            };

            var provider = new Provider
            {
                Id = 1,
                ProviderName = "TestProvider",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };

            _mockMappingService.Setup(x => x.GetMappingByModelAliasAsync(modelName))
                .ReturnsAsync(mapping);

            _mockCredentialService.Setup(x => x.GetProviderByIdAsync(1))
                .ReturnsAsync(provider);

            // Return empty list of key credentials
            _mockCredentialService.Setup(x => x.GetKeyCredentialsByProviderIdAsync(1))
                .ReturnsAsync(new List<ProviderKeyCredential>());

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ConfigurationException>(
                async () => await _factory.GetClientAsync(modelName)
            );

            Assert.Contains("No API key configured", exception.Message);
        }

        [Fact]
        public async Task GetClientAsync_WithMultipleEnabledKeys_WrapsClientsForKeyFailover()
        {
            var mapping = new ModelProviderMapping
            {
                Id = 1,
                ModelAlias = "test-model",
                ProviderId = 1,
                ProviderModelId = "gpt-4"
            };
            var provider = new Provider
            {
                Id = 1,
                ProviderName = "TestProvider",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };
            _mockMappingService.Setup(service => service.GetMappingByModelAliasAsync("test-model"))
                .ReturnsAsync(mapping);
            _mockCredentialService.Setup(service => service.GetProviderByIdAsync(1))
                .ReturnsAsync(provider);
            _mockCredentialService.Setup(service => service.GetKeyCredentialsByProviderIdAsync(1))
                .ReturnsAsync(new List<ProviderKeyCredential>
                {
                    new()
                    {
                        Id = 10,
                        ProviderId = 1,
                        ApiKey = "fallback",
                        IsEnabled = true
                    },
                    new()
                    {
                        Id = 20,
                        ProviderId = 1,
                        ApiKey = "primary",
                        IsEnabled = true,
                        IsPrimary = true
                    },
                    new()
                    {
                        Id = 30,
                        ProviderId = 1,
                        ApiKey = "disabled",
                        IsEnabled = false
                    }
                });

            var client = await _factory.GetClientAsync("test-model");

            Assert.IsType<ProviderKeyFailoverLLMClient>(client);
        }

        [Fact]
        public async Task GetClientByProviderIdAsync_WithNonExistentProvider_ThrowsInvalidRequestException()
        {
            // Arrange
            var providerId = 999;
            _mockCredentialService.Setup(x => x.GetProviderByIdAsync(providerId))
                .ReturnsAsync((Provider?)null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidRequestException>(
                async () => await _factory.GetClientByProviderIdAsync(providerId)
            );

            Assert.Equal($"Provider with ID '{providerId}' not found.", exception.Message);
            Assert.Equal("provider_not_found", exception.ErrorCode);
            Assert.Equal("providerId", exception.Param);
        }

        [Fact]
        public async Task GetClientByProviderIdAsync_WithProviderModelId_DoesNotResolveModelAlias()
        {
            // Arrange - provider exists but has no key, so client creation stops after the
            // provider lookup; the mapping service must never be consulted on this path
            var providerId = 1;
            var provider = new Provider
            {
                Id = providerId,
                ProviderName = "TestProvider",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };

            _mockCredentialService.Setup(x => x.GetProviderByIdAsync(providerId))
                .ReturnsAsync(provider);
            _mockCredentialService.Setup(x => x.GetKeyCredentialsByProviderIdAsync(providerId))
                .ReturnsAsync(new List<ProviderKeyCredential>());

            // Act & Assert
            await Assert.ThrowsAsync<ConfigurationException>(
                async () => await _factory.GetClientByProviderIdAsync(providerId, "gpt-image-1")
            );

            _mockMappingService.Verify(x => x.GetMappingByModelAliasAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task GetClientByProviderIdAsync_WithProviderModelIdAndNonExistentProvider_ThrowsInvalidRequestException()
        {
            // Arrange
            var providerId = 999;
            _mockCredentialService.Setup(x => x.GetProviderByIdAsync(providerId))
                .ReturnsAsync((Provider?)null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidRequestException>(
                async () => await _factory.GetClientByProviderIdAsync(providerId, "gpt-image-1")
            );

            Assert.Equal($"Provider with ID '{providerId}' not found.", exception.Message);
            Assert.Equal("provider_not_found", exception.ErrorCode);
            Assert.Equal("providerId", exception.Param);
        }

        [Fact]
        public async Task GetClientByProviderTypeAsync_WithNoProvider_ThrowsInvalidRequestException()
        {
            // Arrange
            var providerType = ProviderType.OpenAI;
            _mockCredentialService.Setup(x => x.GetAllProvidersAsync())
                .ReturnsAsync(new List<Provider>());

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidRequestException>(
                async () => await _factory.GetClientByProviderTypeAsync(providerType)
            );

            Assert.Equal($"No provider configured for type '{providerType}'.", exception.Message);
            Assert.Equal("provider_type_not_found", exception.ErrorCode);
            Assert.Equal("providerType", exception.Param);
        }

        [Fact]
        public void CreateTestClient_WithNullProvider_ThrowsArgumentNullException()
        {
            var credential = new ProviderKeyCredential { ApiKey = "test-key" };

            var exception = Assert.Throws<ArgumentNullException>(
                () => _factory.CreateTestClient(null, credential));

            Assert.Equal("provider", exception.ParamName);
        }

        [Fact]
        public void CreateTestClient_WithNullCredential_ThrowsArgumentNullException()
        {
            var provider = new Provider { ProviderType = ProviderType.OpenAI };

            var exception = Assert.Throws<ArgumentNullException>(
                () => _factory.CreateTestClient(provider, null));

            Assert.Equal("keyCredential", exception.ParamName);
        }

        [Fact]
        public void CreateTestClient_WithBlankApiKey_ThrowsArgumentException()
        {
            var provider = new Provider { ProviderType = ProviderType.OpenAI };
            var credential = new ProviderKeyCredential { ApiKey = " " };

            var exception = Assert.Throws<ArgumentException>(
                () => _factory.CreateTestClient(provider, credential));

            Assert.Equal("keyCredential", exception.ParamName);
        }

        [Fact]
        public void CreateTestClient_WithSupportedProvider_UsesSuppliedCredentialWithoutDatabaseLookup()
        {
            var provider = new Provider
            {
                Id = 17,
                ProviderName = "CredentialTest",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };
            var credential = new ProviderKeyCredential
            {
                Id = 23,
                ProviderId = provider.Id,
                ApiKey = "test-key",
                IsEnabled = true
            };

            var client = _factory.CreateTestClient(provider, credential);

            var contextClient = Assert.IsType<ContextAwareLLMClient>(client);
            Assert.IsType<OpenAIClient>(contextClient.InnerClient);
            _mockCredentialService.VerifyNoOtherCalls();
            _mockMappingService.VerifyNoOtherCalls();
        }

        [Fact]
        public void CreateTestClient_WithVertexServiceAccount_DoesNotRequireApiKey()
        {
            using var rsa = RSA.Create(2048);
            var provider = new Provider
            {
                Id = 17,
                ProviderName = "VertexTest",
                ProviderType = ProviderType.Vertex,
                IsEnabled = true,
                Settings = new Dictionary<string, string>
                {
                    ["project_id"] = "test-project",
                    ["location"] = "us-central1"
                }
            };
            var credential = new ProviderKeyCredential
            {
                Id = 23,
                ProviderId = provider.Id,
                ApiKey = string.Empty,
                SecretSettings = new Dictionary<string, string>
                {
                    ["service_account_json"] = JsonSerializer.Serialize(
                        new Dictionary<string, string>
                        {
                            ["type"] = "service_account",
                            ["client_email"] = "vertex-test@example.iam.gserviceaccount.com",
                            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem()
                        })
                },
                IsEnabled = true
            };

            var client = _factory.CreateTestClient(provider, credential);

            var contextClient = Assert.IsType<ContextAwareLLMClient>(client);
            Assert.IsType<VertexClient>(contextClient.InnerClient);
        }

        [Fact]
        public void CreateTestClient_WithVertexMissingServiceAccount_ThrowsActionableError()
        {
            var provider = new Provider
            {
                ProviderType = ProviderType.Vertex,
                Settings = new Dictionary<string, string>
                {
                    ["project_id"] = "test-project",
                    ["location"] = "us-central1"
                }
            };
            var credential = new ProviderKeyCredential { ApiKey = string.Empty };

            var exception = Assert.Throws<ArgumentException>(
                () => _factory.CreateTestClient(provider, credential));

            Assert.Contains("Service Account JSON", exception.Message);
            Assert.Equal("keyCredential", exception.ParamName);
        }

        [Fact]
        public async Task CreateTestClient_WithProtectedApiKey_RevealsItBeforeTheProviderUsesIt()
        {
            const string plaintext = "sk-provider-plaintext";
            var stored = _protector.Protect(plaintext);
            string? observedApiKey = null;
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                    observedApiKey = request.Headers.Authorization?.Parameter)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
            _mockHttpClientFactory
                .Setup(factory => factory.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler.Object));

            var provider = new Provider
            {
                Id = 17,
                ProviderName = "CredentialTest",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };
            var credential = new ProviderKeyCredential
            {
                Id = 23,
                ProviderId = provider.Id,
                ApiKey = stored,
                IsEnabled = true
            };

            var client = _factory.CreateTestClient(provider, credential);
            var contextClient = Assert.IsType<ContextAwareLLMClient>(client);
            var providerClient = Assert.IsType<OpenAIClient>(contextClient.InnerClient);

            await providerClient.VerifyAuthenticationAsync();

            Assert.Equal(plaintext, observedApiKey);
            Assert.Equal(stored, credential.ApiKey);
        }

        [Fact]
        public void CreateTestClient_WithUnsupportedProvider_ThrowsConfigurationException()
        {
            var provider = new Provider { ProviderType = (ProviderType)int.MaxValue };
            var credential = new ProviderKeyCredential { ApiKey = "test-key" };

            var exception = Assert.Throws<ConfigurationException>(
                () => _factory.CreateTestClient(provider, credential));

            Assert.Contains("Unsupported provider type", exception.Message);
        }
    }
}
