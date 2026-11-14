# Conduit Test Suite

This directory contains all test and benchmarking projects for the Conduit solution. The test suite is organized into three main categories to provide comprehensive testing coverage and performance analysis across all aspects of the system.

## Test Projects

### ConduitLLM.Tests
**Location**: `Tests/ConduitLLM.Tests/`

The primary automated test suite providing comprehensive unit and integration tests for all core ConduitLLM components.

**Purpose**:
- Unit tests for core logic, services, and utilities
- Component-level integration tests
- Provider compatibility validation
- API endpoint testing
- Configuration, caching, streaming, security, and middleware validation
- CI/CD pipeline integration

**Test Count**: 1,900+ tests covering all major components

**Key Coverage Areas**:
- Core abstractions and shared logic
- Provider integrations (OpenAI, Anthropic, Groq, Cerebras, etc.)
- HTTP API endpoints
- Configuration management
- Security and authentication
- Middleware and request processing
- Caching strategies
- Streaming functionality

See [ConduitLLM.Tests/README.md](./ConduitLLM.Tests/README.md) for detailed information.

### ConduitLLM.IntegrationTests
**Location**: `Tests/ConduitLLM.IntegrationTests/`

End-to-end integration tests that verify the complete Conduit workflow from provider setup through virtual key billing.

**Purpose**:
- Full-stack integration testing
- Real provider API interaction validation
- Complete request lifecycle verification
- Token tracking and billing accuracy
- Multi-service orchestration testing

**Test Flow**:
1. Provider creation and configuration
2. API key management
3. Model mapping setup
4. Cost configuration
5. Virtual key provisioning
6. Chat request execution
7. Token tracking validation
8. Billing accuracy verification
9. Detailed reporting

See [ConduitLLM.IntegrationTests/README.md](./ConduitLLM.IntegrationTests/README.md) for detailed information.

### ConduitLLM.Benchmarks
**Location**: `Tests/ConduitLLM.Benchmarks/`

Performance benchmarking suite using BenchmarkDotNet for measuring and analyzing code performance.

**Purpose**:
- Performance benchmarking of critical code paths
- Identifying performance bottlenecks
- Regression testing for performance
- Comparative analysis of different implementations
- Optimization validation

**Key Features**:
- Uses BenchmarkDotNet for accurate performance measurements
- Benchmarks for string operations and other core utilities
- Statistical analysis of performance metrics
- Memory allocation profiling

**Running Benchmarks**:
```bash
# Run all benchmarks
dotnet run --project Tests/ConduitLLM.Benchmarks/ConduitLLM.Benchmarks.csproj -c Release

# Run specific benchmark
dotnet run --project Tests/ConduitLLM.Benchmarks/ConduitLLM.Benchmarks.csproj -c Release --filter "*StringOperations*"
```

**Note**: Always run benchmarks in Release configuration for accurate results.

## Running Tests

### Run All Tests
```bash
# From solution root
dotnet test

# From Tests directory
cd Tests
dotnet test
```

### Run Specific Test Project
```bash
# Unit tests only
dotnet test Tests/ConduitLLM.Tests/ConduitLLM.Tests.csproj

# Integration tests only
dotnet test Tests/ConduitLLM.IntegrationTests/ConduitLLM.IntegrationTests.csproj
```

### Run with Detailed Output
```bash
dotnet test --logger "console;verbosity=detailed"
```

### Run Specific Test
```bash
dotnet test --filter "FullyQualifiedName=Namespace.ClassName.TestMethodName"
```

## Project Organization

```
Tests/
├── README.md                          # This file
├── ConduitLLM.Benchmarks/             # Performance benchmarks
│   ├── Program.cs                     # BenchmarkDotNet runner
│   └── StringOperationsBenchmarks.cs  # String operation benchmarks
├── ConduitLLM.Tests/                  # Unit & component tests
│   ├── Admin/                         # Admin API tests
│   ├── Architecture/                  # Architecture tests
│   ├── Builders/                      # Builder pattern tests
│   ├── Configuration/                 # Configuration tests
│   ├── Core/                          # Core logic tests
│   ├── HealthChecks/                  # Health check tests
│   ├── Http/                          # HTTP API tests
│   ├── Integration/                   # Component integration tests
│   ├── Middleware/                    # Middleware tests
│   ├── Providers/                     # Provider tests
│   ├── Security/                      # Security tests
│   ├── Services/                      # Service layer tests
│   ├── TestHelpers/                   # Test utilities
│   ├── TestInfrastructure/            # Test infrastructure
│   └── Utilities/                     # Utility tests
└── ConduitLLM.IntegrationTests/       # E2E integration tests
    ├── Config/                        # Test configurations
    ├── Core/                          # Core integration tests
    └── Tests/                         # Test implementations
```

