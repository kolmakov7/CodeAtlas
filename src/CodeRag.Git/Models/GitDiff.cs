namespace CodeRag.Git.Models;

/// <summary>Результат git diff --name-status -M.</summary>
public sealed record GitDiff(
    string FromSha,
    string ToSha,
    IReadOnlyList<GitDiffEntry> Entries);

public sealed record GitDiffEntry(
    GitDiffKind Kind,
    string Path,
    string? OldPath,
    string? Score);

public enum GitDiffKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Unknown
}
