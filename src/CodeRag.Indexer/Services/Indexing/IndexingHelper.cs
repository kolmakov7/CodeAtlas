using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Git.Models;

namespace CodeRag.Indexer.Services.Indexing;

internal static class IndexingHelper
{
    public const string CSharpExtension = ".cs";
    public const string CSharpScriptExtension = ".csx";

    public static string TruncateError(string msg) => msg.Length <= 2000 ? msg : msg[..2000];

    public static async Task CommitIndexedAsync(IRepositoryStore store, Repository repo, string head, CancellationToken ct)
    {
        repo.MarkIndexSucceeded(head);
        await store.UpsertAsync(repo, ct).ConfigureAwait(false);
    }

    public static GitDiff ApplyDirectoryFilter(Repository repo, GitDiff diff)
    {
        if (!repo.DirectoryFilterEnabled) return diff;
        var filtered = diff.Entries.Where(f => repo.IsPathAllowed(f.Path)).ToList();
        return new GitDiff(diff.FromSha, diff.ToSha, filtered, diff.IsFullDiff);
    }

    public static bool IsCSharpPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Path.GetExtension(path) is CSharpExtension or CSharpScriptExtension;
    }

    public static string ComputeHash(string source) => Domain.Entities.Chunk.ComputeHash(source);
}
