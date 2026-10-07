using ConduitLLM.Persistence;

using Moq;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class MediaLifecycleServiceTests
    {
        #region UpdateAccessStatsAsync Tests

        [Fact]
        public async Task UpdateAccessStatsAsync_WithValidStorageKey_ShouldUpdateAccessStats()
        {
            // Arrange
            var storageKey = "image/test.jpg";
            var mediaRecord = new MediaRuntimeRecord
            {
                Id = Guid.NewGuid(),
                StorageKey = storageKey,
                AccessCount = 5
            };

            _mockMediaStore.Setup(x => x.GetByStorageKeyAsync(storageKey, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mediaRecord);

            _mockMediaStore.Setup(x => x.UpdateAccessStatsAsync(mediaRecord.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Act
            var result = await _service.UpdateAccessStatsAsync(storageKey);

            // Assert
            Assert.True(result);
            _mockMediaStore.Verify(x => x.GetByStorageKeyAsync(storageKey, false, It.IsAny<CancellationToken>()), Times.Once);
            _mockMediaStore.Verify(x => x.UpdateAccessStatsAsync(mediaRecord.Id, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateAccessStatsAsync_WithNonExistentStorageKey_ShouldReturnFalse()
        {
            // Arrange
            var storageKey = "image/non-existent.jpg";

            _mockMediaStore.Setup(x => x.GetByStorageKeyAsync(storageKey, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync((MediaRuntimeRecord?)null);

            // Act
            var result = await _service.UpdateAccessStatsAsync(storageKey);

            // Assert
            Assert.False(result);
            _mockMediaStore.Verify(x => x.GetByStorageKeyAsync(storageKey, false, It.IsAny<CancellationToken>()), Times.Once);
            _mockMediaStore.Verify(x => x.UpdateAccessStatsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task UpdateAccessStatsAsync_WithInvalidStorageKey_ShouldReturnFalse(string invalidStorageKey)
        {
            // Act
            var result = await _service.UpdateAccessStatsAsync(invalidStorageKey);

            // Assert
            Assert.False(result);
            _mockMediaStore.Verify(x => x.GetByStorageKeyAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task UpdateAccessStatsAsync_WithRepositoryException_ShouldReturnFalse()
        {
            // Arrange
            var storageKey = "image/test.jpg";

            _mockMediaStore.Setup(x => x.GetByStorageKeyAsync(storageKey, false, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Repository error"));

            // Act
            var result = await _service.UpdateAccessStatsAsync(storageKey);

            // Assert
            Assert.False(result);
        }

        #endregion

        #region GetStorageStatsByVirtualKeyAsync Tests

        [Fact]
        public async Task GetStorageStatsByVirtualKeyAsync_WithValidVirtualKey_ShouldReturnStats()
        {
            // Arrange
            var virtualKeyId = 1;
            var mediaRecords = new List<MediaRuntimeRecord>
            {
                new MediaRuntimeRecord
                { 
                    Id = Guid.NewGuid(), 
                    VirtualKeyId = virtualKeyId, 
                    MediaType = "image", 
                    SizeBytes = 1024 
                },
                new MediaRuntimeRecord
                { 
                    Id = Guid.NewGuid(), 
                    VirtualKeyId = virtualKeyId, 
                    MediaType = "image", 
                    SizeBytes = 2048 
                },
                new MediaRuntimeRecord
                { 
                    Id = Guid.NewGuid(), 
                    VirtualKeyId = virtualKeyId, 
                    MediaType = "video", 
                    SizeBytes = 5000000 
                }
            };

            _mockMediaStore.Setup(x => x.GetByVirtualKeyIdAsync(virtualKeyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mediaRecords);

            // Act
            var result = await _service.GetStorageStatsByVirtualKeyAsync(virtualKeyId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(virtualKeyId, result.VirtualKeyId);
            Assert.Equal(3, result.TotalFiles);
            Assert.Equal(5003072, result.TotalSizeBytes); // 1024 + 2048 + 5000000

            Assert.Equal(2, result.ByMediaType["image"].FileCount);
            Assert.Equal(3072, result.ByMediaType["image"].SizeBytes); // 1024 + 2048

            Assert.Equal(1, result.ByMediaType["video"].FileCount);
            Assert.Equal(5000000, result.ByMediaType["video"].SizeBytes);
        }

        [Fact]
        public async Task GetStorageStatsByVirtualKeyAsync_WithNoMediaRecords_ShouldReturnEmptyStats()
        {
            // Arrange
            var virtualKeyId = 1;

            _mockMediaStore.Setup(x => x.GetByVirtualKeyIdAsync(virtualKeyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MediaRuntimeRecord>());

            // Act
            var result = await _service.GetStorageStatsByVirtualKeyAsync(virtualKeyId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(virtualKeyId, result.VirtualKeyId);
            Assert.Equal(0, result.TotalFiles);
            Assert.Equal(0, result.TotalSizeBytes);
            Assert.Empty(result.ByMediaType);
        }

        [Fact]
        public async Task GetStorageStatsByVirtualKeyAsync_WithNullSizeBytes_ShouldTreatAsZero()
        {
            // Arrange
            var virtualKeyId = 1;
            var mediaRecords = new List<MediaRuntimeRecord>
            {
                new MediaRuntimeRecord
                { 
                    Id = Guid.NewGuid(), 
                    VirtualKeyId = virtualKeyId, 
                    MediaType = "image", 
                    SizeBytes = null 
                },
                new MediaRuntimeRecord
                { 
                    Id = Guid.NewGuid(), 
                    VirtualKeyId = virtualKeyId, 
                    MediaType = "image", 
                    SizeBytes = 1024 
                }
            };

            _mockMediaStore.Setup(x => x.GetByVirtualKeyIdAsync(virtualKeyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mediaRecords);

            // Act
            var result = await _service.GetStorageStatsByVirtualKeyAsync(virtualKeyId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.TotalFiles);
            Assert.Equal(1024, result.TotalSizeBytes); // null treated as 0
            Assert.Equal(2, result.ByMediaType["image"].FileCount);
            Assert.Equal(1024, result.ByMediaType["image"].SizeBytes);
        }

        #endregion

        #region GetOverallStorageStatsAsync Tests

        [Fact]
        public async Task GetOverallStorageStatsAsync_ShouldReturnOverallStats()
        {
            // Arrange
            var byProvider = new Dictionary<string, long>
            {
                ["openai"] = 1000000,
                ["minimax"] = 2000000
            };

            var allMedia = new List<MediaRuntimeRecord>
            {
                new MediaRuntimeRecord { Id = Guid.NewGuid(), MediaType = "image", SizeBytes = 200000 },
                new MediaRuntimeRecord { Id = Guid.NewGuid(), MediaType = "image", SizeBytes = 300000 },
                new MediaRuntimeRecord { Id = Guid.NewGuid(), MediaType = "video", SizeBytes = 2500000 }
            };

            _mockMediaStore.Setup(x => x.GetAggregateStorageStatsAsync(
                    null,
                    100,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaRuntimeStorageAggregate
                {
                    TotalFiles = allMedia.Count,
                    TotalSizeBytes = allMedia.Sum(media => media.SizeBytes ?? 0),
                    ByProvider = byProvider,
                    ByMediaType =
                    [
                        new MediaRuntimeTypeAggregate("image", 2, 500000),
                        new MediaRuntimeTypeAggregate("video", 1, 2500000)
                    ]
                });

            // Act
            var result = await _service.GetOverallStorageStatsAsync();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(3000000, result.TotalSizeBytes); // Sum of all media sizes
            Assert.Equal(3, result.TotalFiles);
            Assert.Equal(0, result.OrphanedFiles);
            Assert.Equal(byProvider, result.ByProvider);
            
            // Check byMediaType structure
            Assert.Equal(2, result.ByMediaType.Count);
            Assert.Equal(2, result.ByMediaType["image"].FileCount);
            Assert.Equal(500000, result.ByMediaType["image"].SizeBytes);
            Assert.Equal(1, result.ByMediaType["video"].FileCount);
            Assert.Equal(2500000, result.ByMediaType["video"].SizeBytes);
        }

        #endregion

        #region GetMediaByVirtualKeyAsync Tests

        [Fact]
        public async Task GetMediaByVirtualKeyAsync_WithValidVirtualKey_ShouldReturnMediaRecords()
        {
            // Arrange
            var virtualKeyId = 1;
            var mediaRecords = new List<MediaRuntimeRecord>
            {
                new MediaRuntimeRecord { Id = Guid.NewGuid(), VirtualKeyId = virtualKeyId },
                new MediaRuntimeRecord { Id = Guid.NewGuid(), VirtualKeyId = virtualKeyId }
            };

            _mockMediaStore.Setup(x => x.GetByVirtualKeyIdAsync(virtualKeyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(mediaRecords);

            // Act
            var result = await _service.GetMediaByVirtualKeyAsync(virtualKeyId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal(mediaRecords.Select(media => media.Id), result.Select(media => media.Id));
        }

        [Fact]
        public async Task GetMediaByVirtualKeyAsync_WithRepositoryException_ShouldThrowException()
        {
            // Arrange
            var virtualKeyId = 1;

            _mockMediaStore.Setup(x => x.GetByVirtualKeyIdAsync(virtualKeyId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Repository error"));

            // Act & Assert
            await Assert.ThrowsAsync<Exception>(() => _service.GetMediaByVirtualKeyAsync(virtualKeyId));
        }

        #endregion
    }
}
