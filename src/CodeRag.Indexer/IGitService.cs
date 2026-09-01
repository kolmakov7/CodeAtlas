using CodeRag.Domain.Entities;

namespace CodeRag.Indexer;

/// <summary>
/// Абстракция git-операций (indexing.md §2, git-proxy-auth.md §1).
/// Реализация — CLI git (primary) с поддержкой proxy/credentials per-repository.
/// </summary>
public interface IGitService
{
    Task CloneAsync(string remoteUrl, string localPath, string branch, ProxyConfig? proxy, Credential? credential, CancellationToken ct);

    Task FetchAsync(string repoPath, string branch, ProxyConfig? proxy, Credential? credential, CancellationToken ct);

    // Backward compat overload for callers that pass proxyUrl string
    Task FetchAsync(string repoPath, string branch, string? proxyUrl, CancellationToken ct)
        => FetchAsync(repoPath, branch, null, null, ct);

    Task<string> GetHeadAsync(string repoPath, string branch, CancellationToken ct);

    /// <summary>Содержимое файла из git blob (не зависит от working tree, обходит NAME_MAX 255).</summary>
    Task<string> ShowFileAsync(string repoPath, string filePath, string commitSha, CancellationToken ct);

    /// <summary>
    /// Инкрементальный diff IndexedCommit..HEAD (indexing.md §2).
    /// Fallback при force-push/rebase: IsAncestor==false → полный diff через ls-tree vs DB.
    /// </summary>
    Task<GitDiff> DiffAsync(string repoPath, string? fromSha, string toSha, CancellationToken ct);

    Task<bool> IsAncestorAsync(string repoPath, string ancestor, string descendant, CancellationToken ct);

    Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct);
}

public sealed record GitDiff(
    string? FromCommit,
    string ToCommit,
    IReadOnlyList<GitChangedFile> ChangedFiles,
    bool IsFullDiff // true если fallback из-за rebase/force-push или diff > threshold
);

public sealed record GitChangedFile(
    string Path,
    string? OldPath,
    GitChangeKind Kind
);

public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    Unmerged
}
