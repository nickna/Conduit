using ConduitLLM.Core.Services;
using ConduitLLM.Persistence;

using Moq;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class MediaLifecycleServiceTests
    {
        private MediaLifecycleService CreateStatsService() => new(
            _mockMediaStore.Object,
            _mockLogger.Object);

        [Fact]
        public async Task GetOverallStorageStatsAsync_WithoutGroupId_ReturnsAllStats()
        {
            // Arrange
            var service = CreateStatsService();

            var providerStats = new Dictionary<string, long>
            {
                { "Replicate", 3000 },
                { "OpenAI", 5000 }
            };

            _mockMediaStore.Setup(x => x.GetAggregateStorageStatsAsync(
                    null,
                    100,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaRuntimeStorageAggregate
                {
                    TotalFiles = 3,
                    TotalSizeBytes = 8000,
                    ByProvider = providerStats,
                    ByMediaType =
                    [
                        new MediaRuntimeTypeAggregate("image", 2, 3000),
                        new MediaRuntimeTypeAggregate("video", 1, 5000)
                    ],
                    TopVirtualKeys =
                    [
                        new MediaRuntimeVirtualKeyAggregate(2, 5000),
                        new MediaRuntimeVirtualKeyAggregate(1, 3000)
                    ]
                });
            // Act
            var result = await service.GetOverallStorageStatsAsync();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(8000, result.TotalSizeBytes);
            Assert.Equal(3, result.TotalFiles);
            Assert.Equal(0, result.OrphanedFiles);
            Assert.Equal(2, result.ByProvider.Count);
            Assert.Equal(2, result.ByMediaType.Count);
            Assert.Equal(2, result.StorageByVirtualKey.Count);
            Assert.Equal(3000L, result.StorageByVirtualKey["1"]);
            Assert.Equal(5000L, result.StorageByVirtualKey["2"]);
        }

        [Fact]
        public async Task GetOverallStorageStatsAsync_WithGroupId_ReturnsFilteredStats()
        {
            // Arrange
            var service = CreateStatsService();
            const int groupId = 1;
            _mockMediaStore.Setup(x => x.GetAggregateStorageStatsAsync(
                    groupId,
                    100,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaRuntimeStorageAggregate
                {
                    TotalFiles = 2,
                    TotalSizeBytes = 3000,
                    ByProvider = new Dictionary<string, long>
                    {
                        ["Replicate"] = 1000,
                        ["OpenAI"] = 2000
                    },
                    ByMediaType =
                    [
                        new MediaRuntimeTypeAggregate("image", 1, 1000),
                        new MediaRuntimeTypeAggregate("video", 1, 2000)
                    ],
                    TopVirtualKeys =
                    [
                        new MediaRuntimeVirtualKeyAggregate(3, 2000),
                        new MediaRuntimeVirtualKeyAggregate(1, 1000)
                    ]
                });

            // Act
            var result = await service.GetOverallStorageStatsAsync(groupId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(3000, result.TotalSizeBytes);
            Assert.Equal(2, result.TotalFiles);
            Assert.Equal(0, result.OrphanedFiles); // No orphaned files when filtering by group
            Assert.Equal(2, result.ByProvider.Count);
            Assert.Equal(1000L, result.ByProvider["Replicate"]);
            Assert.Equal(2000L, result.ByProvider["OpenAI"]);
            Assert.Equal(2, result.StorageByVirtualKey.Count);
        }

        [Fact]
        public async Task GetOverallStorageStatsAsync_WithGroupId_UsesAggregateRepository()
        {
            // Arrange
            const int groupId = 1;
            var service = CreateStatsService();

            _mockMediaStore.Setup(x => x.GetAggregateStorageStatsAsync(
                    groupId,
                    100,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaRuntimeStorageAggregate());

            var result = await service.GetOverallStorageStatsAsync(groupId);

            Assert.Equal(0, result.TotalFiles);
            _mockMediaStore.Verify(x => x.GetAggregateStorageStatsAsync(
                groupId,
                100,
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetOverallStorageStatsAsync_WithEmptyGroup_ReturnsEmptyStats()
        {
            // Arrange
            var service = CreateStatsService();
            const int groupId = 999;
            _mockMediaStore.Setup(x => x.GetAggregateStorageStatsAsync(
                    groupId,
                    100,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaRuntimeStorageAggregate());

            // Act
            var result = await service.GetOverallStorageStatsAsync(groupId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(0, result.TotalSizeBytes);
            Assert.Equal(0, result.TotalFiles);
            Assert.Equal(0, result.OrphanedFiles);
            Assert.Empty(result.ByProvider);
            Assert.Empty(result.ByMediaType);
            Assert.Empty(result.StorageByVirtualKey);
        }

        [Fact]
        public async Task GetOverallStorageStatsAsync_GroupsMediaByType()
        {
            // Arrange
            var service = CreateStatsService();

            _mockMediaStore.Setup(x => x.GetAggregateStorageStatsAsync(
                    null,
                    100,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaRuntimeStorageAggregate
                {
                    TotalFiles = 4,
                    TotalSizeBytes = 11000,
                    ByMediaType =
                    [
                        new MediaRuntimeTypeAggregate("image", 2, 3000),
                        new MediaRuntimeTypeAggregate("video", 2, 8000)
                    ]
                });
            // Act
            var result = await service.GetOverallStorageStatsAsync();

            // Assert
            Assert.Equal(2, result.ByMediaType["image"].FileCount);
            Assert.Equal(3000, result.ByMediaType["image"].SizeBytes);
            Assert.Equal(2, result.ByMediaType["video"].FileCount);
            Assert.Equal(8000, result.ByMediaType["video"].SizeBytes);
        }
    }
}
