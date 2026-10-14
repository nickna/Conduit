#nullable enable

using System.Net;
using System.Text.Json;

using ConduitLLM.Admin.Filters;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Middleware;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Moq;

using AdminProgram = ConduitLLM.Admin.Program;

namespace ConduitLLM.Tests.Admin.Filters;

public sealed class IpFilterConditionalRequestHttpTests
{
    [Theory]
    [InlineData("PATCH", null, StatusCodes.Status428PreconditionRequired)]
    [InlineData("PATCH", "\"stale\"", StatusCodes.Status412PreconditionFailed)]
    [InlineData("DELETE", null, StatusCodes.Status428PreconditionRequired)]
    [InlineData("DELETE", "\"stale\"", StatusCodes.Status412PreconditionFailed)]
    public async Task PreconditionResultsUseProductionProblemWriterAndCorrelation(
        string method, string? ifMatch, int expectedStatus)
    {
        const int filterId = 73;
        var row = new IpFilterEntity
        {
            Id = filterId,
            RowVersion = [1, 2, 3, 4],
            UpdatedAt = new DateTime(2026, 10, 11, 12, 34, 56, DateTimeKind.Utc)
        };
        var repository = new Mock<IIpFilterRepository>(MockBehavior.Strict);
        CancellationToken requestToken = default;
        repository.Setup(value => value.GetByIdAsync(filterId, It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((_, token) => Assert.Equal(requestToken, token))
            .ReturnsAsync(row);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        AdminProgram.ConfigureHttpServices(builder);
        await using var app = builder.Build();
        app.UseCorrelationId();
        app.Use((context, next) =>
        {
            requestToken = context.RequestAborted;
            return next(context);
        });
        var handlerCalls = 0;
        app.MapMethods("/v1/admin/ip-filters/{id:int}", [method], (int id) =>
        {
            Assert.Equal(filterId, id);
            handlerCalls++;
            return Results.NoContent();
        }).AddEndpointFilter(VersionedResourceEndpointFilter.CreateWithRuntimePersistence(repository.Object));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(deadline.Token);
        try
        {
            using var client = app.GetTestClient();
            var correlation = Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(new HttpMethod(method), $"/v1/admin/ip-filters/{filterId}");
            request.Headers.Add("x-request-id", correlation);
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            using var response = await client.SendAsync(request, deadline.Token);

            Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(correlation, Assert.Single(response.Headers.GetValues("x-request-id")));
            Assert.False(response.Headers.Contains("ETag"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var problem = body.RootElement;
            Assert.Equal(JsonValueKind.Object, problem.ValueKind);
            var expectedProperties = expectedStatus == 428
                ? new[] { "code", "detail", "status", "title", "traceId" }
                : new[] { "code", "detail", "status", "title", "traceId", "type" };
            Assert.Equal(expectedProperties,
                problem.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(expectedStatus, problem.GetProperty("status").GetInt32());
            if (expectedStatus == 428)
                Assert.False(problem.TryGetProperty("type", out _));
            else
                Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
            Assert.Equal("request_failed", problem.GetProperty("code").GetString());
            Assert.Equal(correlation, problem.GetProperty("traceId").GetString());
            Assert.Equal(expectedStatus == 428 ? "If-Match is required" : "Resource version mismatch",
                problem.GetProperty("title").GetString());
            Assert.Equal(expectedStatus == 428
                ? "Fetch the resource and send its ETag in the If-Match header."
                : "The resource changed after it was read. Fetch it again and retry.",
                problem.GetProperty("detail").GetString());
            Assert.Equal(0, handlerCalls);
            repository.Verify(value => value.GetByIdAsync(filterId, requestToken), Times.Once);
            repository.VerifyNoOtherCalls();
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, row.RowVersion);
            Assert.Equal(new DateTime(2026, 10, 11, 12, 34, 56, DateTimeKind.Utc), row.UpdatedAt);
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(shutdown.Token);
        }
    }
}
