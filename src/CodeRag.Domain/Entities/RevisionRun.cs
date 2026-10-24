using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public sealed class RevisionRun
{
    public Guid Id { get; private set; }
    public string Trigger { get; private set; } = "manual";
    public string Status { get; private set; } = "running";
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? Stats { get; private set; }
    public string? Error { get; private set; }

    private RevisionRun(Guid id)
    {
        Id = id;
    }

    public static RevisionRun Start(Guid id, string trigger, DateTimeOffset? startedAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("RevisionRun Id must not be empty.");
        return new RevisionRun(id)
        {
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim(),
            Status = "running",
            StartedAt = startedAt ?? DateTimeOffset.UtcNow
        };
    }

    public void Complete(string stats, DateTimeOffset? at = null)
    {
        Status = "completed";
        Stats = stats;
        FinishedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void Fail(string error, string? stats = null, DateTimeOffset? at = null)
    {
        Status = "failed";
        Error = error;
        Stats = stats;
        FinishedAt = at ?? DateTimeOffset.UtcNow;
    }

    public static RevisionRun Rehydrate(
        Guid id, string trigger, string status, DateTimeOffset startedAt,
        DateTimeOffset? finishedAt, string? stats, string? error)
        => new(id)
        {
            Trigger = trigger,
            Status = status,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Stats = stats,
            Error = error
        };
}
