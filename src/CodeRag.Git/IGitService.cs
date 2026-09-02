using CodeRag.Domain.Entities;
using CodeRag.Git.Models;

namespace CodeRag.Git;

/// <summary>
/// Обёртка над CLI git по ADR-004.
/// Все операции через Process.Start с proxy/auth env и -c config.
/// </summary>
public interface IGitService
{
    /// <summary>Клонирование: git clone --branch &lt;branch&gt; &lt;url&gt; &lt;localPath&gt; с proxy/auth.</summary>
    Task CloneAsync(
        string remoteUrl,
        string localPath,
        string branch,
        ProxyConfig? proxy = null,
        Credential? credential = null,
        CancellationToken ct = default);

    /// <summary>Fetch: git -c http.proxy=... fetch origin &lt;branch&gt;</summary>
    Task FetchAsync(
        string localPath,
        string branch,
        ProxyConfig? proxy = null,
        Credential? credential = null,
        CancellationToken ct = default);

    /// <summary>Diff: git diff --name-status -M from..to (R100 parsing).</summary>
    Task<GitDiff> DiffAsync(
        string localPath,
        string fromSha,
        string toSha,
        CancellationToken ct = default);

    /// <summary>HEAD remote branch: git rev-parse origin/&lt;branch&gt;</summary>
    Task<string> GetHeadAsync(
        string localPath,
        string branch,
        CancellationToken ct = default);

    /// <summary>Проверка предка: git merge-base --is-ancestor (exit 0/1).</summary>
    Task<bool> IsAncestorAsync(
        string localPath,
        string ancestorSha,
        string descendantSha,
        CancellationToken ct = default);

    /// <summary>Список файлов: git ls-tree -r --name-only &lt;commit&gt;</summary>
    Task<IReadOnlyList<string>> LsTreeAsync(
        string localPath,
        string commitSha,
        CancellationToken ct = default);

    /// <summary>Remote refs: git ls-remote &lt;url&gt; [&lt;branch&gt;]</summary>
    Task<string?> LsRemoteAsync(
        string remoteUrl,
        string? branch = null,
        ProxyConfig? proxy = null,
        Credential? credential = null,
        CancellationToken ct = default);

    /// <summary>Список всех refs ls-remote (для диагностирования).</summary>
    Task<IReadOnlyList<GitRemoteRef>> LsRemoteAllAsync(
        string remoteUrl,
        ProxyConfig? proxy = null,
        Credential? credential = null,
        CancellationToken ct = default);

    /// <summary>Список веток remote (фильтр refs/heads/*).</summary>
    Task<IReadOnlyList<string>> ListRemoteBranchesAsync(
        string remoteUrl,
        ProxyConfig? proxy = null,
        Credential? credential = null,
        CancellationToken ct = default);

    /// <summary>Корневые директории для ветки (через temp clone + ls-tree).</summary>
    Task<IReadOnlyList<string>> ListRemoteRootDirectoriesAsync(
        string remoteUrl,
        string branch,
        ProxyConfig? proxy = null,
        Credential? credential = null,
        CancellationToken ct = default);

    /// <summary>Корневые директории локального репо для заданного коммита/ветки.</summary>
    Task<IReadOnlyList<string>> ListLocalRootDirectoriesAsync(
        string localPath,
        string commitOrBranch,
        CancellationToken ct = default);
}

public sealed record GitRemoteRef(string Ref, string Sha);
