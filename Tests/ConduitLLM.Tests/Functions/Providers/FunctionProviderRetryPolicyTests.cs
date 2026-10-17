using System.Net;

using ConduitLLM.Core.Extensions;

using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.Functions.Providers;

public sealed class FunctionProviderRetryPolicyTests
{
    [Theory]
    [InlineData("ExaFunctionClient")]
    [InlineData("TavilyFunctionClient")]
    public async Task DefaultProviderClientsDoNotRetryMethodsOutsideTheSafeAllowlist(string clientName)
    {
        HttpMethod[] methods = [HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete,
            HttpMethod.Trace, new("CUSTOM")];
        foreach (var method in methods)
        foreach (var failure in new[] { "connection", "unavailable", "rate-limit" })
        {
            using var unavailable = new HttpResponseMessage(failure == "rate-limit"
                ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable);
            using var handler = new CountingHandler((_, _) => failure == "connection"
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Response lost after acceptance."))
                : Task.FromResult(unavailable));
            using var provider = CreateProvider(clientName, handler);
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            using var request = new HttpRequestMessage(method, "http://provider.invalid/operation")
            {
                Content = new StringContent("synthetic request")
            };

            if (failure == "connection")
                await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
            else
            {
                using var response = await client.SendAsync(request);
                Assert.Equal(unavailable.StatusCode, response.StatusCode);
            }
            Assert.Equal(1, handler.Attempts);
        }
    }

    [Theory]
    [InlineData("ExaFunctionClient", "GET")]
    [InlineData("ExaFunctionClient", "HEAD")]
    [InlineData("ExaFunctionClient", "OPTIONS")]
    [InlineData("TavilyFunctionClient", "GET")]
    [InlineData("TavilyFunctionClient", "HEAD")]
    [InlineData("TavilyFunctionClient", "OPTIONS")]
    public async Task DefaultProviderClientsRetainRetriesForExplicitSafeMethods(string clientName, string method)
    {
        var attempts = 0;
        using var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var success = new HttpResponseMessage(HttpStatusCode.OK);
        using var handler = new CountingHandler((_, _) =>
            Task.FromResult(++attempts == 1 ? unavailable : success));
        using var provider = CreateProvider(clientName, handler);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://provider.invalid/probe");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    [Theory]
    [InlineData("ExaFunctionClient.SingleDispatch")]
    [InlineData("TavilyFunctionClient.SingleDispatch")]
    public async Task KeyedTransportStillDoesNotRetryAnyMethod(string clientName)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Options, HttpMethod.Post })
        {
            using var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            using var handler = new CountingHandler((_, _) => Task.FromResult(unavailable));
            using var provider = CreateProvider(clientName, handler);
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            using var request = new HttpRequestMessage(method, "http://provider.invalid/operation");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(1, handler.Attempts);
        }
    }

    [Theory]
    [InlineData("ExaFunctionClient", "GET")]
    [InlineData("ExaFunctionClient", "POST")]
    [InlineData("TavilyFunctionClient", "GET")]
    [InlineData("TavilyFunctionClient", "POST")]
    public async Task CallerCancellationDoesNotRetry(string clientName, string method)
    {
        using var caller = new CancellationTokenSource();
        using var handler = new CountingHandler((_, token) =>
        {
            caller.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var provider = CreateProvider(clientName, handler);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://provider.invalid/operation");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, caller.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, handler.Attempts);
    }

    private static ServiceProvider CreateProvider(string clientName, HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSharedHttpClients();
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return send(request, cancellationToken);
        }
    }
}
