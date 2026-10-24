using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;

namespace CodeRag.Domain.Models;

public sealed record TaskQuery(
    string? Query = null,
    TaskState? Status = null,
    TaskKind? Kind = null,
    Guid? ProjectId = null,
    Guid? RepositoryId = null,
    Guid? ParentTaskId = null,
    bool IncludeArchived = false,
    bool RootOnly = false,
    int Limit = 100,
    int Offset = 0);

public sealed record TaskTreeNode(TaskItem Task, IReadOnlyList<TaskTreeNode> Children);

public sealed record KnowledgeQuery(
    string? Query = null,
    IReadOnlyList<KnowledgeType>? Types = null,
    IReadOnlyList<KnowledgeStatus>? Statuses = null,
    Guid? ProjectId = null,
    Guid? RepositoryId = null,
    bool GlobalOnly = false,
    bool IncludeArchived = false,
    int Limit = 50,
    int Offset = 0);

public sealed record ConsolidationJobQuery(
    ConsolidationJobStatus? Status = null,
    ConsolidationJobType? Type = null,
    Guid? ProjectId = null,
    Guid? RepositoryId = null,
    int Limit = 100,
    int Offset = 0);
