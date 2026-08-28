using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Запись аудита (таблица activity_log).</summary>
public sealed class ActivityLog
{
    public Guid Id { get; private set; }
    public DateTimeOffset At { get; private set; }
    public string? Actor { get; private set; }
    public string Action { get; private set; }
    public Guid? RepositoryId { get; private set; }
    public string? Detail { get; private set; }

    private ActivityLog(Guid id, DateTimeOffset at, string action)
    {
        Id = id;
        At = at;
        Action = action;
    }

    public static ActivityLog Create(
        Guid id, string action,
        DateTimeOffset? at = null,
        string? actor = null, Guid? repositoryId = null, string? detail = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("ActivityLog Id must not be empty.");
        if (string.IsNullOrWhiteSpace(action)) throw new DomainValidationException("Action must not be empty.");
        return new ActivityLog(id, at ?? DateTimeOffset.UtcNow, action.Trim())
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            RepositoryId = repositoryId,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim()
        };
    }
}
