using System.Net;
using System.Text;
using System.Text.Json;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Services;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class FunctionCustomerErrorTests
{
    private const string Diagnostic = "Exa upstream private diagnostic {\"code\":\"private_request\"}";

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, "provider_request_error", "invalid_request_error")]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.BadGateway, "provider_authentication_error", "server_error")]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.BadGateway, "provider_authentication_error", "server_error")]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, "rate_limit_exceeded", "rate_limit_error")]
    [InlineData(HttpStatusCode.RequestTimeout, HttpStatusCode.ServiceUnavailable, "provider_timeout", "server_error")]
    [InlineData(HttpStatusCode.GatewayTimeout, HttpStatusCode.GatewayTimeout, "provider_timeout", "server_error")]
    public async Task FunctionFailure_PreservesStatusAndClassificationInBothModes(
        HttpStatusCode upstreamStatus, HttpStatusCode expectedStatus, string expectedCode, string expectedType)
    {
        foreach (var mode in new[] { CustomerErrorMode.External, CustomerErrorMode.Internal })
        {
            var service = FailingService(upstreamStatus, Diagnostic);
            await using var host = await GatewayEndpointTestHost.StartAsync(services =>
                Configure(services, mode, service.Object, EnabledConfiguration()));
            using var response = await host.Client.PostAsync("/v1/conduit/functions/execute", Request());
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expectedStatus, body);
            AssertVisibility(body, mode);
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            Assert.Equal(expectedCode, error.GetProperty("code").GetString());
            Assert.Equal(expectedType, error.GetProperty("type").GetString());
            if (mode == CustomerErrorMode.Internal)
            {
                var detail = error.GetProperty("metadata").GetProperty("provider_error");
                Assert.Equal("Exa", detail.GetProperty("provider").GetString());
                Assert.Equal((int)upstreamStatus, detail.GetProperty("upstream_status").GetInt32());
                Assert.Equal(Diagnostic, detail.GetProperty("raw_message").GetString());
                Assert.False(string.IsNullOrEmpty(detail.GetProperty("error_type").GetString()));
            }
            else
            {
                Assert.False(error.TryGetProperty("metadata", out var metadata) &&
                    metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("provider_error", out _));
            }
        }
    }

    [Theory]
    [InlineData(CustomerErrorMode.External, CustomerErrorMode.External)]
    [InlineData(CustomerErrorMode.Internal, CustomerErrorMode.Internal)]
    [InlineData(CustomerErrorMode.Internal, CustomerErrorMode.External)]
    [InlineData(CustomerErrorMode.External, CustomerErrorMode.Internal)]
    public async Task FunctionFailure_PersistsPrivateDiagnosticAndFreshHostUsesItsOwnMode(
        CustomerErrorMode writerMode, CustomerErrorMode readerMode)
    {
        await using var environment = await FunctionExecutionTestEnvironment.CreateAsync();
        await using (var writer = await GatewayEndpointTestHost.StartAsync(services =>
            Configure(services, writerMode, environment.CreateService(), environment.CreateConfigurationRepository())))
        {
            using var response = await writer.Client.PostAsync("/v1/conduit/functions/execute", Request());
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
            AssertVisibility(body, writerMode);
        }

        Guid executionId;
        string privateDiagnostic;
        await using (var db = environment.Database.CreateContext())
        {
            var persisted = await db.FunctionExecutions.AsNoTracking().SingleAsync();
            executionId = persisted.Id;
            privateDiagnostic = persisted.ErrorMessage;
            Assert.Equal(ExecutionState.Failed, persisted.State);
            Assert.Contains(FunctionExecutionTestEnvironment.Diagnostic, privateDiagnostic);
            using var details = JsonDocument.Parse(persisted.CostCalculationDetails);
            Assert.Equal(400, details.RootElement.GetProperty("httpStatusCode").GetInt32());
        }

        // New service/repository instances and contexts load the persisted row after the writer host stops.
        await using var reader = await GatewayEndpointTestHost.StartAsync(services =>
            Configure(services, readerMode, environment.CreateService(), environment.CreateConfigurationRepository()));
        using var stored = await reader.Client.GetAsync($"/v1/conduit/functions/executions/{executionId}");
        var storedBody = await stored.Content.ReadAsStringAsync();
        Assert.True(stored.StatusCode == HttpStatusCode.OK, storedBody);
        AssertVisibility(storedBody, readerMode);
        using var storedDocument = JsonDocument.Parse(storedBody);
        Assert.Equal(400, storedDocument.RootElement.GetProperty("cost").GetProperty("breakdown")
            .GetProperty("httpStatusCode").GetInt32());
        var afterRead = await environment.CreateExecutionRepository().GetByIdAsync(executionId);
        Assert.Equal(privateDiagnostic, afterRead.ErrorMessage);
        Assert.Equal(1, environment.ProviderRequests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"httpStatusCode\":null}")]
    [InlineData("{\"httpStatusCode\":\"429\"}")]
    [InlineData("{\"httpStatusCode\":{}}")]
    [InlineData("{\"httpStatusCode\":2147483648}")]
    [InlineData("invalid legacy JSON")]
    public async Task LegacyFailure_WithMissingOrMalformedStatusMetadata_RemainsReadable(string costDetails)
    {
        await using var environment = await FunctionExecutionTestEnvironment.CreateAsync();
        var execution = new FunctionExecution
        {
            Id = Guid.NewGuid(), VirtualKeyId = 1, FunctionConfigurationId = 7,
            State = ExecutionState.Failed, ErrorMessage = Diagnostic,
            CostCalculationDetails = costDetails
        };
        await environment.CreateExecutionRepository().CreateAsync(execution);
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
            Configure(services, CustomerErrorMode.External, environment.CreateService(), environment.CreateConfigurationRepository()));
        using var response = await host.Client.GetAsync($"/v1/conduit/functions/executions/{execution.Id}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        AssertVisibility(body, CustomerErrorMode.External);
        Assert.Equal(Diagnostic, (await environment.CreateExecutionRepository().GetByIdAsync(execution.Id)).ErrorMessage);
    }

    [Fact]
    public async Task InternalFailure_RedactsAndTruncatesProviderDetail()
    {
        const string secret = "private-signed-token";
        var raw = $"Exa upstream private diagnostic https://example.test/private?token={secret} " + new string('x', 2100);
        var service = FailingService(HttpStatusCode.BadRequest, raw);
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
            Configure(services, CustomerErrorMode.Internal, service.Object, EnabledConfiguration()));
        using var response = await host.Client.PostAsync("/v1/conduit/functions/execute", Request());
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.DoesNotContain(secret, body);
        using var document = JsonDocument.Parse(body);
        var detail = document.RootElement.GetProperty("error").GetProperty("metadata").GetProperty("provider_error");
        var rawMessage = detail.GetProperty("raw_message").GetString();
        Assert.Contains("[REDACTED]", rawMessage);
        Assert.Equal(2001, rawMessage.Length);
        Assert.EndsWith("…", rawMessage);
    }

    [Fact]
    public async Task FailedExecutionReturnedByExecute_AlsoTranslatesLegacyDiagnostics()
    {
        var service = new Mock<IFunctionExecutionService>();
        service.Setup(s => s.ExecuteAsync(7, 1, It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FunctionExecution { State = ExecutionState.Failed, ErrorMessage = Diagnostic });
        var configuration = new Mock<IFunctionConfigurationRepository>();
        configuration.Setup(s => s.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FunctionConfiguration { ConfigurationName = "search", Id = 7, IsEnabled = true });
        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
        {
            AddBalanceValidation(services);
            services.AddSingleton(service.Object);
            services.AddSingleton(configuration.Object);
            services.AddSingleton(new FunctionParameterValidationService(NullLogger<FunctionParameterValidationService>.Instance));
            services.AddSingleton<IProviderErrorTranslator>(new ProviderErrorTranslator(new CustomerErrorOptions()));
            services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                ConduitLLM.Gateway.Options.GatewayJsonOptions.Configure(options.SerializerOptions));
        });
        using var response = await host.Client.PostAsync("/v1/conduit/functions/execute",
            new StringContent("{\"function_configuration_id\":7}", Encoding.UTF8, "application/json"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        AssertVisibility(await response.Content.ReadAsStringAsync(), CustomerErrorMode.External);
    }

    private static void AddBalanceValidation(IServiceCollection services)
    {
        var keys = new Mock<IVirtualKeyRuntimeService>();
        keys.Setup(s => s.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(ConduitLLM.Core.Models.VirtualKeyValidationOutcome.Success(
                new ConduitLLM.Configuration.Entities.VirtualKey { Id = 1, IsEnabled = true }));
        services.AddSingleton(keys.Object);
    }

    private static void Configure(IServiceCollection services, CustomerErrorMode mode,
        IFunctionExecutionService executionService, IFunctionConfigurationRepository configurations)
    {
        AddBalanceValidation(services);
        services.AddSingleton(executionService);
        services.AddSingleton(configurations);
        services.AddSingleton(new FunctionParameterValidationService(NullLogger<FunctionParameterValidationService>.Instance));
        services.AddSingleton<IProviderErrorTranslator>(new ProviderErrorTranslator(new CustomerErrorOptions { Mode = mode }));
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            ConduitLLM.Gateway.Options.GatewayJsonOptions.Configure(options.SerializerOptions));
    }

    private static IFunctionConfigurationRepository EnabledConfiguration()
    {
        var configurations = new Mock<IFunctionConfigurationRepository>();
        configurations.Setup(repository => repository.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FunctionConfiguration { ConfigurationName = "search", Id = 7, IsEnabled = true });
        return configurations.Object;
    }

    private static Mock<IFunctionExecutionService> FailingService(HttpStatusCode status, string diagnostic)
    {
        var service = new Mock<IFunctionExecutionService>();
        service.Setup(s => s.ExecuteAsync(7, 1, It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FunctionCommunicationException("Exa", diagnostic, status, diagnostic));
        return service;
    }

    private static StringContent Request() => new(
        "{\"function_configuration_id\":7,\"parameters\":{\"query\":\"test\"}}", Encoding.UTF8, "application/json");

    private static void AssertVisibility(string body, CustomerErrorMode mode)
    {
        if (mode == CustomerErrorMode.External)
        {
            Assert.DoesNotContain("private diagnostic", body);
            Assert.DoesNotContain("private_request", body);
            Assert.DoesNotContain("Exa", body);
        }
        else
        {
            Assert.Contains("private diagnostic", body);
            Assert.Contains("Exa", body);
        }
    }
}

