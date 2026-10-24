namespace CodeRag.Api.Models;

/// <summary>Request to generate documentation for a single target (symbol or module).</summary>
public sealed record GenerateDocumentationRequest(
    Guid RepositoryId,
    string? TargetType = "symbol",
    string? TargetRef = null,
    string? Name = null,
    Guid? SymbolId = null,
    string? Namespace = null,
    string? FilePath = null,
    string? Language = null,
    bool Force = false);

/// <summary>Request to enqueue whole-repository documentation generation.</summary>
public sealed record GenerateRepositoryDocumentationRequest(
    bool Force = false,
    int? MaxSymbols = null,
    string? Strategy = null,
    int MinUsage = 0);

/// <summary>List-item view of a documentation entry (no content).</summary>
public sealed record DocumentationDto(
    Guid Id,
    Guid RepositoryId,
    string TargetType,
    Guid? TargetId,
    string TargetRef,
    string Name,
    string? Namespace,
    string? FilePath,
    string? Language,
    string Status,
    string? Error,
    string? Model,
    string PromptVersion,
    string? SourceHash,
    string? GenerationCommit,
    int? ContextChars,
    int? OutputChars,
    string GeneratedAt,
    string UpdatedAt);

/// <summary>Detail view of a documentation entry including the generated Markdown content.</summary>
public sealed record DocumentationDetailDto(
    Guid Id,
    Guid RepositoryId,
    string TargetType,
    Guid? TargetId,
    string TargetRef,
    string Name,
    string? Namespace,
    string? FilePath,
    string? Language,
    string Status,
    string? Error,
    string? Model,
    string PromptVersion,
    string? SourceHash,
    string? GenerationCommit,
    int? ContextChars,
    int? OutputChars,
    string GeneratedAt,
    string UpdatedAt,
    string Content);

/// <summary>Result of a single-target generation request.</summary>
public sealed record GenerateDocumentationResponse(
    DocumentationDetailDto Documentation,
    bool Generated);
