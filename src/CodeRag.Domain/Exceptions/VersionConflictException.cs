namespace CodeRag.Domain.Exceptions;

/// <summary>Optimistic-concurrency conflict: the entity changed since the caller read it.</summary>
public sealed class VersionConflictException : DomainException
{
    public int ExpectedVersion { get; }
    public int CurrentVersion { get; }

    public VersionConflictException(string entity, Guid id, int expectedVersion, int currentVersion)
        : base($"{entity} {id} was modified concurrently (expected version {expectedVersion}, current {currentVersion}).")
    {
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }
}
