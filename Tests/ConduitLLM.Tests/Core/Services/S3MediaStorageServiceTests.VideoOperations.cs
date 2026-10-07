using System.Net;

using Amazon.S3;
using Amazon.S3.Model;

using ConduitLLM.Core.Models;

using Moq;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class S3MediaStorageServiceTests
    {
        #region StoreVideoAsync Tests

        [Fact]
        public async Task StoreVideoAsync_WithLargeVideo_ShouldUseMultipartUpload()
        {
            // Arrange
            var videoContent = new byte[150 * 1024 * 1024]; // 150MB (over 100MB threshold)
            var content = new MemoryStream(videoContent);
            var metadata = new VideoMediaMetadata
            {
                ContentType = "video/mp4",
                FileName = "large.mp4",
                Duration = 300,
                Resolution = "1920x1080",
                Width = 1920,
                Height = 1080,
                FrameRate = 30.0
            };

            var progressCallbacks = new List<long>();

            // Setup multipart upload mocks
            var initiateResponse = new InitiateMultipartUploadResponse
            {
                BucketName = _options.BucketName,
                Key = "video/test-key.mp4",
                UploadId = "test-upload-id"
            };

            var uploadPartResponse = new UploadPartResponse
            {
                ETag = "part-etag",
                HttpStatusCode = HttpStatusCode.OK
            };

            var completeResponse = new CompleteMultipartUploadResponse
            {
                BucketName = _options.BucketName,
                Key = "video/test-key.mp4",
                ETag = "complete-etag",
                Location = "https://s3.amazonaws.com/test-bucket/video/test-key.mp4"
            };

            _mockS3Client.Setup(x => x.InitiateMultipartUploadAsync(It.IsAny<InitiateMultipartUploadRequest>(), default))
                .ReturnsAsync(initiateResponse);

            _mockS3Client.Setup(x => x.UploadPartAsync(It.IsAny<UploadPartRequest>(), default))
                .ReturnsAsync(uploadPartResponse);

            _mockS3Client.Setup(x => x.CompleteMultipartUploadAsync(It.IsAny<CompleteMultipartUploadRequest>(), default))
                .ReturnsAsync(completeResponse);

            // Act
            var result = await _service.StoreVideoAsync(content, metadata, progressCallbacks.Add);

            // Assert
            Assert.NotNull(result);
            Assert.StartsWith("video/", result.StorageKey);
            Assert.EndsWith(".mp4", result.StorageKey);
            Assert.Equal(videoContent.Length, result.SizeBytes);
            Assert.NotEmpty(progressCallbacks);

            // Verify multipart upload was used
            _mockS3Client.Verify(x => x.InitiateMultipartUploadAsync(It.IsAny<InitiateMultipartUploadRequest>(), default), 
                Times.Once);
            _mockS3Client.Verify(x => x.UploadPartAsync(It.IsAny<UploadPartRequest>(), default), 
                Times.AtLeastOnce);
            _mockS3Client.Verify(x => x.CompleteMultipartUploadAsync(It.IsAny<CompleteMultipartUploadRequest>(), default), 
                Times.Once);
        }

        [Fact]
        public async Task StoreVideoAsync_WithShortReads_FillsEveryNonFinalMultipartPart()
        {
            var totalLength = 105L * 1024 * 1024 + 123;
            using var content = new ShortReadSeekableStream(totalLength, 1024 * 1024);
            var metadata = new VideoMediaMetadata
            {
                ContentType = "video/mp4",
                FileName = "short-reads.mp4"
            };
            var uploadedPartSizes = new List<long>();
            _mockS3Client
                .Setup(client => client.InitiateMultipartUploadAsync(
                    It.IsAny<InitiateMultipartUploadRequest>(), default))
                .ReturnsAsync(new InitiateMultipartUploadResponse
                {
                    BucketName = _options.BucketName,
                    Key = "video/short-reads.mp4",
                    UploadId = "short-read-upload"
                });
            _mockS3Client
                .Setup(client => client.UploadPartAsync(It.IsAny<UploadPartRequest>(), default))
                .Returns<UploadPartRequest, CancellationToken>((request, _) =>
                {
                    uploadedPartSizes.Add(request.InputStream.Length);
                    return Task.FromResult(new UploadPartResponse { ETag = $"part-{request.PartNumber}" });
                });
            _mockS3Client
                .Setup(client => client.CompleteMultipartUploadAsync(
                    It.IsAny<CompleteMultipartUploadRequest>(), default))
                .ReturnsAsync(new CompleteMultipartUploadResponse { ETag = "complete" });

            var result = await _service.StoreVideoAsync(content, metadata);

            Assert.Equal(totalLength, result.SizeBytes);
            Assert.True(uploadedPartSizes.Count > 1);
            Assert.All(uploadedPartSizes.SkipLast(1), size =>
                Assert.Equal(_options.MultipartChunkSizeBytes, size));
            Assert.InRange(uploadedPartSizes[^1], 1, _options.MultipartChunkSizeBytes);
            Assert.Equal(totalLength, uploadedPartSizes.Sum());
        }

        private sealed class ShortReadSeekableStream(long length, int maximumReadSize) : Stream
        {
            private long _position;

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position
            {
                get => _position;
                set => _position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var bytesRead = (int)Math.Min(Math.Min(count, maximumReadSize), length - _position);
                _position += bytesRead;
                return bytesRead;
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Read(buffer.Span));
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                _position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                return _position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        #endregion

        #region GetVideoStreamAsync Tests

        [Fact]
        public async Task GetVideoStreamAsync_WithNonExistentKey_ShouldReturnNull()
        {
            // Arrange
            var storageKey = "non-existent-key";

            _mockS3Client.Setup(x => x.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), default))
                .ThrowsAsync(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });

            // Act
            var rangedStream = await _service.GetVideoStreamAsync(storageKey);

            // Assert
            Assert.Null(rangedStream);
        }

        #endregion
    }
}
