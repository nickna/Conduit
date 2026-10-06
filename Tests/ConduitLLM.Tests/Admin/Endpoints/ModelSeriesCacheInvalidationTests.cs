using System.Net;
using System.Text;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Events;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ConduitLLM.Tests.Admin.Endpoints;

public sealed class ModelSeriesCacheInvalidationTests
{
    [Fact]
    public async Task UpdatingInheritedParametersPublishesRoutingAndDiscoveryExpirationAfterPersistence()
    {
        var series = new ModelSeries { Id = 1, AuthorId = 2, Author = new() { Id = 2, Name = "Author" }, Name = "Series" };
        var repository = new Mock<IModelSeriesRepository>();
        repository.Setup(repo => repo.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(series);
        var operations = new List<string>();
        repository.Setup(repo => repo.UpdateAsync(series, It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("saved")).ReturnsAsync(true);
        var bus = new Mock<IEventBus>();
        bus.Setup(events => events.PublishAsync(It.IsAny<DiscoveryCacheInvalidationRequested>(), It.IsAny<CancellationToken>()))
            .Callback(() => operations.Add("published")).Returns(Task.CompletedTask);
        using var host = AdminEndpointTestHost.Create(services =>
        {
            services.AddSingleton(repository.Object);
            services.AddSingleton(bus.Object);
        }, endpoints => endpoints.MapModelSeriesEndpoints());
        var response = await host.Client.PatchAsync("/v1/admin/model-series/1",
            new StringContent("""{"parameters":{"temperature":{"default":0.8}}}""", Encoding.UTF8, "application/merge-patch+json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("0.8", series.Parameters);
        Assert.Equal(new[] { "saved", "published" }, operations);
    }
}
