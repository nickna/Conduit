# Gateway API Dockerfile - Optimized for selective cache invalidation
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# ===== CACHED LAYERS (Keep these fast) =====
# Copy solution-wide restore inputs before project files so package changes
# invalidate the restore layer consistently.
COPY Conduit.slnx Directory.Build.props Directory.Packages.props NuGet.Config ./

# Copy project files to correct locations
COPY Services/ConduitLLM.Gateway/*.csproj ./Services/ConduitLLM.Gateway/
COPY Shared/ConduitLLM.Configuration/*.csproj ./Shared/ConduitLLM.Configuration/
COPY Shared/ConduitLLM.Core/*.csproj ./Shared/ConduitLLM.Core/
COPY Shared/ConduitLLM.Security/*.csproj ./Shared/ConduitLLM.Security/
COPY Shared/ConduitLLM.Providers/*.csproj ./Shared/ConduitLLM.Providers/
COPY Shared/ConduitLLM.Functions/*.csproj ./Shared/ConduitLLM.Functions/
COPY Shared/ConduitLLM.Contracts/*.csproj ./Shared/ConduitLLM.Contracts/
COPY Shared/ConduitLLM.Media/*.csproj ./Shared/ConduitLLM.Media/
COPY Shared/ConduitLLM.Messaging.Wolverine/*.csproj ./Shared/ConduitLLM.Messaging.Wolverine/
COPY Shared/ConduitLLM.OpenApi/*.csproj ./Shared/ConduitLLM.OpenApi/
COPY Shared/ConduitLLM.SignalR/*.csproj ./Shared/ConduitLLM.SignalR/
COPY Shared/ConduitLLM.Tokenization/*.csproj ./Shared/ConduitLLM.Tokenization/
COPY Services/ConduitLLM.Admin/*.csproj ./Services/ConduitLLM.Admin/
COPY Tests/ConduitLLM.Tests/*.csproj ./Tests/ConduitLLM.Tests/
COPY Tests/ConduitLLM.IntegrationTests/*.csproj ./Tests/ConduitLLM.IntegrationTests/
COPY Tests/ConduitLLM.Benchmarks/*.csproj ./Tests/ConduitLLM.Benchmarks/

# Restore dependencies (cached unless project files change)
RUN dotnet restore "Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj"

# ===== CACHE INVALIDATION POINT =====
# This argument forces rebuild from here down when .NET code changes
ARG CACHEBUST=1
ARG VERSION=3.0.0
ARG COMMIT_SHA=dev
ARG BUILD_TIMESTAMP=unknown

# Copy all source code (this and everything after will rebuild)
COPY . .

# Build and publish
RUN dotnet publish "Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj" \
    -c Release \
    -o /app/publish \
    -p:Version="$VERSION" \
    -p:InformationalVersion="$VERSION" \
    -p:ConduitCommitSha="$COMMIT_SHA" \
    -p:ConduitBuildTimestamp="$BUILD_TIMESTAMP" \
    --no-restore

# Runtime stage - Debian-based for reliability
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .

# Use the existing non-root user (ubuntu with UID 1000 in .NET 10.0 images)
RUN chown -R 1000:1000 /app
USER 1000

# Configure ASP.NET Core
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "ConduitLLM.Gateway.dll"]
