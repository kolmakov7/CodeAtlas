using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public sealed class TaskItem
{
    public Guid Id { get; private set; }
    public string Title { get; private set; } = "";
    public string? Description { get; private set; }
    public TaskKind Kind { get; private set; }
    public TaskState Status { get; private set; }
    public TaskState? PreviousStatus { get; private set; }
    public TaskPriority Priority { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? RepositoryId { get; private set; }
    public Guid? ParentTaskId { get; private set; }
    public string Source { get; private set; } = "agent";
    public string? ExternalKey { get; private set; }
    public string? CreatedBy { get; private set; }
    public string? Metadata { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public int Version { get; private set; } = 1;

    private TaskItem(Guid id)
    {
        Id = id;
    }

    public static TaskItem Create(
        Guid id,
        string title,
        string? description = null,
        TaskKind kind = TaskKind.Work,
        TaskState status = TaskState.Open,
        TaskPriority priority = TaskPriority.Normal,
        Guid? projectId = null,
        Guid? repositoryId = null,
        Guid? parentTaskId = null,
        string source = "agent",
        string? externalKey = null,
        string? createdBy = null,
        string? metadata = null,
        DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Task Id must not be empty.");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainValidationException("Task title must not be empty.");
        if (parentTaskId == id) throw new DomainValidationException("A task cannot be its own parent.");

        var now = createdAt ?? DateTimeOffset.UtcNow;
        return new TaskItem(id)
        {
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Kind = kind,
            Status = status,
            Priority = priority,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            ParentTaskId = parentTaskId,
            Source = string.IsNullOrWhiteSpace(source) ? "agent" : source.Trim(),
            ExternalKey = string.IsNullOrWhiteSpace(externalKey) ? null : externalKey.Trim(),
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            Metadata = string.IsNullOrWhiteSpace(metadata) ? null : metadata,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };
    }

    public void UpdateContent(string title, string? description, TaskKind kind, TaskPriority priority, string? metadata)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainValidationException("Task title must not be empty.");
        Title = title.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Kind = kind;
        Priority = priority;
        Metadata = string.IsNullOrWhiteSpace(metadata) ? null : metadata;
    }

    public void SetStatus(TaskState status, DateTimeOffset? at = null)
    {
        if (status == Status) return;
        if (status == TaskState.Archived)
        {
            PreviousStatus = Status;
            ArchivedAt = at ?? DateTimeOffset.UtcNow;
        }
        else if (Status == TaskState.Archived)
        {
            PreviousStatus = null;
            ArchivedAt = null;
        }
        Status = status;
    }

    public void SetParent(Guid? parentTaskId)
    {
        if (parentTaskId == Id) throw new DomainValidationException("A task cannot be its own parent.");
        ParentTaskId = parentTaskId;
    }

    public void SetScope(Guid? projectId, Guid? repositoryId)
    {
        ProjectId = projectId;
        RepositoryId = repositoryId;
    }

    public void BumpVersion(DateTimeOffset? at = null)
    {
        Version++;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public static TaskItem Rehydrate(
        Guid id,
        string title,
        string? description,
        TaskKind kind,
        TaskState status,
        TaskState? previousStatus,
        TaskPriority priority,
        Guid? projectId,
        Guid? repositoryId,
        Guid? parentTaskId,
        string source,
        string? externalKey,
        string? createdBy,
        string? metadata,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? archivedAt,
        int version)
        => new(id)
        {
            Title = title,
            Description = description,
            Kind = kind,
            Status = status,
            PreviousStatus = previousStatus,
            Priority = priority,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            ParentTaskId = parentTaskId,
            Source = source,
            ExternalKey = externalKey,
            CreatedBy = createdBy,
            Metadata = metadata,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ArchivedAt = archivedAt,
            Version = version
        };
}
