using System.Net;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public sealed class TasksEndpointsOwnershipTests
{
    [Theory]
    [InlineData("/v1/conduit/tasks/task_owned")]
    [InlineData("/v1/conduit/tasks/task_owned/poll?timeout=1&interval=1")]
    public async Task OwnedStatus_DoesNotExposeOperatorRecoveryEvidenceOrMutateCachedMetadata(string path)
    {
        var taskService = new Mock<IAsyncTaskService>();
        var status = new AsyncTaskStatus { TaskId = "task_owned", State = TaskState.Completed,
            Metadata = new TaskMetadata(1) { Model = "test-model", MediaProvider = new(1, 7, "provider-model", "Test"),
                ProviderInvocationWorkerId = "private-worker", Reconciliation = new() { Evidence = "private-incident",
                    ResultUrls = ["https://provider.example/media?signature=private-token"] },
                ReconciliationHistory = [new() { Evidence = "private-history" }] } };
        taskService.Setup(x => x.GetTaskStatusAsync(status.TaskId, It.IsAny<CancellationToken>())).ReturnsAsync(status);
        taskService.Setup(x => x.PollTaskUntilCompletedAsync(status.TaskId, It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(status);
        await using var host = await GatewayEndpointTestHost.StartAsync(services => services.AddSingleton(taskService.Object));
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("test-model", json);
        Assert.DoesNotContain("private-", json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var metadata = document.RootElement.GetProperty("metadata");
        foreach (var name in new[] { "reconciliation", "reconciliationHistory", "mediaProvider", "providerInvocationWorkerId" })
            Assert.True(!metadata.TryGetProperty(name, out var property) || property.ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.NotNull(status.Metadata.Reconciliation); Assert.NotNull(status.Metadata.MediaProvider);
    }

    [Theory]
    [InlineData("GET", "/v1/conduit/tasks/task_other")]
    [InlineData("GET", "/v1/conduit/tasks/task_other/poll?timeout=1&interval=1")]
    [InlineData("POST", "/v1/conduit/tasks/task_other/cancel")]
    public async Task TaskRoutes_HideTasksOwnedByAnotherVirtualKey(string method, string path)
    {
        var taskService = new Mock<IAsyncTaskService>();
        taskService
            .Setup(service => service.GetTaskStatusAsync(
                "task_other", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsyncTaskStatus
            {
                TaskId = "task_other",
                Metadata = new TaskMetadata(virtualKeyId: 2)
            });

        await using var host = await GatewayEndpointTestHost.StartAsync(services =>
            services.AddSingleton(taskService.Object));
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        taskService.Verify(
            service => service.CancelTaskAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        taskService.Verify(
            service => service.PollTaskUntilCompletedAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
