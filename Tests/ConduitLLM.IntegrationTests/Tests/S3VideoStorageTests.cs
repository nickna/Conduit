using Amazon.S3;
using Amazon.S3.Model;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Options;
using ConduitLLM.Core.Services;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ConduitLLM.IntegrationTests.Tests;

[CollectionDefinition("S3Storage")]
public sealed class S3StorageCollection : ICollectionFixture<S3StorageFixture>;

public sealed class S3StorageFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("minio/minio:RELEASE.2025-04-22T22-12-26Z")
        .WithEnvironment("MINIO_ROOT_USER", "conduitci")
        .WithEnvironment("MINIO_ROOT_PASSWORD", "conduit-ci-storage-only")
        .WithCommand("server", "/data")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
            .ForPort(9000).ForPath("/minio/health/ready")))
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();
    public S3MediaStorageService CreateService()
    {
        var endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";
        var options = new S3StorageOptions
        {
            AccessKey = "conduitci", SecretKey = "conduit-ci-storage-only",
            BucketName = "conduit-ci-videos", ServiceUrl = endpoint, ForcePathStyle = true,
            AutoConfigureCors = false
        };
        var client = new AmazonS3Client(options.AccessKey, options.SecretKey, new AmazonS3Config
        {
            ServiceURL = endpoint, ForcePathStyle = true, UseHttp = true,
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED
        });
        return new S3MediaStorageService(Options.Create(options), NullLogger<S3MediaStorageService>.Instance, s3Client: client);
    }
}

[Collection("S3Storage")]
[Trait("Component", "S3Storage")]
public sealed class S3VideoStorageTests(S3StorageFixture fixture)
{
    private static readonly byte[] Video = Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray();
    private static VideoMediaMetadata Metadata => new()
    {
        ContentType = "video/mp4", FileName = "ci-video.mp4", Width = 1920, Height = 1080,
        Resolution = "1920x1080", Duration = 30, FrameRate = 30
    };

    [Fact]
    public async Task SmallVideoUpload_PersistsBytesAndMetadata()
    {
        using var service = fixture.CreateService();
        using var content = new MemoryStream(Video);
        var stored = await service.StoreVideoAsync(content, Metadata);
        Assert.Equal(Video.Length, stored.SizeBytes);
        Assert.EndsWith(".mp4", stored.StorageKey);
        var video = await service.GetVideoStreamAsync(stored.StorageKey);
        Assert.NotNull(video);
        await using var stream = video.Stream;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(Video, bytes.ToArray());
        Assert.Equal("video/mp4", video.ContentType);
    }

    [Fact]
    public async Task FullVideoRange_ReturnsCompleteStreamAndBounds()
    {
        using var service = fixture.CreateService();
        using var content = new MemoryStream(Video);
        var stored = await service.StoreVideoAsync(content, Metadata);
        var video = await service.GetVideoStreamAsync(stored.StorageKey);
        Assert.NotNull(video);
        await using var stream = video.Stream;
        Assert.Equal(0, video.RangeStart);
        Assert.Equal(Video.Length - 1, video.RangeEnd);
        Assert.Equal(Video.Length, video.TotalSize);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(Video, bytes.ToArray());
    }

    [Fact]
    public async Task PartialVideoRange_ReturnsRequestedBytesAndFullObjectSize()
    {
        using var service = fixture.CreateService();
        using var content = new MemoryStream(Video);
        var stored = await service.StoreVideoAsync(content, Metadata);
        var video = await service.GetVideoStreamAsync(stored.StorageKey, 100, 200);
        Assert.NotNull(video);
        await using var stream = video.Stream;
        Assert.Equal(100, video.RangeStart);
        Assert.Equal(200, video.RangeEnd);
        Assert.Equal(Video.Length, video.TotalSize);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(Video[100..201], bytes.ToArray());
    }
}
