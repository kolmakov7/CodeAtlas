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
    Guid? GroupId = null,
    int? SyncIntervalSeconds = null,
    Guid? ProxyId = null,
    Guid? CredentialId = null,
    bool DirectoryFilterEnabled = false,
    string[]? AllowedDirectories = null);

public sealed record UpdateRepositoryRequest(
    string? Name = null,
    string? GitUrl = null,
    string? Branch = null,
    string? LocalPath = null,
    Guid? GroupId = null,
    int? SyncIntervalSeconds = null,
    bool? Enabled = null,
    Guid? ProxyId = null,
    Guid? CredentialId = null,
    bool? DirectoryFilterEnabled = null,
    string[]? AllowedDirectories = null);

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
    Guid? GroupId,
    Guid? ProxyId,
    Guid? CredentialId,
    int? SyncIntervalSeconds,
    string? LastSyncAt,
    string? LastIndexAt,
    string? LastError,
    bool DirectoryFilterEnabled = false,
    string[]? AllowedDirectories = null);

public sealed record RepositoryGroupDto(
    Guid Id,
    string Name,
    Guid? ProxyId,
    Guid? CredentialId,
    string CreatedAt);

public sealed record CreateRepositoryGroupRequest(
    string Name,
    Guid? ProxyId = null,
    Guid? CredentialId = null);

public sealed record UpdateRepositoryGroupRequest(
    string? Name = null,
    Guid? ProxyId = null,
    Guid? CredentialId = null);

public sealed record EmbeddingDomainStatsDto(string Domain, int Indexed, int Pending);

public sealed record RelationTypeStatsDto(string RelationType, int Count);

public sealed record RepositoryStatisticsDto(
    Guid RepositoryId,
    string Name,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int VectorCount,
    int RelationCount,
    IReadOnlyList<RelationTypeStatsDto> RelationsByType,
    IReadOnlyList<EmbeddingDomainStatsDto> EmbeddingStats,
    int EmbeddingsFailed,
    string? IndexedCommit,
    string? CurrentCommit,
    string Status,
    IReadOnlyDictionary<string, int>? LanguageBreakdown = null);

public sealed record StorageDto(
    long SqliteSizeBytes,
    long VectorSizeBytes,
    long RepositoriesSizeBytes);

public sealed record GlobalStatisticsDto(
    int RepositoryCount,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int RelationCount,
    IReadOnlyList<RelationTypeStatsDto> RelationsByType,
    int JobCount,
    IReadOnlyList<EmbeddingDomainStatsDto> EmbeddingStats,
    int EmbeddingsFailed,
    IReadOnlyList<RepositoryStatisticsDto> Repositories,
    StorageDto? Storage = null,
    IReadOnlyDictionary<string, int>? LanguageBreakdown = null);

public sealed record McpToolUsageDto(string Tool, long Total, long Errors);

public sealed record McpDailyUsageDto(string Day, long Total);

public sealed record McpUsageStatsDto(
    long TotalCalls,
    long TotalErrors,
    int Days,
    IReadOnlyList<McpToolUsageDto> ByTool,
    IReadOnlyList<McpDailyUsageDto> ByDay);

public sealed record JobDto(
    Guid Id,
    Guid RepositoryId,
    string RepositoryName,
    string Type,
    string Status,
    string StartedAt,
    string? FinishedAt,
    double Progress,
    string? Error,
    string? FromCommit,
    string? ToCommit,
    int? TotalItems = null,
    int? ItemsDone = null,
    string? Phase = null,
    string? CurrentFile = null);

public sealed record SettingsDto(
    Dictionary<string, string> Values,
    string? UpdatedAt);

public sealed record HealthDetailsDto(
    string Status,
    HealthComponentDto Database,
    HealthComponentDto Vector,
    IReadOnlyList<VectorDomainHealthDto> VectorDomains,
    HealthComponentDto Embedding,
    IReadOnlyList<EmbeddingDomainHealthDto> EmbeddingDomains,
    HealthComponentDto Indexer,
    HealthComponentDto Storage,
    IReadOnlyList<string> Warnings,
    VectorFreshnessDto? VectorIndex = null);

public sealed record HealthComponentDto(string Status, string? Message, double? LatencyMs);

/// <summary>Health of one vector space (code / text) — store reachability + index freshness.</summary>
public sealed record VectorDomainHealthDto(
    string Domain,
    string Status,
    string? Message,
    double? LatencyMs,
    VectorFreshnessDto? Freshness);

/// <summary>Health of one embedding endpoint (code / text) — probe + configured model.</summary>
public sealed record EmbeddingDomainHealthDto(
    string Domain,
    string Status,
    string? Message,
    double? LatencyMs,
    string? Model,
    int Dimensions);

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

public sealed record ValidateRepositoryRequest(
    string GitUrl,
    Guid? GroupId = null,
    Guid? ProxyId = null,
    Guid? CredentialId = null);

public sealed record ValidateRepositoryResponse(
    bool Available,
    string? Error = null);

public sealed record BranchesResponse(
    IReadOnlyList<string> Branches);

public sealed record RootDirectoriesResponse(
    IReadOnlyList<string> Directories);
