using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public sealed class TaskComment
{
    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public string Author { get; private set; } = "agent";
    public string Body { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    private TaskComment(Guid id, Guid taskId, string author, string body, DateTimeOffset createdAt)
    {
        Id = id;
        TaskId = taskId;
        Author = author;
        Body = body;
        CreatedAt = createdAt;
    }

    public static TaskComment Create(Guid id, Guid taskId, string body, string? author = null, DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Comment Id must not be empty.");
        if (taskId == Guid.Empty) throw new DomainValidationException("TaskId must not be empty.");
        if (string.IsNullOrWhiteSpace(body)) throw new DomainValidationException("Comment body must not be empty.");
        return new TaskComment(id, taskId, string.IsNullOrWhiteSpace(author) ? "agent" : author.Trim(), body.Trim(), createdAt ?? DateTimeOffset.UtcNow);
    }

    public static TaskComment Rehydrate(Guid id, Guid taskId, string author, string body, DateTimeOffset createdAt)
        => new(id, taskId, author, body, createdAt);
}
