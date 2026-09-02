using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Ports;
using CodeRag.Git;
using CodeRag.Git.Models;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer.Services.Indexing;

internal sealed class CSharpRelationService
{
    private readonly IRepositoryStore _repoStore;
    private readonly IGitService _git;
    private readonly IRelationStore _relationStore;
    private readonly IEnumerable<IRepositoryRelationExtractor> _extractors;
    private readonly ILogger<CSharpRelationService> _logger;

    private static readonly HashSet<string> CSharpExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", "packages", ".nuget", "dist", "build", ".idea", ".gradle"
    };

    public CSharpRelationService(
        IRepositoryStore repoStore,
        IGitService git,
        IRelationStore relationStore,
        IEnumerable<IRepositoryRelationExtractor> extractors,
        ILogger<CSharpRelationService> logger)
    {
        _repoStore = repoStore;
        _git = git;
        _relationStore = relationStore;
        _extractors = extractors;
        _logger = logger;
    }

    private static bool HasExcludedSegment(string full)
    {
        foreach (var part in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.Length > 0 && CSharpExcludedDirs.Contains(part))
                return true;
        return false;
    }

    private static bool IsCSharpPath(string? path) => !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path) is IndexingHelper.CSharpExtension or IndexingHelper.CSharpScriptExtension;

    private static Domain.Entities.Relation? ToDomainRelation(Relation r)
    {
        if (r.FromSymbolId == Guid.Empty || r.ToSymbolId == Guid.Empty) return null;
        if (r.FromSymbolId == r.ToSymbolId) return null;
        if (!Enum.TryParse<Domain.Enums.RelationType>(r.RelationType, true, out var t)) return null;
        return new Domain.Entities.Relation(r.FromSymbolId, r.ToSymbolId, t);
    }

    private async Task<List<SourceFile>> EnumerateCSharpFilesAsync(string repoPath, string commitSha, CancellationToken ct)
    {
        Domain.Entities.Repository? filterRepo = null;
        try
        {
            var allRepos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
            filterRepo = allRepos.FirstOrDefault(r => string.Equals(r.LocalPath.Value, repoPath, StringComparison.Ordinal));
        }
        catch { }

        var files = new List<SourceFile>();
        var all = await _git.LsTreeAsync(repoPath, commitSha, ct).ConfigureAwait(false);
        foreach (var path in all)
        {
            ct.ThrowIfCancellationRequested();
            if (filterRepo is not null && !filterRepo.IsPathAllowed(path)) continue;
            if (!IsCSharpPath(path)) continue;
            if (HasExcludedSegment(path)) continue;
            try
            {
                var content = await _git.ShowFileAsync(repoPath, path, commitSha, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) continue;
                files.Add(new SourceFile(path, content));
            }
            catch { }
        }
        return files;
    }

    public async Task RebuildAsync(Domain.Entities.Repository repo, string commitSha, CancellationToken ct)
    {
        var extractor = _extractors.FirstOrDefault(e => string.Equals(e.Language, SupportedLanguages.CSharp, StringComparison.OrdinalIgnoreCase));
        if (extractor is null)
        {
            _logger.LogInformation("No C# relation extractor registered for repo {Repo}; skipping relation rebuild", repo.Id);
            return;
        }

        var files = await EnumerateCSharpFilesAsync(repo.LocalPath.Value, commitSha, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            await _relationStore.ReplaceForLanguageAsync(repo.Id, SupportedLanguages.CSharp, Array.Empty<Domain.Entities.Relation>(), ct).ConfigureAwait(false);
            return;
        }

        var context = new RelationExtractionContext(repo.Id, files);
        var extracted = await extractor.ExtractAsync(context, ct).ConfigureAwait(false);
        var domainRelations = extracted.Select(ToDomainRelation).Where(r => r is not null)!.ToList();

        await _relationStore.ReplaceForLanguageAsync(repo.Id, SupportedLanguages.CSharp, domainRelations, ct).ConfigureAwait(false);
        _logger.LogInformation("Rebuilt C# relations for repo {Repo}: {Files} files -> {Count} relations", repo.Id, files.Count, domainRelations.Count);
    }
}
