using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>
/// A generated documentation entry (table <c>documentation</c>).
/// Invariants:
/// - (RepositoryId, TargetType, TargetRef) is the stable identity; TargetId (symbol id)
///   is convenience metadata that may change across re-indexes.
/// - Content is non-empty Markdown.
/// - SourceHash captures the target + direct dependencies at generation time and drives staleness.
/// </summary>
public sealed class Documentation
{
    public Guid Id { get; private set; }
    public Guid RepositoryId { get; private set; }
    public DocumentationTargetType TargetType { get; private set; }

    /// <summary>Symbol id when <see cref="TargetType"/> is Symbol; null otherwise.</summary>
    public Guid? TargetId { get; private set; }

    /// <summary>
    /// Stable key within the target type: FQN (symbol), normalized directory path (module),
    /// or the constant "repository" (repository scope).
    /// </summary>
    public string TargetRef { get; private set; }

    public string Name { get; private set; }
    public string? Namespace { get; private set; }
    public string? FilePath { get; private set; }
    public string? Language { get; private set; }

    /// <summary>Generated Markdown.</summary>
    public string Content { get; private set; } = string.Empty;

    public string? Model { get; private set; }
    public string PromptVersion { get; private set; } = "v1";

    /// <summary>Content hash of target + direct dependencies at generation time (null when unknown).</summary>
    public string? SourceHash { get; private set; }

    public DocumentationStatus Status { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Indexed commit of the repository at generation time (best-effort).</summary>
    public string? GenerationCommit { get; private set; }

    /// <summary>Approximate character size of the LLM context (for logging/observability).</summary>
    public int? ContextChars { get; private set; }

    /// <summary>Character size of the generated content.</summary>
    public int? OutputChars { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Documentation(Guid id, Guid repositoryId, DocumentationTargetType targetType, string targetRef, string name)
    {
        Id = id;
        RepositoryId = repositoryId;
        TargetType = targetType;
        TargetRef = targetRef;
        Name = name;
        Status = DocumentationStatus.Generated;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static Documentation Create(
        Guid id, Guid repositoryId, DocumentationTargetType targetType,
        string targetRef, string name,
        Guid? targetId = null, string? @namespace = null, string? filePath = null, string? language = null,
        string? content = null, string? model = null, string promptVersion = "v1",
        string? sourceHash = null, string? generationCommit = null,
        int? contextChars = null, int? outputChars = null,
        DocumentationStatus status = DocumentationStatus.Generated, string? error = null,
        DateTimeOffset? generatedAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Documentation Id must not be empty.");
        if (repositoryId == Guid.Empty) throw new DomainValidationException("RepositoryId must not be empty.");
        if (string.IsNullOrWhiteSpace(targetRef)) throw new DomainValidationException("TargetRef must not be empty.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Name must not be empty.");

        return new Documentation(id, repositoryId, targetType, targetRef.Trim(), name.Trim())
        {
            TargetId = targetId,
            Namespace = string.IsNullOrWhiteSpace(@namespace) ? null : @namespace.Trim(),
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
            Content = content ?? string.Empty,
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            PromptVersion = string.IsNullOrWhiteSpace(promptVersion) ? "v1" : promptVersion.Trim(),
            SourceHash = sourceHash,
            GenerationCommit = generationCommit,
            ContextChars = contextChars,
            OutputChars = outputChars ?? (content is null ? null : content.Length),
            Status = status,
            Error = error,
            GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow
        };
    }

    /// <summary>Rebuild an entity from a persisted row.</summary>
    public static Documentation Rehydrate(
        Guid id, Guid repositoryId, DocumentationTargetType targetType,
        string targetRef, string name,
        Guid? targetId, string? @namespace, string? filePath, string? language,
        string content, string? model, string promptVersion, string? sourceHash,
        DocumentationStatus status, string? error, string? generationCommit,
        int? contextChars, int? outputChars, DateTimeOffset generatedAt, DateTimeOffset updatedAt)
    {
        return new Documentation(id, repositoryId, targetType, targetRef, name)
        {
            TargetId = targetId,
            Namespace = @namespace,
            FilePath = filePath,
            Language = language,
            Content = content,
            Model = model,
            PromptVersion = promptVersion,
            SourceHash = sourceHash,
            Status = status,
            Error = error,
            GenerationCommit = generationCommit,
            ContextChars = contextChars,
            OutputChars = outputChars,
            GeneratedAt = generatedAt,
            UpdatedAt = updatedAt
        };
    }

    /// <summary>Replace content and metadata after a successful generation.</summary>
    public void UpdateContent(
        string content, string? model, string promptVersion, string? sourceHash,
        Guid? targetId = null, string? generationCommit = null,
        int? contextChars = null, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new DomainValidationException("Content must not be empty.");
        Content = content;
        Model = model;
        PromptVersion = string.IsNullOrWhiteSpace(promptVersion) ? "v1" : promptVersion;
        SourceHash = sourceHash;
        if (targetId is not null) TargetId = targetId;
        GenerationCommit = generationCommit;
        ContextChars = contextChars;
        OutputChars = content.Length;
        if (filePath is not null) FilePath = filePath;
        Status = DocumentationStatus.Generated;
        Error = null;
        GeneratedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Flag as in-progress for a background generation.</summary>
    public void MarkGenerating()
    {
        Status = DocumentationStatus.Generating;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Mark stale because the source (or a dependency) changed since generation.</summary>
    public void MarkStale(string? reason = null)
    {
        Status = DocumentationStatus.Stale;
        Error = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Mark a failed generation attempt, keeping the last good content if present.</summary>
    public void MarkFailed(string error)
    {
        Status = DocumentationStatus.Failed;
        Error = string.IsNullOrWhiteSpace(error) ? "generation failed" : error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>True when the stored content hash differs from the current source hash.</summary>
    public bool IsStaleAgainst(string? currentSourceHash)
    {
        if (string.IsNullOrEmpty(SourceHash) || string.IsNullOrEmpty(currentSourceHash))
            return false; // no hash to compare against → not automatically stale
        return !string.Equals(SourceHash, currentSourceHash, StringComparison.Ordinal);
    }
}
