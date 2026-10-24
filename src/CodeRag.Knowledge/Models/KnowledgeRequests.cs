using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;

namespace CodeRag.Knowledge.Models;

public sealed record CreateTaskRequest(
    string Title,
    string? Description = null,
    TaskKind Kind = TaskKind.Work,
    TaskState Status = TaskState.Open,
    TaskPriority Priority = TaskPriority.Normal,
    Guid? ProjectId = null,
    Guid? RepositoryId = null,
    Guid? ParentTaskId = null,
    string Source = "agent",
    string? ExternalKey = null,
    string? CreatedBy = null,
    string? Metadata = null);

public sealed record UpdateTaskRequest(
    int ExpectedVersion,
    string? Title = null,
    string? Description = null,
    TaskKind? Kind = null,
    TaskState? Status = null,
    TaskPriority? Priority = null,
    Guid? ProjectId = null,
    Guid? RepositoryId = null,
    Guid? ParentTaskId = null,
    bool ClearParent = false,
    string? Metadata = null,
    string? Actor = null);

public sealed record TaskResult(TaskItem Task, bool Created, IReadOnlyList<EntityLink> Links);

public sealed record TaskDetail(
    TaskItem Task,
    IReadOnlyList<TaskComment> Comments,
    IReadOnlyList<EntityLink> Links,
    IReadOnlyList<EntityHistoryEntry> History);

public sealed record TaskCommentResult(TaskItem Task, TaskComment Comment);

public sealed record TaskDependencyResult(TaskItem Task, TaskItem DependsOn, bool Created);

public sealed record TaskHistoryResult(TaskItem Task, IReadOnlyList<EntityHistoryEntry> History);

public sealed record TaskTreeResult(IReadOnlyList<TaskTreeNode> Roots);

public sealed record TodoImportResult(
    bool DryRun,
    int Found,
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<TodoImportItem> Items);

public sealed record TodoImportItem(string ExternalKey, string Title, TaskState Status, string Action);

public sealed record TodoExportResult(string FileName, string Content, int Count);

public sealed record SaveKnowledgeRequest(
    string Title,
    string Content,
    KnowledgeType Type,
    Guid? ProjectId = null,
    Guid? RepositoryId = null,
    string? SourceRef = null,
    string? ExternalKey = null,
    string? CreatedBy = null,
    string? Metadata = null,
    IReadOnlyList<KnowledgeLinkRequest>? Links = null,
    string Source = "agent");

public sealed record UpdateKnowledgeRequest(
    int ExpectedVersion,
    string? Title = null,
    string? Content = null,
    KnowledgeType? Type = null,
    KnowledgeStatus? Status = null,
    string? Metadata = null,
    string? Actor = null);

public sealed record KnowledgeLinkRequest(
    string TargetType,
    Guid TargetId,
    string Relation = LinkRelations.Relates,
    string? Note = null);

public sealed record KnowledgeLinkResult(EntityLink Link, bool Created);

public sealed record KnowledgeDetail(
    KnowledgeEntry Entry,
    IReadOnlyList<EntityLink> Links,
    IReadOnlyList<EntityHistoryEntry> History,
    bool Embedded);

public sealed record KnowledgeSearchRequest(
    string Query,
    string Mode = "hybrid",
    Guid? ProjectId = null,
    Guid? RepositoryId = null,
    IReadOnlyList<KnowledgeType>? Types = null,
    IReadOnlyList<KnowledgeStatus>? Statuses = null,
    bool GlobalOnly = false,
    bool IncludeArchived = false,
    int Limit = 20,
    int Offset = 0);

public sealed record KnowledgeSearchHit(KnowledgeEntry Entry, double Score, string MatchedBy);

public sealed record KnowledgeSearchResult(
    IReadOnlyList<KnowledgeSearchHit> Hits,
    IReadOnlyList<string> Warnings,
    string Mode);

public sealed record RevisionRunResult(
    Guid RunId,
    int Scanned,
    int JobsCreated,
    int JobsSkipped,
    IReadOnlyList<Guid> JobIds,
    IReadOnlyList<string> Warnings);

public sealed record ConsolidationAnalysisResult(
    Guid JobId,
    string Status,
    bool LlmConfigured,
    string? Error,
    string? Proposal);

public sealed record ConsolidationApplyOutcome(
    bool Success,
    Guid JobId,
    string Status,
    Guid? ResultKnowledgeId,
    string? Error);

public sealed record ConsolidationJobDetail(
    ConsolidationJob Job,
    IReadOnlyList<ConsolidationEntity> Items,
    IReadOnlyList<EntityHistoryEntry> History);

public sealed record ConsolidationEntity(Guid Id, int Version, string Title, string Content, KnowledgeType Type, KnowledgeStatus Status, string ContentHash);

