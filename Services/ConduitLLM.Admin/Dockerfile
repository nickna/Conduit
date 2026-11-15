# Admin API Dockerfile - Optimized for selective cache invalidation
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src

# ===== CACHED LAYERS (Keep these fast) =====
# Copy solution-wide restore inputs before project files so package changes
# invalidate the restore layer consistently.
COPY global.json Conduit.slnx Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config ./
COPY scripts/architecture/dependency-boundaries.targets ./scripts/architecture/

# Copy project files to correct locations
COPY Services/ConduitLLM.Gateway/*.csproj ./Services/ConduitLLM.Gateway/
COPY Shared/ConduitLLM.Configuration/*.csproj ./Shared/ConduitLLM.Configuration/
COPY Shared/ConduitLLM.Persistence.Abstractions/*.csproj ./Shared/ConduitLLM.Persistence.Abstractions/
COPY Shared/ConduitLLM.Persistence.Composition/*.csproj ./Shared/ConduitLLM.Persistence.Composition/
COPY Shared/ConduitLLM.Persistence.Npgsql/*.csproj ./Shared/ConduitLLM.Persistence.Npgsql/
COPY Shared/ConduitLLM.Core/*.csproj ./Shared/ConduitLLM.Core/
COPY Shared/ConduitLLM.Security/*.csproj ./Shared/ConduitLLM.Security/
COPY Shared/ConduitLLM.Providers/*.csproj ./Shared/ConduitLLM.Providers/
COPY Shared/ConduitLLM.Functions/*.csproj ./Shared/ConduitLLM.Functions/
COPY Shared/ConduitLLM.Contracts/*.csproj ./Shared/ConduitLLM.Contracts/
COPY Shared/ConduitLLM.Media/*.csproj ./Shared/ConduitLLM.Media/
COPY Shared/ConduitLLM.Messaging.Wolverine/*.csproj ./Shared/ConduitLLM.Messaging.Wolverine/
COPY Shared/ConduitLLM.OpenApi/*.csproj ./Shared/ConduitLLM.OpenApi/
COPY tools/ConduitLLM.Migrator/*.csproj ./tools/ConduitLLM.Migrator/
COPY Services/ConduitLLM.Admin/*.csproj ./Services/ConduitLLM.Admin/
COPY Tests/ConduitLLM.Tests/*.csproj ./Tests/ConduitLLM.Tests/
COPY Tests/ConduitLLM.IntegrationTests/*.csproj ./Tests/ConduitLLM.IntegrationTests/
COPY Tests/ConduitLLM.Benchmarks/*.csproj ./Tests/ConduitLLM.Benchmarks/

# Restore dependencies (cached unless project files change)
RUN dotnet restore "Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj"
RUN dotnet restore "tools/ConduitLLM.Migrator/ConduitLLM.Migrator.csproj"

# ===== CACHE INVALIDATION POINT =====
# This argument forces rebuild from here down when .NET code changes
ARG CACHEBUST=1
ARG VERSION=3.0.0
ARG COMMIT_SHA=dev
ARG BUILD_TIMESTAMP=unknown

# Copy all source code (this and everything after will rebuild)
COPY . .

# Build and publish
RUN dotnet publish "Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj" \
    -c Release \
    -o /app/publish \
    -p:Version="$VERSION" \
    -p:InformationalVersion="$VERSION" \
    -p:ConduitCommitSha="$COMMIT_SHA" \
    -p:ConduitBuildTimestamp="$BUILD_TIMESTAMP" \
    --no-restore
RUN dotnet publish "tools/ConduitLLM.Migrator/ConduitLLM.Migrator.csproj" \
    -c Release \
    -o /app/migrator \
    --no-restore

# Runtime stage - explicit Ubuntu 26.04 image family
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .
COPY --from=build /app/migrator ./migrator

# Run with a fixed non-root UID; application directories are owned by this UID.
RUN chown -R 1000:1000 /app
USER 1000

# Configure ASP.NET Core
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=45s --retries=3 \
    CMD curl -f http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "ConduitLLM.Admin.dll"]
