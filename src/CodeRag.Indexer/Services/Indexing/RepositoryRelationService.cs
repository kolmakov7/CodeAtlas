using System.Text.RegularExpressions;
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
    /// <summary>Pseudo-language for edges discovered across linked repositories.</summary>
    public const string CrossRepoLanguage = "crossrepo";

    private readonly IRepositoryStore _repoStore;
    private readonly IGitService _git;
    private readonly IRelationStore _relationStore;
    private readonly IEnumerable<IRepositoryRelationExtractor> _extractors;
    private readonly IRepositoryLinkStore _linkStore;
    private readonly ISymbolCatalog _catalog;
    private readonly ISymbolStore _symbolStore;
    private readonly IFileStore _fileStore;
    private readonly IUsageRecorder _usage;
    private readonly IOptionsMonitor<IndexerOptions> _options;
    private readonly ILogger<RepositoryRelationService> _logger;

    public RepositoryRelationService(
        IRepositoryStore repoStore,
        IGitService git,
        IRelationStore relationStore,
        IEnumerable<IRepositoryRelationExtractor> extractors,
        IRepositoryLinkStore linkStore,
        ISymbolCatalog catalog,
        ISymbolStore symbolStore,
        IFileStore fileStore,
        IUsageRecorder usage,
        IOptionsMonitor<IndexerOptions> options,
        ILogger<RepositoryRelationService> logger)
    {
        _repoStore = repoStore;
        _git = git;
        _relationStore = relationStore;
        _extractors = extractors.ToList();
        _linkStore = linkStore;
        _catalog = catalog;
        _symbolStore = symbolStore;
        _fileStore = fileStore;
        _usage = usage;
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
        var linkedRepoIds = await GetLinkedTargetIdsAsync(repo.Id, ct).ConfigureAwait(false);

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
                var context = new RelationExtractionContext(repo.Id, files, linkedRepoIds);
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

        // Cross-repository references (e.g. app code calling SQL objects in a linked DB repo).
        if (linkedRepoIds.Count > 0)
        {
            try
            {
                var crossRepo = await ExtractCrossRepoAsync(repo, allowedPaths, commitSha, linkedRepoIds, ct).ConfigureAwait(false);
                results.Add(crossRepo);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cross-repo relation extraction failed for repo {Repo}", repo.Id);
            }
        }

        return results;
    }

    private async Task<IReadOnlyCollection<Guid>> GetLinkedTargetIdsAsync(Guid repositoryId, CancellationToken ct)
    {
        try
        {
            var links = await _linkStore.GetByRepositoryAsync(repositoryId, ct).ConfigureAwait(false);
            return links.Where(l => l.SourceRepositoryId == repositoryId)
                .Select(l => l.TargetRepositoryId).Distinct().ToArray();
        }
        catch
        {
            return Array.Empty<Guid>();
        }
    }

    private static readonly Regex IdentifierRegex = new(@"[A-Za-z_][A-Za-z0-9_]{3,}", RegexOptions.Compiled);
    private static readonly Regex QualifiedRegex = new(@"[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".vbs", ".py", ".js", ".ts", ".tsx", ".jsx", ".sql", ".tsql",
        ".java", ".go", ".cpp", ".c", ".h", ".hpp", ".php", ".rb", ".json", ".xml", ".config", ".txt"
    };

    /// <summary>Kinds matched by their bare name (distinctive names); tables/views require a schema prefix.</summary>
    private static readonly HashSet<string> BareNameKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Procedure", "Function", "Class", "Interface", "Struct", "Record", "Enum"
    };

    private static readonly HashSet<string> MeaningfulKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Procedure", "Function", "View", "Table", "Class", "Interface", "Struct", "Record", "Enum"
    };

    private static bool IsTextPath(string path)
    {
        var ext = Path.GetExtension(path);
        return TextExtensions.Contains(ext);
    }

    private static string NormalizeQualified(string s) => s.Replace("[", "").Replace("]", "").Trim().ToLowerInvariant();

    private static bool IsCallContext(string line, int matchIndex)
    {
        var before = line[..matchIndex].TrimEnd();
        return before.EndsWith("EXEC", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("EXECUTE", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("CALL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans the repository's text files for identifiers matching symbols of linked repositories
    /// and emits References/Calls edges from the enclosing local symbol to the foreign symbol.
    /// Bare names are matched only for distinctive kinds (procedures/functions/types); tables and
    /// views require a schema-qualified form (e.g. <c>dbo.Type</c>) to avoid coincidental matches
    /// with common UI identifiers.
    /// </summary>
    private async Task<LanguageRelations> ExtractCrossRepoAsync(
        Repository repo, IReadOnlyList<string> allowedPaths, string commitSha,
        IReadOnlyCollection<Guid> linkedRepoIds, CancellationToken ct)
    {
        var targetSymbols = await _catalog.GetSymbolsAsync(linkedRepoIds, null, ct).ConfigureAwait(false);
        var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var byFqn = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in targetSymbols)
        {
            if (s.Kind is not null && !MeaningfulKinds.Contains(s.Kind)) continue;
            if (BareNameKinds.Contains(s.Kind ?? "") && s.Name.Length >= 4) byName.TryAdd(s.Name, s.Id);
            if (!string.IsNullOrWhiteSpace(s.Fqn) && s.Fqn.Length >= 4) byFqn.TryAdd(NormalizeQualified(s.Fqn), s.Id);
        }
        if (byName.Count == 0 && byFqn.Count == 0)
            return new LanguageRelations(CrossRepoLanguage, 0, Array.Empty<Domain.Entities.Relation>());

        // Map source symbols to their file path + line range.
        var files = await _fileStore.GetByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        var pathById = files.ToDictionary(f => f.Id, f => f.Path);
        var symbols = await _symbolStore.GetByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        var byPath = symbols
            .Where(s => s.StartLine.HasValue)
            .GroupBy(s => pathById.GetValueOrDefault(s.FileId) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var textPaths = allowedPaths.Where(IsTextPath).ToList();
        var read = await ReadFilesAsync(repo.LocalPath.Value, commitSha, textPaths, ct).ConfigureAwait(false);

        var rels = new HashSet<Domain.Entities.Relation>();
        foreach (var file in read)
        {
            if (!byPath.TryGetValue(file.Path, out var fileSymbols) || fileSymbols.Count == 0) continue;
            var lines = file.Content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length < 4) continue;
                var lineNo = i + 1;
                var owner = fileSymbols.FirstOrDefault(s => (s.StartLine ?? 0) <= lineNo && lineNo <= (s.EndLine ?? int.MaxValue));
                if (owner is null) continue;

                // Schema-qualified references (tables/views/procs): dbo.Type, dbo.spGet...
                foreach (Match m in QualifiedRegex.Matches(line))
                {
                    if (!byFqn.TryGetValue(NormalizeQualified(m.Value), out var targetId)) continue;
                    var type = IsCallContext(line, m.Index) ? Domain.Enums.RelationType.Calls : Domain.Enums.RelationType.References;
                    rels.Add(new Domain.Entities.Relation(owner.Id, targetId, type));
                }

                // Bare, distinctive names (procedures/functions/types).
                foreach (Match m in IdentifierRegex.Matches(line))
                {
                    if (!byName.TryGetValue(m.Value, out var targetId)) continue;
                    var type = IsCallContext(line, m.Index) ? Domain.Enums.RelationType.Calls : Domain.Enums.RelationType.References;
                    rels.Add(new Domain.Entities.Relation(owner.Id, targetId, type));
                }
            }
        }

        _logger.LogInformation("Cross-repo extraction for repo {Repo}: {Names} bare names, {Fqns} qualified targets, {Files} files -> {Count} relations",
            repo.Id, byName.Count, byFqn.Count, read.Count, rels.Count);
        return new LanguageRelations(CrossRepoLanguage, read.Count, rels.ToList());
    }

    /// <summary>Заменяет связи каждого языка из результатов (атомарно per language).</summary>
    public async Task PersistAsync(Repository repo, IReadOnlyList<LanguageRelations> results, CancellationToken ct = default)
    {
        foreach (var r in results)
        {
            if (r.Language == CrossRepoLanguage)
            {
                await _relationStore.DeleteCrossRepoByOriginAsync(repo.Id, ct).ConfigureAwait(false);
                if (r.Relations.Count > 0)
                {
                    await _relationStore.UpsertBatchAsync(repo.Id, r.Relations, ct).ConfigureAwait(false);
                    // Referenced foreign objects count as usage (drives doc prioritisation).
                    _usage.RecordSymbols("reference", "cross_repo", r.Relations.Select(x => x.ToSymbolId));
                }
            }
            else
            {
                await _relationStore.ReplaceForLanguageAsync(repo.Id, r.Language, r.Relations, ct).ConfigureAwait(false);
            }
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
