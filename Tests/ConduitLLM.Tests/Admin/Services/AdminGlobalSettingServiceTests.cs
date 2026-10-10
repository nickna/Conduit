using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using AwesomeAssertions;
using ConduitLLM.Configuration.Messaging;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Admin.Services;

public class AdminGlobalSettingServiceTests
{
    private readonly Mock<IGlobalSettingRepository> _mockGlobalSettingRepository;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<ILogger<AdminGlobalSettingService>> _mockLogger;
    private readonly AdminGlobalSettingService _service;

    public AdminGlobalSettingServiceTests()
    {
        _mockGlobalSettingRepository = new Mock<IGlobalSettingRepository>();
        _mockEventBus = new Mock<IEventBus>();
        _mockLogger = new Mock<ILogger<AdminGlobalSettingService>>();

        _service = new AdminGlobalSettingService(
            _mockGlobalSettingRepository.Object,
            _mockLogger.Object,
            _mockEventBus.Object);
    }

    [Fact]
    public async Task GetAllSettingsAsync_ShouldReturnMappedDtos()
    {
        // Arrange
        var entities = new List<GlobalSetting>
        {
            new() { Id = 1, Key = "setting1", Value = "value1", Description = "desc1" },
            new() { Id = 2, Key = "setting2", Value = "value2", Description = null }
        };
        _mockGlobalSettingRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        // Act
        var result = (await _service.GetAllSettingsAsync()).ToList();

        // Assert
        result.Should().HaveCount(2);
        result[0].Key.Should().Be("setting1");
        result[0].Value.Should().Be("value1");
        result[1].Key.Should().Be("setting2");
    }

