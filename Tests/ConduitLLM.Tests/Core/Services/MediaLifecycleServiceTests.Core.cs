using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class MediaLifecycleServiceTests
    {
        private readonly Mock<IMediaRuntimeStore> _mockMediaStore;
        private readonly Mock<ILogger<MediaLifecycleService>> _mockLogger;
        private readonly MediaLifecycleService _service;

        public MediaLifecycleServiceTests()
        {
            _mockMediaStore = new Mock<IMediaRuntimeStore>();
            _mockLogger = new Mock<ILogger<MediaLifecycleService>>();

            _service = new MediaLifecycleService(
                _mockMediaStore.Object,
                _mockLogger.Object);
        }
    }
}
