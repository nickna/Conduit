using ConduitLLM.Admin.Services;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Tests.TestInfrastructure;

using ConduitLLM.Configuration.Messaging;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Admin.Services
{
    public partial class AdminVirtualKeyServiceTests : IDisposable
    {
        private readonly Mock<IVirtualKeyRepository> _mockVirtualKeyRepository;
        private readonly Mock<IVirtualKeySpendHistoryRepository> _mockSpendHistoryRepository;
        private readonly Mock<IVirtualKeyGroupRepository> _mockGroupRepository;
        private readonly Mock<IVirtualKeyCache> _mockCache;
        private readonly Mock<IEventBus> _mockPublishEndpoint;
        private readonly Mock<ILogger<AdminVirtualKeyService>> _mockLogger;
        private readonly Mock<IMediaLifecycleService> _mockMediaLifecycleService;
        private readonly Mock<IMediaDeletionEngine> _mockMediaDeletionEngine;
        private readonly Mock<IDistributedLockProvider> _mockMediaCleanupLockService;
        private readonly Mock<IDistributedLockOwnership> _mockMediaCleanupLock;
        private readonly Mock<IModelProviderMappingRepository> _mockModelProviderMappingRepository;
        private readonly Mock<IModelCapabilityService> _mockModelCapabilityService;
        private readonly SqliteTestDatabase _database;
        private readonly DbContextOptions<ConduitDbContext> _dbContextOptions;
        private readonly TestDbContextFactory _dbContextFactory;
        private readonly AdminVirtualKeyService _service;
        private bool _disposed;

        public AdminVirtualKeyServiceTests()
        {
            _mockVirtualKeyRepository = new Mock<IVirtualKeyRepository>();
            _mockSpendHistoryRepository = new Mock<IVirtualKeySpendHistoryRepository>();
            _mockGroupRepository = new Mock<IVirtualKeyGroupRepository>();
            _mockCache = new Mock<IVirtualKeyCache>();
            _mockPublishEndpoint = new Mock<IEventBus>();
            _mockLogger = new Mock<ILogger<AdminVirtualKeyService>>();
            _mockMediaLifecycleService = new Mock<IMediaLifecycleService>();
            _mockMediaDeletionEngine = new Mock<IMediaDeletionEngine>();
            _mockMediaCleanupLockService = new Mock<IDistributedLockProvider>();
            _mockMediaCleanupLock = new Mock<IDistributedLockOwnership>();
            _mockModelProviderMappingRepository = new Mock<IModelProviderMappingRepository>();
            _mockModelCapabilityService = new Mock<IModelCapabilityService>();
            _mockMediaCleanupLockService
                .Setup(service => service.TryAcquireAsync(
                    MediaCleanupLock.Key,
                    TimeSpan.Zero,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(_mockMediaCleanupLock.Object);
            _mockMediaDeletionEngine
                .Setup(engine => engine.ExecuteOperationAsync(
                    It.IsAny<MediaDeletionOperationContext>(),
                    It.IsAny<Func<Task<MediaDeletionEngineResult>>>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    MediaDeletionOperationContext _,
                    Func<Task<MediaDeletionEngineResult>> action,
                    CancellationToken _) => action());

            // SQLite-backed factory so tests that hit ExecuteUpdateAsync (e.g. PerformMaintenanceAsync)
            // run against a real relational provider. EF's InMemory provider does not support it.
            _database = new SqliteTestDatabase();
            _dbContextOptions = _database.Options;
            _dbContextFactory = new TestDbContextFactory(_database.CreateContext);

            _service = new AdminVirtualKeyService(
                _mockVirtualKeyRepository.Object,
                _mockSpendHistoryRepository.Object,
                _mockGroupRepository.Object,
                _mockLogger.Object,
                _mockModelProviderMappingRepository.Object,
                _mockModelCapabilityService.Object,
                _dbContextFactory,
                _mockCache.Object,
                _mockPublishEndpoint.Object,
                _mockMediaLifecycleService.Object,
                _mockMediaDeletionEngine.Object,
                _mockMediaCleanupLockService.Object);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _database.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
