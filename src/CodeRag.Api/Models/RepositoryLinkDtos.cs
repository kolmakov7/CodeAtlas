namespace CodeRag.Api.Models;

public sealed record CreateRepositoryLinkRequest(
    Guid SourceRepositoryId,
    Guid TargetRepositoryId,
    string LinkType,
    string? Note = null);

public sealed record RepositoryLinkDto(
    Guid Id,
    Guid SourceRepositoryId,
    string SourceRepositoryName,
    Guid TargetRepositoryId,
    string TargetRepositoryName,
    string LinkType,
    double? Confidence,
    string? Note,
    string CreatedAt);
