using System.Reflection;
using System.Text.Json;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Admin.Endpoints;

public sealed class FunctionOperationResponseTests
{
    [Theory]
    [InlineData(true, "verified")]
    [InlineData(false, null)]
    public async Task CredentialTest_SerializesSuccessAndFailureWithProductionOptions(
        bool success, string details)
    {
        var credentials = new Mock<IFunctionCredentialRepository>();
        credentials.Setup(value => value.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FunctionCredential
            {
                Id = 7, ProviderType = FunctionProviderType.Exa, FunctionConfigurationId = 11
            });
        var configurations = new Mock<IFunctionConfigurationRepository>();
        configurations.Setup(value => value.GetByIdAsync(11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FunctionConfiguration { Id = 11, ConfigurationName = "test provider" });
        var client = new Mock<IFunctionClient>();
        client.Setup(value => value.VerifyAuthenticationAsync("override", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FunctionAuthenticationResult
            {
                IsSuccess = success, Message = "provider result", Details = details, ResponseTimeMs = 2.5
            });
        var factory = new Mock<IFunctionClientFactory>();
        factory.Setup(value => value.GetClientAsync(FunctionProviderType.Exa, 11))
            .ReturnsAsync(client.Object);

        var result = await InvokeAsync(typeof(FunctionCredentialsEndpoints), "Test",
            new TestFunctionCredentialRequest { CredentialId = 7, ApiKeyOverride = "override" },
            credentials.Object, configurations.Object, factory.Object,
            Mock.Of<IFunctionCredentialProtector>());
        using var body = await ExecuteAsync(result);

        Assert.Equal(success, body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("provider result", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(details, body.RootElement.GetProperty("details").GetString());
        Assert.Equal(2.5, body.RootElement.GetProperty("durationMs").GetDouble());
        Assert.Equal(4, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task ExecutionCleanup_PreservesCountAndRetentionMessage()
    {
        var repository = new Mock<IFunctionExecutionRepository>();
        repository.Setup(value => value.DeleteOldExecutionsAsync(
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        var result = await InvokeAsync(typeof(FunctionExecutionsEndpoints), "Cleanup",
            repository.Object, new DefaultHttpContext(), NullLoggerFactory.Instance, 14);
        using var body = await ExecuteAsync(result);

        Assert.Equal(3, body.RootElement.GetProperty("deletedCount").GetInt32());
        Assert.Equal("Deleted 3 executions older than 14 days",
            body.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        repository.Verify(value => value.DeleteOldExecutionsAsync(
            It.Is<DateTime>(cutoff => cutoff < DateTime.UtcNow.AddDays(-13)
                && cutoff > DateTime.UtcNow.AddDays(-15)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecutionCleanup_InvalidRetentionDoesNotDeleteExecutions()
    {
        var repository = new Mock<IFunctionExecutionRepository>(MockBehavior.Strict);
        var result = await InvokeAsync(typeof(FunctionExecutionsEndpoints), "Cleanup",
            repository.Object, new DefaultHttpContext(), NullLoggerFactory.Instance, 0);

        using var body = await ExecuteAsync(result, StatusCodes.Status400BadRequest);
        repository.VerifyNoOtherCalls();
    }

    private static Task<IResult> InvokeAsync(Type endpointType, string method, params object[] arguments) =>
        (Task<IResult>)endpointType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments)!;

    private static async Task<JsonDocument> ExecuteAsync(IResult result, int statusCode = 200)
    {
        using var services = new ServiceCollection().AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                AdminJsonOptions.Configure(options.SerializerOptions))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var stream = new MemoryStream();
        context.Response.Body = stream;
        await result.ExecuteAsync(context);
        Assert.Equal(statusCode, context.Response.StatusCode);
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream);
    }
}
