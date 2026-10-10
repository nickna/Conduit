using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

[Trait("Category", "Unit")]
[Trait("Component", "MediaLifecycle")]
public sealed class MediaEndpointsSoftDeleteTests
{
    private readonly Mock<IMediaStorageService> _storage = new();
    private readonly Mock<IMediaRuntimeStore> _mediaStore = new();

    [Fact]
    public async Task GetMediaInfo_TombstonedRecord_ReturnsNotFoundWithoutReadingStorage()
    {
        const string storageKey = "tombstoned-media";
        _mediaStore.Setup(store =>
                store.GetByStorageKeyAsync(
                    storageKey,
                    true,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaRuntimeRecord
            {
                Id = Guid.NewGuid(),
                VirtualKeyId = 1,
                StorageKey = storageKey,
                MediaType = "image",
                CreatedAt = DateTime.UtcNow,
                DeletedAt = DateTime.UtcNow
            });

        var result = await CreateEndpoint().GetMediaInfo(storageKey);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _storage.Verify(storage => storage.GetInfoAsync(It.IsAny<string>()), Times.Never);
    }

    private MediaEndpoints CreateEndpoint()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };
        return new MediaEndpoints(
            _storage.Object,
            _mediaStore.Object,
            accessor,
            NullLogger<MediaEndpoints>.Instance);
    }
}