    [Fact]
    public async Task GetAllSettingsAsync_OmitsProtectedWebAdminKey()
    {
        _mockGlobalSettingRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Visible", Value = "value" },
                new() { Id = 2, Key = "WebAdmin_VirtualKey", Value = "secret" }
            });

        var result = await _service.GetAllSettingsAsync();

        result.Should().ContainSingle(item => item.Key == "Visible");
        result.Should().NotContain(item => item.Key == "WebAdmin_VirtualKey");
    }

    [Fact]
    public async Task GetSettingByIdAsync_WithExistingId_ShouldReturnDto()
    {
        // Arrange
        var entity = new GlobalSetting { Id = 1, Key = "test-key", Value = "test-value" };
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        // Act
        var result = await _service.GetSettingByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Key.Should().Be("test-key");
        result.Value.Should().Be("test-value");
    }

    [Fact]
    public async Task GetSettingByIdAsync_WithNonExistentId_ShouldReturnNull()
    {
        // Arrange
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlobalSetting?)null);

        // Act
        var result = await _service.GetSettingByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSettingByKeyAsync_WithExistingKey_ShouldReturnDto()
    {
        // Arrange
        var entity = new GlobalSetting { Id = 1, Key = "my-key", Value = "my-value" };
        _mockGlobalSettingRepository.Setup(x => x.GetByKeyAsync("my-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        // Act
        var result = await _service.GetSettingByKeyAsync("my-key");

        // Assert
        result.Should().NotBeNull();
        result!.Value.Should().Be("my-value");
    }

    [Fact]
    public async Task CreateSettingAsync_WithUniqueKey_ShouldCreateAndReturnDto()
    {
        // Arrange
        var createDto = new CreateGlobalSettingDto { Key = "new-key", Value = "new-value", Description = "desc" };
        var createdEntity = new GlobalSetting { Id = 1, Key = "new-key", Value = "new-value", Description = "desc" };

        _mockGlobalSettingRepository.Setup(x => x.GetByKeyAsync("new-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlobalSetting?)null);
        _mockGlobalSettingRepository.Setup(x => x.CreateAsync(It.IsAny<GlobalSetting>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdEntity);

        // Act
        var result = await _service.CreateSettingAsync(createDto);

        // Assert
        result.Should().NotBeNull();
        result.Key.Should().Be("new-key");
        result.Value.Should().Be("new-value");
    }

    [Fact]
    public async Task CreateSettingAsync_WithDuplicateKey_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var existing = new GlobalSetting { Id = 1, Key = "existing-key", Value = "old-value" };
        _mockGlobalSettingRepository.Setup(x => x.GetByKeyAsync("existing-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var createDto = new CreateGlobalSettingDto { Key = "existing-key", Value = "new-value" };

        // Act
        var act = () => _service.CreateSettingAsync(createDto);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateSettingAsync_RejectsProtectedWebAdminKey()
    {
        var action = () => _service.CreateSettingAsync(new CreateGlobalSettingDto
        {
            Key = "WebAdmin_VirtualKey",
            Value = "secret"
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit by-key bootstrap API*");
        _mockGlobalSettingRepository.Verify(
            repository => repository.CreateAsync(
                It.IsAny<GlobalSetting>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSettingAsync_WithExistingId_ShouldUpdateAndReturnTrue()
    {
        // Arrange
        var existing = new GlobalSetting { Id = 1, Key = "key", Value = "old-value", Description = "old-desc" };
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _mockGlobalSettingRepository.Setup(x => x.UpdateAsync(It.IsAny<GlobalSetting>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var updateDto = new UpdateGlobalSettingDto { Id = 1, Value = "new-value", Description = "new-desc" };

        // Act
        var result = await _service.UpdateSettingAsync(updateDto);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSettingAsync_WithNonExistentId_ShouldReturnFalse()
    {
        // Arrange
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlobalSetting?)null);

        var updateDto = new UpdateGlobalSettingDto { Id = 999, Value = "new-value" };

        // Act
        var result = await _service.UpdateSettingAsync(updateDto);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateSettingAsync_RejectsProtectedWebAdminKey()
    {
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlobalSetting
            {
                Id = 7,
                Key = "WebAdmin_VirtualKey",
                Value = "secret"
            });

        var action = () => _service.UpdateSettingAsync(
            7,
            new UpdateGlobalSettingDto { Id = 7, Value = "replacement" });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit by-key bootstrap API*");
    }

    [Fact]
    public async Task UpdateSettingByKeyAsync_RejectsInvalidTypedValue()
    {
        var action = () => _service.UpdateSettingByKeyAsync(new UpdateGlobalSettingByKeyDto
        {
            Key = "Agentic.MaxIterations",
            Value = "101"
        });

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _mockGlobalSettingRepository.Verify(
            repository => repository.UpsertAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSettingByKeyAsync_ValidatesAgenticIterationRelationship()
    {
        _mockGlobalSettingRepository
            .Setup(repository => repository.GetByKeyAsync(
                "Agentic.MaxIterations",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlobalSetting
            {
                Key = "Agentic.MaxIterations",
                Value = "5"
            });

        var action = () => _service.UpdateSettingByKeyAsync(new UpdateGlobalSettingByKeyDto
        {
            Key = "Agentic.MinIterations",
            Value = "6"
        });

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be greater*");
    }

    [Fact]
    public async Task UpdateSettingAsync_WithNoChanges_ShouldReturnTrueWithoutCallingUpdate()
    {
        // Arrange
        var existing = new GlobalSetting { Id = 1, Key = "key", Value = "same-value", Description = "same-desc" };
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var updateDto = new UpdateGlobalSettingDto { Id = 1, Value = "same-value", Description = "same-desc" };

        // Act
        var result = await _service.UpdateSettingAsync(updateDto);

        // Assert
        result.Should().BeTrue();
        _mockGlobalSettingRepository.Verify(
            x => x.UpdateAsync(It.IsAny<GlobalSetting>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteSettingAsync_WithExistingId_ShouldDeleteAndReturnTrue()
    {
        // Arrange
        var entity = new GlobalSetting { Id = 1, Key = "to-delete", Value = "val" };
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _mockGlobalSettingRepository.Setup(x => x.DeleteAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.DeleteSettingAsync(1);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteSettingAsync_WithNonExistentId_ShouldReturnFalse()
    {
        // Arrange
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlobalSetting?)null);

        // Act
        var result = await _service.DeleteSettingAsync(999);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSettingAsync_RejectsProtectedWebAdminKey()
    {
        _mockGlobalSettingRepository.Setup(x => x.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlobalSetting
            {
                Id = 7,
                Key = "WebAdmin_VirtualKey",
                Value = "secret"
            });

        var action = () => _service.DeleteSettingAsync(7);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteSettingByKeyAsync_WithExistingKey_ShouldDeleteAndReturnTrue()
    {
        // Arrange
        var entity = new GlobalSetting { Id = 1, Key = "to-delete", Value = "val" };
        _mockGlobalSettingRepository.Setup(x => x.GetByKeyAsync("to-delete", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _mockGlobalSettingRepository.Setup(x => x.DeleteByKeyAsync("to-delete", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.DeleteSettingByKeyAsync("to-delete");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteSettingByKeyAsync_WithNonExistentKey_ShouldReturnFalse()
    {
        // Arrange
        _mockGlobalSettingRepository.Setup(x => x.GetByKeyAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlobalSetting?)null);

        // Act
        var result = await _service.DeleteSettingByKeyAsync("missing");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSettingByKeyAsync_RejectsProtectedWebAdminKey()
    {
        var action = () => _service.DeleteSettingByKeyAsync("WebAdmin_VirtualKey");

        await action.Should().ThrowAsync<InvalidOperationException>();
        _mockGlobalSettingRepository.Verify(
            repository => repository.DeleteByKeyAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
