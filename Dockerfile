# syntax=docker/dockerfile:1
# CodeRAG Dockerfile — multi-stage sdk:10.0 -> aspnet:10.0 (§37 deployment.md §4)

# Web UI build stage (React+Vite) — optional but included for single image
FROM node:20-alpine AS web-build
WORKDIR /web
COPY src/CodeRag.Web/package.json src/CodeRag.Web/package-lock.json* ./
RUN npm install --silent || npm install
COPY src/CodeRag.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and sln first for restore caching
COPY CodeAtlas.sln ./
COPY src/CodeRag.Domain/CodeRag.Domain.csproj src/CodeRag.Domain/
COPY src/CodeRag.Analyzers.Abstractions/CodeRag.Analyzers.Abstractions.csproj src/CodeRag.Analyzers.Abstractions/
COPY src/CodeRag.Analyzers.CSharp/CodeRag.Analyzers.CSharp.csproj src/CodeRag.Analyzers.CSharp/
COPY src/CodeRag.Analyzers.TreeSitter/CodeRag.Analyzers.TreeSitter.csproj src/CodeRag.Analyzers.TreeSitter/
COPY src/CodeRag.Analyzers.Sql/CodeRag.Analyzers.Sql.csproj src/CodeRag.Analyzers.Sql/
COPY src/CodeRag.Analyzers.Vbs/CodeRag.Analyzers.Vbs.csproj src/CodeRag.Analyzers.Vbs/
COPY src/CodeRag.Infrastructure/CodeRag.Infrastructure.csproj src/CodeRag.Infrastructure/
COPY src/CodeRag.Embeddings/CodeRag.Embeddings.csproj src/CodeRag.Embeddings/
COPY src/CodeRag.Git/CodeRag.Git.csproj src/CodeRag.Git/
COPY src/CodeRag.Indexer/CodeRag.Indexer.csproj src/CodeRag.Indexer/
COPY src/CodeRag.Search/CodeRag.Search.csproj src/CodeRag.Search/
COPY src/CodeRag.Mcp/CodeRag.Mcp.csproj src/CodeRag.Mcp/
COPY src/CodeRag.Application/CodeRag.Application.csproj src/CodeRag.Application/
COPY src/CodeRag.Api/CodeRag.Api.csproj src/CodeRag.Api/
COPY tests/CodeRag.Tests/CodeRag.Tests.csproj tests/CodeRag.Tests/

RUN dotnet restore CodeAtlas.sln

# Copy remaining source
COPY . .

# Publish Api (Release). SkipWebBuild: Web UI is already built in the web-build stage
# (avoids a second npm ci + npm run build inside this stage, see CodeRag.Api.csproj)
RUN dotnet publish src/CodeRag.Api/CodeRag.Api.csproj -c Release -o /app/publish -p:SkipWebBuild=true
RUN find /root/.nuget -name "vec0.so" -exec cp {} /app/publish/vec0.so \; 2>/dev/null; ls -lh /app/publish/vec0.so 2>&1 || echo "vec0 not found"
# Publish Mcp (Release) — stdio sidecar, shares Storage/Search
RUN dotnet publish src/CodeRag.Mcp/CodeRag.Mcp.csproj -c Release -o /app/mcp -p:SkipWebBuild=true
RUN find /root/.nuget -name "vec0.so" -exec cp {} /app/mcp/vec0.so \; 2>/dev/null; ls -lh /app/mcp/vec0.so 2>&1 || echo "vec0 mcp not found"

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for healthcheck, git + openssh-client for Git SSH (§37)
RUN apt-get update --allow-unauthenticated || apt-get update --fix-missing || true; \
    apt-get install -y --no-install-recommends curl git openssh-client || apt-get install -y --allow-unauthenticated curl git openssh-client; \
    rm -rf /var/lib/apt/lists/*

# Non-root user 1000:1000 (§37, deployment.md §2) — aspnet image already has 'ubuntu' GID/UID 1000, reuse it
RUN mkdir -p /data && chown -R 1000:1000 /app /data

# Copy Web UI dist to wwwroot for MapStaticAssets/UseStaticFiles
COPY --from=web-build /web/dist ./wwwroot

# Copy published app
COPY --from=build /app/publish ./

# Ensure DataProtection keys + SSH keys dir will be under /data
VOLUME ["/data"]

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

STOPSIGNAL SIGTERM

HEALTHCHECK --interval=15s --timeout=5s --retries=3 --start-period=20s \
  CMD curl -f http://localhost:8080/health/ready || exit 1

USER 1000:1000

ENTRYPOINT ["dotnet", "CodeRag.Api.dll"]

# MCP sidecar — supports stdio (local) + Streamable HTTP (remote, default both)
FROM runtime AS mcp
COPY --from=build /app/mcp ./mcp/
WORKDIR /app/mcp
EXPOSE 3001
ENV ASPNETCORE_URLS=http://+:3001
ENV MCP_TRANSPORT=both
HEALTHCHECK --interval=15s --timeout=5s --retries=3 --start-period=10s \
  CMD curl -f http://localhost:3001/health || exit 1
USER 1000:1000
ENTRYPOINT ["dotnet", "CodeRag.Mcp.dll"]
