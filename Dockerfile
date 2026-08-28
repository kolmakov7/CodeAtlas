# syntax=docker/dockerfile:1
# CodeRAG Dockerfile — multi-stage sdk:10.0 -> aspnet:10.0 (§37 deployment.md §4)
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
COPY src/CodeRag.Api/CodeRag.Api.csproj src/CodeRag.Api/
COPY tests/CodeRag.Tests/CodeRag.Tests.csproj tests/CodeRag.Tests/

RUN dotnet restore CodeAtlas.sln

# Copy remaining source
COPY . .

# Publish Api (Release, no restore)
RUN dotnet publish src/CodeRag.Api/CodeRag.Api.csproj -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for healthcheck, git + openssh-client for Git SSH (§37)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl git openssh-client && \
    rm -rf /var/lib/apt/lists/*

# Non-root user 1000:1000 (§37, deployment.md §2)
RUN groupadd -g 1000 coderag && \
    useradd -u 1000 -g 1000 -m -s /bin/bash coderag && \
    mkdir -p /data && \
    chown -R 1000:1000 /app /data

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
