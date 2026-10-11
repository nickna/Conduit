using System.Net;
using ConduitLLM.Configuration;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Utilities;

namespace ConduitLLM.Tests.Core.Services;

public sealed class MediaReconciliationSafetyTests
{
    [Theory]
    [InlineData("https://127.0.0.1/a")] [InlineData("https://10.1.2.3/a")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/a")] [InlineData("https://[::ffff:127.0.0.1]/a")]
    [InlineData("https://[2001:db8::1]/a")] [InlineData("https://user:secret@example.com/a")]
    [InlineData("https://[2001::1]/a")] [InlineData("https://[2002:7f00:1::1]/a")]
    [InlineData("http://example.com/a")] [InlineData("https://example.com:8443/a")]
    public void RecoveryRejectsPrivateAndCredentialBearingUrls(string url) => Assert.False(MediaRecoveryDestination.IsValidUrl(url));

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("2606:4700:4700::1111")]
    public void RecoveryAcceptsPublicAddresses(string ip) => Assert.True(MediaRecoveryDestination.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task InvalidContentCannotEnterStoragePipeline()
    {
        using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<html>provider error, not media</html>"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryMediaValidation.BufferAsync(body, MediaType.Image, CancellationToken.None));
    }

    [Fact]
    public async Task ProviderAcceptanceScopesAreIsolatedAcrossConcurrentInvocationsAndRestoreTheirParent()
    {
        var parent = new List<string>();
        using var outer = new MediaProviderOperationScope((operation, _) => { parent.Add(operation.OperationId!); return Task.CompletedTask; });
        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(async id =>
        {
            var received = new List<string>();
            using (new MediaProviderOperationScope((operation, _) => { received.Add(operation.OperationId!); return Task.CompletedTask; }))
            {
                await Task.Yield();
                await MediaProviderOperationScope.RecordAsync(new(1, id, "model", "provider", id.ToString()), CancellationToken.None);
            }
            return Assert.Single(received);
        }));
        Assert.Equal(new[] { "1", "2" }, results);
        await MediaProviderOperationScope.RecordAsync(new(1, 1, "model", "provider", "parent"), CancellationToken.None);
        Assert.Equal("parent", Assert.Single(parent));
    }
}