## Prerequisites

### For ConduitLLM.Tests
- The .NET 11 SDK selected by `global.json`
- No external services required (uses in-memory/mock implementations)

### For ConduitLLM.Benchmarks
- The .NET 11 SDK selected by `global.json`
- No external services required
- Release configuration required for accurate measurements

### For ConduitLLM.IntegrationTests
- The .NET 11 SDK selected by `global.json`
- Docker environment running (`./scripts/dev.ps1`)
- All services healthy:
  - Gateway API (http://localhost:5000)
  - Admin API (http://localhost:5002)
  - PostgreSQL
  - Redis
  - RabbitMQ (if configured)
- Valid provider API keys (e.g., Groq, OpenAI)
- Configuration files set up (see IntegrationTests README)

## Test Configuration

### Unit Tests
Most unit tests use default settings with in-memory or mock services. No special configuration required.

### Integration Tests
Require configuration files:
```bash
cd Tests/ConduitLLM.IntegrationTests

# Copy configuration templates
cp Config/test-config.template.yaml Config/test-config.yaml
cp Config/providers/groq.template.yaml Config/providers/groq.yaml

# Edit with your settings
# - adminApiKey from docker-compose.dev.yml
# - Provider API keys
```

## Best Practices

### General Testing Guidelines
- Keep tests isolated and repeatable
- Clean up resources created during tests
- Use meaningful test names following the pattern: `MethodName_Condition_ExpectedResult`
- One assertion per test when possible
- Use appropriate test categories (unit vs integration)

### Unit Testing
- Prefer in-memory or mock services
- Mock external dependencies
- Test one component at a time
- Fast execution (entire suite should run in seconds)

### Performance Benchmarking
- Always run in Release configuration
- Minimize background processes during benchmarking
- Run multiple iterations for statistical accuracy
- Document baseline performance metrics
- Compare results across different implementations
- Monitor for performance regressions

### Integration Testing
- Use real services when necessary
- Prefix test data with `TEST_` for easy identification
- Generate detailed reports for debugging
- Tests may leave data for manual verification
- Consider API costs when using real providers

## CI/CD Integration

The test suite is designed for automated pipeline integration:

```bash
# Standard CI pipeline
dotnet restore
dotnet build
dotnet test --no-build --logger "trx;LogFileName=test-results.trx"
```

### Test Categories
Tests can be filtered by category for selective execution:
- Unit tests: Fast, no external dependencies
- Integration tests: Require running services
- Provider tests: May require API keys and credits

## Troubleshooting

### Build Errors
```bash
# Restore packages
dotnet restore

# Clean and rebuild
dotnet clean
dotnet build
```

### Test Discovery Issues
```bash
# List all tests
dotnet test --list-tests
```

### Permission Issues
Ensure the test projects have proper file permissions after git operations:
```bash
chmod +x Tests/ConduitLLM.Tests/*.sh
chmod +x Tests/ConduitLLM.IntegrationTests/*.sh
```

### Database Issues (Integration Tests)
```bash
# Restart development environment
./scripts/dev.ps1 -Clean
```

### Clean Test Data (Integration Tests)
See [ConduitLLM.IntegrationTests/README.md](./ConduitLLM.IntegrationTests/README.md#test-data-persistence) for SQL cleanup scripts.

## Code Coverage

Generate code coverage reports:
```bash
# Run tests with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover

# View coverage in Tests/ConduitLLM.Tests/coverage.opencover.xml
```

## Contributing

When adding new tests:
1. Place in the appropriate test project
2. Follow existing naming conventions
3. Add to appropriate subdirectory by component
4. Include test documentation if the test setup is complex
5. Ensure tests are isolated and repeatable
6. Update relevant README if adding new test categories

## Additional Resources

- **Core Logic**: See `ConduitLLM.Core/README.md`
- **Provider Info**: See `ConduitLLM.Providers/README.md`
- **API Usage**: See `ConduitLLM.Gateway/README.md`
- **Configuration**: See `ConduitLLM.Configuration/README.md`
- **Main Documentation**: See root `README.md` and `docs/` directory

## Support

For questions, issues, or contributions:
- Open an issue on the [GitHub repository](https://github.com/nickna/Conduit/issues)
- Review existing documentation in the `docs/` directory
- Check project-specific README files for detailed information
