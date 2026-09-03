using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using CodeRag.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer.Services.Indexing;

/// <summary>
/// Языково-агностичный оркестратор whole-repository извлечения связей.
/// Перечисляет файлы в git, фильтрует пути через <see cref="IRepositoryRelationExtractor.IsRelevantPath"/>
/// (языковые детали — в экстракторе, а не здесь), читает контент параллельно (git show)
/// и запускает экстрактор. Persist отделил от Extract, чтобы handler успел сделать
/// staleness-проверку между извлечением и записью.
/// </summary>
public sealed class RepositoryRelationService
{
    private readonly IRepositoryStore _repoStore;
    private readonly IGitService _git;
    private readonly IRelationStore _relationStore;
    private readonly IEnumerable<IRepositoryRelationExtractor> _extractors;
    private readonly IOptionsMonitor<IndexerOptions> _options;
    private readonly ILogger<RepositoryRelationService> _logger;

    public RepositoryRelationService(
        IRepositoryStore repoStore,
        IGitService git,
        IRelationStore relationStore,
        IEnumerable<IRepositoryRelationExtractor> extractors,
        IOptionsMonitor<IndexerOptions> options,
        ILogger<RepositoryRelationService> logger)
    {
        _repoStore = repoStore;
        _git = git;
        _relationStore = relationStore;
        _extractors = extractors.ToList();
        _options = options;
        _logger = logger;
    }

    /// <summary>Результат извлечения для одного языка (готовые domain-связи).</summary>
    public sealed record LanguageRelations(string Language, int FilesProcessed, IReadOnlyList<Domain.Entities.Relation> Relations);

    /// <summary>
    /// Извлекает связи всех зарегистрированных языков на указанном коммите.
    /// Ошибки одного экстрактора логируются и пропускаются (старые связи языка остаются).
    /// </summary>
    public async Task<IReadOnlyList<LanguageRelations>> ExtractAsync(
        Repository repo,
        string commitSha,
        IProgress<RelationExtractionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var allowedPaths = await EnumerateAllowedPathsAsync(repo.LocalPath.Value, commitSha, ct).ConfigureAwait(false);
        var results = new List<LanguageRelations>();
        var filesDoneBefore = 0;

        foreach (var extractor in _extractors)
        {
            ct.ThrowIfCancellationRequested();

            var relevantPaths = allowedPaths.Where(p => extractor.IsRelevantPath(p)).ToList();
            if (relevantPaths.Count == 0)
            {
                results.Add(new LanguageRelations(extractor.Language, 0, Array.Empty<Domain.Entities.Relation>()));
                continue;
            }

            var files = await ReadFilesAsync(repo.LocalPath.Value, commitSha, relevantPaths, ct).ConfigureAwait(false);
            if (files.Count == 0)
            {
                results.Add(new LanguageRelations(extractor.Language, 0, Array.Empty<Domain.Entities.Relation>()));
                continue;
            }

            // Прогресс на несколько языков: смещаем счётчик на уже обработанные файлы.
            var offset = filesDoneBefore;
            IProgress<RelationExtractionProgress>? scoped = progress is null
                ? null
                : new Progress<RelationExtractionProgress>(p =>
                    progress.Report(new RelationExtractionProgress(
                        p.FilesTotal + offset,
                        p.FilesDone + offset,
                        p.CurrentFile)));

            try
            {
                var context = new RelationExtractionContext(repo.Id, files);
                var extracted = await extractor.ExtractAsync(context, ct, scoped).ConfigureAwait(false);
                var domainRelations = extracted.Select(ToDomainRelation).Where(r => r is not null)!.ToList();
                results.Add(new LanguageRelations(extractor.Language, files.Count, domainRelations));
                _logger.LogInformation(
                    "Extracted {Language} relations for repo {Repo} at {Commit}: {Files} files -> {Count} relations",
                    extractor.Language, repo.Id, commitSha, files.Count, domainRelations.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relation extraction failed for {Language}, repo {Repo}; keeping previous relations", extractor.Language, repo.Id);
            }

            filesDoneBefore += files.Count;
        }

        return results;
    }

    /// <summary>Заменяет связи каждого языка из результатов (атомарно per language).</summary>
    public async Task PersistAsync(Repository repo, IReadOnlyList<LanguageRelations> results, CancellationToken ct = default)
    {
        foreach (var r in results)
        {
            await _relationStore.ReplaceForLanguageAsync(repo.Id, r.Language, r.Relations, ct).ConfigureAwait(false);
            _logger.LogInformation("Persisted {Language} relations for repo {Repo}: {Count}", r.Language, repo.Id, r.Relations.Count);
        }
    }

    private async Task<List<string>> EnumerateAllowedPathsAsync(string repoPath, string commitSha, CancellationToken ct)
    {
        Repository? filterRepo = null;
        try
        {
            var allRepos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
            filterRepo = allRepos.FirstOrDefault(r => string.Equals(r.LocalPath.Value, repoPath, StringComparison.Ordinal));
        }
        catch { }

        var paths = new List<string>();
        foreach (var path in await _git.LsTreeAsync(repoPath, commitSha, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (filterRepo is not null && !filterRepo.IsPathAllowed(path)) continue;
            paths.Add(path);
        }
        return paths;
    }

    private async Task<List<SourceFile>> ReadFilesAsync(string repoPath, string commitSha, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var files = new List<SourceFile>(paths.Count);
        var gate = new object();
        var degree = Math.Max(1, _options.CurrentValue.ParallelFileIndexers);

        await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct }, async (path, token) =>
        {
            ct.ThrowIfCancellationRequested();
            string? content = null;
            try { content = await _git.ShowFileAsync(repoPath, path, commitSha, token).ConfigureAwait(false); }
            catch { /* unreadable file — skip */ }

            if (string.IsNullOrWhiteSpace(content)) return;
            lock (gate) files.Add(new SourceFile(path, content));
        });

        return files;
    }

    private static Domain.Entities.Relation? ToDomainRelation(Analyzers.Abstractions.Relation r)
    {
        if (r.FromSymbolId == Guid.Empty || r.ToSymbolId == Guid.Empty) return null;
        if (r.FromSymbolId == r.ToSymbolId) return null;
        if (!Enum.TryParse<Domain.Enums.RelationType>(r.RelationType, true, out var t)) return null;
        return new Domain.Entities.Relation(r.FromSymbolId, r.ToSymbolId, t);
    }
}
