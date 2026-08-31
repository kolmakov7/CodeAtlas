namespace CodeRag.Api.Models;

public sealed record SearchApiRequest(
    string Query,
    Guid[]? RepositoryIds = null,
    string[]? Languages = null,
    string? SymbolKind = null,
    string? Mode = null,
    int? Limit = null,
    int? Offset = null);

public sealed record FindSymbolApiRequest(
    string Query,
    Guid? RepositoryId = null,
    string? Language = null,
    int? Limit = null,
    int? Offset = null);

public sealed record SymbolIdRequest(Guid SymbolId);

public sealed record CreateRepositoryRequest(
    string Name,
    string GitUrl,
    string Branch = "main",
    string? LocalPath = null,
    Guid? ProxyId = null,
    Guid? CredentialId = null,
    int? SyncIntervalSeconds = null);

public sealed record UpdateRepositoryRequest(
    string? Name = null,
    string? GitUrl = null,
    string? Branch = null,
    string? LocalPath = null,
    Guid? ProxyId = null,
    Guid? CredentialId = null,
    int? SyncIntervalSeconds = null,
    bool? Enabled = null);

public sealed record RepositoryDto(
    Guid Id,
    string Name,
    string GitUrl,
    string Branch,
    string LocalPath,
    string? CurrentCommit,
    string? IndexedCommit,
    string Status,
    bool Enabled,
    Guid? ProxyId,
    Guid? CredentialId,
    int? SyncIntervalSeconds,
    string? LastSyncAt,
    string? LastIndexAt,
    string? LastError);

public sealed record RepositoryStatisticsDto(
    Guid RepositoryId,
    string Name,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int VectorCount,
    string? IndexedCommit,
    string? CurrentCommit,
    string Status);

public sealed record GlobalStatisticsDto(
    int RepositoryCount,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int JobCount,
    IReadOnlyList<RepositoryStatisticsDto> Repositories);

public sealed record JobDto(
    Guid Id,
    Guid RepositoryId,
    string Type,
    string Status,
    string StartedAt,
    string? FinishedAt,
    double Progress,
    string? Error,
    string? FromCommit,
    string? ToCommit);

public sealed record SettingsDto(
    Dictionary<string, string> Values,
    string? UpdatedAt);

public sealed record HealthDetailsDto(
    string Status,
    HealthComponentDto Database,
    HealthComponentDto Vector,
    HealthComponentDto Embedding,
    HealthComponentDto Indexer,
    HealthComponentDto Storage,
    IReadOnlyList<string> Warnings,
    VectorFreshnessDto? VectorIndex = null);

public sealed record HealthComponentDto(string Status, string? Message, double? LatencyMs);

/// <summary>
/// Freshness of the stored vector index relative to the currently configured embedding model.
/// </summary>
public sealed record VectorFreshnessDto(
    bool Stale,
    string? Reason,
    IReadOnlyList<VectorModelInfoDto> Stored,
    string? CurrentModel,
    int? CurrentDim,
    int? CurrentContextLength)
{
    public static VectorFreshnessDto Fresh(
        IReadOnlyList<VectorModelInfoDto> stored,
        string? model, int? dim, int? contextLength) => new(false, null, stored, model, dim, contextLength);
}

/// <summary>
/// One group of stored vectors: which model/dim they were built with and how many chunks.
/// </summary>
public sealed record VectorModelInfoDto(string? Model, int Dim, int? ContextLength, int ChunkCount);

public sealed record SearchApiResponse(
    IReadOnlyList<SearchResultItemDto> Items,
    int Total,
    IReadOnlyList<string> Warnings,
    long ElapsedMs);

public sealed record SearchResultItemDto(
    Guid RepositoryId,
    string RepositoryName,
    Guid ChunkId,
    Guid FileId,
    string FilePath,
    Guid? SymbolId,
    string? SymbolName,
    string? Fqn,
    string Language,
    int StartLine,
    int EndLine,
    double Score,
    string Origin,
    string Preview);

public sealed record GetCodeResponseDto(
    Guid RepositoryId,
    Guid FileId,
    string Path,
    string Language,
    int StartLine,
    int EndLine,
    int TotalLines,
    string Content,
    string Preview);
