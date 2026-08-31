using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Stores;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer;

/// <summary>
/// Ядро индексации: per-file Analyzer -> Symbols/Relations/Chunks -> diff vs DB -> FTS upsert + Vector upsert -> IndexedCommit=HEAD.
/// Поддерживает JobType Sync/Index/Embedding/Rebuild (indexing.md §2, §4, §5).
/// </summary>
public sealed class IndexingService
{
    private readonly IRepositoryStore _repoStore;
    private readonly IFileStore _fileStore;
    private readonly IChunkStore _chunkStore;
    private readonly ISymbolStore _symbolStore;
    private readonly IVectorStoreFactory _vectorStoreFactory;
    private readonly FtsHelper _fts;
    private readonly IEmbeddingProviderFactory _embeddingsFactory;
    private readonly IEnumerable<ICodeAnalyzer> _analyzers;
    private readonly IGitService _git;
    private readonly IndexingJobStore _jobStore;
    private readonly IOptionsMonitor<IndexerOptions> _indexerOptions;
    private readonly CodeRag.Domain.Ports.IIndexingErrorStore _errorStore;
    private readonly CodeRag.Domain.Ports.IRelationStore _relationStore;
    private readonly IEnumerable<IRelationExtractor> _relationExtractors;
    private readonly ILogger<IndexingService> _logger;

    public IndexingService(
        IRepositoryStore repoStore,
        IFileStore fileStore,
        IChunkStore chunkStore,
        ISymbolStore symbolStore,
        IVectorStoreFactory vectorStoreFactory,
        FtsHelper fts,
        IEmbeddingProviderFactory embeddingsFactory,
        IEnumerable<ICodeAnalyzer> analyzers,
        IGitService git,
        IndexingJobStore jobStore,
        IOptionsMonitor<IndexerOptions> indexerOptions,
        CodeRag.Domain.Ports.IIndexingErrorStore errorStore,
        CodeRag.Domain.Ports.IRelationStore relationStore,
        IEnumerable<IRelationExtractor> relationExtractors,
        ILogger<IndexingService> logger)
    {
        _repoStore = repoStore;
        _fileStore = fileStore;
        _chunkStore = chunkStore;
        _symbolStore = symbolStore;
        _vectorStoreFactory = vectorStoreFactory;
        _fts = fts;
        _embeddingsFactory = embeddingsFactory;
        _analyzers = analyzers;
        _git = git;
        _jobStore = jobStore;
        _indexerOptions = indexerOptions;
        _errorStore = errorStore;
        _relationStore = relationStore;
        _relationExtractors = relationExtractors;
        _logger = logger;
    }

    /// <summary>
    /// (maxChars, overlapChars) chunk budget. Uses the RAG-tuned fixed <see cref="IndexerOptions.MaxChunkChars"/>,
    /// capped by the embedding model context length (n_ctx from /v1/models) so a chunk never overflows the
    /// window. Always applied — oversized chunks are split even before the context length is detected,
    /// so a whole class/method is never embedded as one diluted vector.
    /// </summary>
    private (int MaxChars, int OverlapChars) GetChunkBudget(EmbeddingDomain domain)
    {
        var o = _indexerOptions.CurrentValue;
        var maxChars = o.MaxChunkChars;
        // Cap the chunk budget by THIS domain's model context length so a chunk never overflows that model's window.
        if (_embeddingsFactory.Get(domain).ContextLength is > 0)
        {
            var contextChars = (int)Math.Max(256, _embeddingsFactory.Get(domain).ContextLength.Value * o.CharsPerToken);
            maxChars = Math.Min(maxChars, contextChars);
        }
        maxChars = Math.Max(256, maxChars);
        var overlapChars = (int)(maxChars * o.ChunkOverlapFraction);
        return (maxChars, overlapChars);
    }

    /// <summary>True if the file extension is a known binary type.</summary>
    private bool IsBinaryPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext is not { Length: > 1 }) return false;
        return _indexerOptions.CurrentValue.BinaryExtensions.Contains(ext[1..]);
    }

    /// <summary>True if content looks binary (contains NUL bytes).</summary>
    private static bool LooksBinary(string content)
    {
        var limit = Math.Min(content.Length, 8192);
        for (var i = 0; i < limit; i++)
            if (content[i] == '\0') return true;
        return false;
    }

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        _logger.LogInformation("Executing job {JobId} type={Type} repo={Repo}", job.Id, job.Type, job.RepositoryId);

        job.MarkRunning();
        await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);

        try
        {
            switch (job.Type)
            {
                case JobType.Sync:
                    await ExecuteSyncAsync(job, ct).ConfigureAwait(false);
                    break;
                case JobType.Index:
                    await ExecuteIndexAsync(job, ct).ConfigureAwait(false);
                    break;
                case JobType.Embedding:
                    await ExecuteEmbeddingAsync(job, ct).ConfigureAwait(false);
                    break;
                case JobType.Rebuild:
                    await ExecuteRebuildAsync(job, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Unknown job type {job.Type}");
            }

            job.MarkCompleted();
            await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);
            _logger.LogInformation("Job {JobId} completed", job.Id);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
            await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);
            _logger.LogWarning("Job {JobId} cancelled", job.Id);
        }
        catch (Exception ex)
        {
            job.MarkFailed(TruncateError(ex.Message), DateTimeOffset.UtcNow);
            await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);
            _logger.LogError(ex, "Job {JobId} failed", job.Id);

            // Обновить repository LastError/Status=Error (indexing.md §7)
            try
            {
                var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false);
                if (repo is not null)
                {
                    repo.MarkError(TruncateError(ex.Message));
                    await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
                }
            }
            catch { }
            throw;
        }
    }

    // ---- Sync: fetch + GetHead + diff + enqueue Index if needed (indexing.md §2 поток) ----
    private async Task ExecuteSyncAsync(IndexingJob job, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        if (!repo.Enabled || repo.Status == RepositoryStatus.Paused)
        {
            _logger.LogInformation("Sync skipped repo {Repo} status={Status} enabled={Enabled}", repo.Id, repo.Status, repo.Enabled);
            return;
        }

        repo.MarkSyncing();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

        // Proxy per-repo (git-proxy-auth.md §2) — берём из proxy_id если есть
        string? proxyUrl = null; // TODO: resolve from proxies table via ProxyConfig if needed

        try
        {
            await _git.FetchAsync(repo.LocalPath.Value, repo.Branch, proxyUrl, ct).ConfigureAwait(false);
            var newHead = await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);

            repo.UpdateCurrentCommit(newHead);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

            if (string.Equals(newHead, repo.IndexedCommit, StringComparison.Ordinal))
            {
                _logger.LogInformation("Sync done repo {Repo}: HEAD {Head} already indexed", repo.Id, newHead);
                repo.MarkActive();
                await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
                return;
            }

            // git diff IndexedCommit..newHead
            var diff = await _git.DiffAsync(repo.LocalPath.Value, repo.IndexedCommit, newHead, ct).ConfigureAwait(false);
            _logger.LogInformation("Sync diff repo {Repo}: {Count} changed files (full={Full})", repo.Id, diff.ChangedFiles.Count, diff.IsFullDiff);

            // Enqueue Index job (или Rebuild если diff > threshold) — но делаем синхронно здесь для простоты потока Sync->Index
            var indexJob = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Index, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, newHead);
            indexJob.MarkRunning();
            await _jobStore.UpsertAsync(indexJob, ct).ConfigureAwait(false);

            // Непосредственно выполнить индексацию в рамках Sync джобы (упрощённый поток Sync->Index)
            await ExecuteIndexCoreAsync(indexJob, diff, ct).ConfigureAwait(false);

            indexJob.MarkCompleted();
            await _jobStore.UpsertAsync(indexJob, ct).ConfigureAwait(false);
            repo.MarkActive();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            repo.MarkError(TruncateError(ex.Message));
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            throw;
        }
    }

    private Task ExecuteIndexAsync(IndexingJob job, CancellationToken ct) =>
        ExecuteIndexCoreAsync(job, null, ct);

    private async Task ExecuteIndexCoreAsync(IndexingJob job, GitDiff? precomputedDiff, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        repo.MarkIndexing();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

        var from = job.FromCommit ?? repo.IndexedCommit;
        var to = job.ToCommit ?? repo.CurrentCommit;
        if (string.IsNullOrWhiteSpace(to))
        {
            // Если ToCommit не задан — взять HEAD
            to = await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);
        }

        var diff = precomputedDiff ?? await _git.DiffAsync(repo.LocalPath.Value, from, to, ct).ConfigureAwait(false);
        var total = diff.ChangedFiles.Count;
        if (total == 0)
        {
            _logger.LogInformation("Index job {JobId}: empty diff, nothing to do", job.Id);
            await CommitIndexedAsync(repo, to, ct).ConfigureAwait(false);
            return;
        }

        var processed = 0;
        var lastProgressSave = DateTime.UtcNow;
        var pendingChunks = new List<Domain.Entities.Chunk>();
        var csharpTouched = false;

        foreach (var cf in diff.ChangedFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (IsCSharpPath(cf.Path) || (cf.Kind == GitChangeKind.Renamed && IsCSharpPath(cf.OldPath)))
                csharpTouched = true;

            if (cf.Kind == GitChangeKind.Deleted)
            {
                await DeleteFileAsync(repo, cf.Path, ct).ConfigureAwait(false);
            }
            else
            {
                // Renamed: delete old, add new
                if (cf.Kind == GitChangeKind.Renamed && !string.IsNullOrWhiteSpace(cf.OldPath))
                    await DeleteFileAsync(repo, cf.OldPath!, ct).ConfigureAwait(false);

                await IndexFileAsync(repo, cf.Path, pendingChunks, ct).ConfigureAwait(false);
            }

            processed++;

            // Progress (processed/total) — пишем в БД раз в N файлов / 1s (indexing.md §4)
            var now = DateTime.UtcNow;
            if (processed % 10 == 0 || (now - lastProgressSave).TotalSeconds >= 1)
            {
                var p = total == 0 ? 1.0 : (double)processed / total;
                job.UpdateProgress(p);
                await _jobStore.UpdateProgressAsync(job.Id, p, ct).ConfigureAwait(false);
                lastProgressSave = now;
            }
        }

        // Embedding батчами (indexing.md §5) с кэшем по ContentHash+Model+ConfigHash.
        // Code и text уходят в СВОИ модели/векторные пространства — группируем чанки по домену.
        if (pendingChunks.Count > 0)
        {
            foreach (var group in pendingChunks.GroupBy(c => c.EmbeddingDomain))
            {
                await EmbedAndUpsertAsync(group.ToList(), ct).ConfigureAwait(false);
            }
        }

        // C# relations are rebuilt authoritatively from the whole repo once any C# file changed
        // (per-file C# symbols get fresh IDs each run, so only a full-repo extraction stays consistent).
        if (csharpTouched)
        {
            try
            {
                await RebuildCSharpRelationsAsync(repo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never fail the index run (and never leave the graph empty) if extraction misbehaves.
                _logger.LogWarning(ex, "C# relation extraction failed for repo {Repo}; keeping existing relations", repo.Id);
            }
        }

        await CommitIndexedAsync(repo, to, ct).ConfigureAwait(false);

        job.UpdateProgress(1.0);
        await _jobStore.UpdateProgressAsync(job.Id, 1.0, ct).ConfigureAwait(false);
    }

    private async Task DeleteFileAsync(Domain.Entities.Repository repo, string filePath, CancellationToken ct)
    {
        var file = await _fileStore.GetByPathAsync(repo.Id, filePath, ct).ConfigureAwait(false);
        if (file is null) return;

        var chunks = await _chunkStore.GetByFileAsync(file.Id, ct).ConfigureAwait(false);
        var chunkIds = chunks.Select(c => c.Id).ToList();

        if (chunkIds.Count > 0)
        {
            await _fts.DeleteByChunkIdsAsync(chunkIds, ct).ConfigureAwait(false);
            // Each chunk lives in exactly one vector space; deleting from both stores is a safe no-op for the other.
            try { await _vectorStoreFactory.Code.DeleteByChunkIdsAsync(chunkIds, ct).ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Vector delete failed for file {Path}", filePath); }
            try { await _vectorStoreFactory.Text.DeleteByChunkIdsAsync(chunkIds, ct).ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Vector delete failed for file {Path}", filePath); }
        }

        await _fileStore.DeleteAsync(file.Id, ct).ConfigureAwait(false);
        _logger.LogDebug("Deleted file {Path} ({Chunks} chunks)", filePath, chunkIds.Count);
    }

    private async Task IndexFileAsync(Domain.Entities.Repository repo, string filePath, List<Domain.Entities.Chunk> pendingChunks, CancellationToken ct)
    {
        var fullPath = Path.Combine(repo.LocalPath.Value, filePath);
        string content;
        try
        {
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("File not found on disk, deleting from index: {Full}", fullPath);
                await DeleteFileAsync(repo, filePath, ct).ConfigureAwait(false);
                return;
            }
            content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file {Path}, skipping", filePath);
            await LogIndexingErrorAsync(repo.Id, filePath, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        // Skip binary files entirely (images, archives, models, ...):
        // NUL bytes corrupt TEXT columns (sqlite truncates at NUL) and produce garbage vectors.
        if (IsBinaryPath(filePath) || LooksBinary(content))
        {
            var existingBin = await _fileStore.GetByPathAsync(repo.Id, filePath, ct).ConfigureAwait(false);
            if (existingBin is not null)
            {
                _logger.LogInformation("Removing binary file {Path} from index", filePath);
                await DeleteFileAsync(repo, filePath, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug("Skipping binary file {Path}", filePath);
            }
            return;
        }

        var hash = ComputeHash(content);
        var existing = await _fileStore.GetByPathAsync(repo.Id, filePath, ct).ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.Hash, hash, StringComparison.Ordinal))
        {
            _logger.LogDebug("File unchanged, skip {Path}", filePath);
            return;
        }

        // Определить язык и анализатор (analyzers.md)
        var language = CodeRag.Analyzers.Abstractions.LanguageDetector.Detect(filePath, content);
        var analyzer = _analyzers.FirstOrDefault(a => a.CanAnalyze(filePath));

        // Классифицировать файл: code (исходник) vs text (markdown/docs/prose) — выбор embedding-домена.
        var domain = ContentClassifier.Classify(filePath, language);

        // Если файла уже был — удалить старые данные перед вставкой (diff vs DB)
        if (existing is not null)
        {
            var oldChunks = await _chunkStore.GetByFileAsync(existing.Id, ct).ConfigureAwait(false);
            var oldIds = oldChunks.Select(c => c.Id).ToList();
            if (oldIds.Count > 0)
            {
                await _fts.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false);
                try { await _vectorStoreFactory.Code.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false); } catch { }
                try { await _vectorStoreFactory.Text.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false); } catch { }
            }
            // Символы/реляции каскадно через удаление файла? Удаляем явно
            await _chunkStore.DeleteByFileAsync(existing.Id, ct).ConfigureAwait(false);
            await _symbolStore.DeleteByFileAsync(existing.Id, ct).ConfigureAwait(false);
        }

        // Создать/обновить FileEntry
        var fileId = existing?.Id ?? Guid.NewGuid();
        var fileEntry = Domain.Entities.FileEntry.Create(fileId, repo.Id, filePath, null, language, hash);
        await _fileStore.UpsertAsync(fileEntry, ct).ConfigureAwait(false);

        var (budgetMaxChars, budgetOverlapChars) = GetChunkBudget(domain);

        if (analyzer is null)
        {
            // Fallback чанкинг: sliding window (indexing.md §6), sized from the model budget when known
            var fallbackChunks = CreateFallbackChunks(repo.Id, fileId, filePath, language, domain, content, budgetMaxChars, budgetOverlapChars);
            foreach (var ch in fallbackChunks)
            {
                await _chunkStore.UpsertAsync(ch, ct).ConfigureAwait(false);
                await _fts.UpsertAsync(ch.Id, repo.Id, ch.Source, null, null, filePath, null, null, ct).ConfigureAwait(false);
                if (ShouldEmbed(ch))
                {
                    pendingChunks.Add(ch);
                }
            }
            return;
        }

        try
        {
            var ctx = new FileContext(repo.Id, null, filePath, content, language ?? analyzer.Language);
            var result = await analyzer.AnalyzeAsync(ctx, ct).ConfigureAwait(false);

            // Symbols
            foreach (var s in result.Symbols)
            {
                var sym = MapSymbol(s, repo.Id, fileId);
                await _symbolStore.UpsertAsync(sym, ct).ConfigureAwait(false);
            }

            // Chunks — перемапить на Domain.Chunk с ContentHash/Model/ConfigHash
            foreach (var ac in result.Chunks)
            {
                var symName = ac.SymbolId.HasValue ? result.Symbols.FirstOrDefault(s => s.Id == ac.SymbolId.Value)?.Name : null;

                var chunksToStore = new List<Domain.Entities.Chunk>();
                // Oversized analyzer chunks (e.g. a huge generated class) would overflow the model
                // context — split into budget-sized windows with a context header.
                if (budgetMaxChars > 0 && ac.Source.Length > budgetMaxChars)
                {
                    var header = MakeCommentHeader(filePath, language, symName);
                    var sub = ChunkingHelper.SplitByCharBudget(ac.Source, ac.StartLine, ac.EndLine, budgetMaxChars, budgetOverlapChars, header);
                    foreach (var slice in sub)
                    {
                        chunksToStore.Add(Domain.Entities.Chunk.Create(
                            Guid.NewGuid(), repo.Id, fileId, slice.Text,
                            symbolId: ac.SymbolId, language: language, startLine: slice.StartLine, endLine: slice.EndLine, domain: domain));
                    }
                }
                if (chunksToStore.Count == 0)
                {
                    var domainChunk = Domain.Entities.Chunk.Create(
                        Guid.NewGuid(), repo.Id, fileId, ac.Source,
                        language: language, startLine: ac.StartLine, endLine: ac.EndLine, domain: domain);
                    chunksToStore.Add(domainChunk);
                }

                foreach (var domainChunk in chunksToStore)
                {
                    await _chunkStore.UpsertAsync(domainChunk, ct).ConfigureAwait(false);

                    // FTS upsert
                    await _fts.UpsertAsync(domainChunk.Id, repo.Id, domainChunk.Source, symName, null, filePath, null, null, ct).ConfigureAwait(false);

                    if (ShouldEmbed(domainChunk))
                    {
                        pendingChunks.Add(domainChunk);
                    }
                }
            }

            // Relations: persist intra-file relations from the per-file analyzer for non-C# languages.
            // C# relations are handled by the whole-repo extractor (C# per-file symbols get fresh IDs each
            // run, so per-file C# relations would dangle after a reindex).
            if (language != SupportedLanguages.CSharp && result.Relations.Count > 0)
            {
                var domainRelations = result.Relations
                    .Select(ToDomainRelation)
                    .Where(r => r is not null)!
                    .ToList();
                if (domainRelations.Count > 0)
                    await _relationStore.UpsertBatchAsync(repo.Id, domainRelations, ct).ConfigureAwait(false);
            }

            if (result.Errors.Count > 0)
                foreach (var e in result.Errors)
                    await LogIndexingErrorAsync(repo.Id, filePath, e.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Analyzer failed for {Path} lang={Lang}", filePath, language);
            await LogIndexingErrorAsync(repo.Id, filePath, ex.Message, ct).ConfigureAwait(false);
            // Fallback чанки при ошибке парсера (indexing.md §7)
            var fallbackChunks = CreateFallbackChunks(repo.Id, fileId, filePath, language, domain, content, budgetMaxChars, budgetOverlapChars);
            foreach (var ch in fallbackChunks)
            {
                await _chunkStore.UpsertAsync(ch, ct).ConfigureAwait(false);
                await _fts.UpsertAsync(ch.Id, repo.Id, ch.Source, null, null, filePath, null, null, ct).ConfigureAwait(false);
                if (ShouldEmbed(ch))
                {
                    pendingChunks.Add(ch);
                }
            }
        }
    }

    private bool ShouldEmbed(Domain.Entities.Chunk ch)
    {
        // indexing.md §5: ContentHash не менялся AND Model/ConfigHash не менялся => skip.
        // Сравниваем с моделью СВОЕГО домена чанка (code / text имеют разные модели).
        var provider = _embeddingsFactory.Get(ch.EmbeddingDomain);
        if (!ch.NeedsEmbedding(provider.Model, provider.ConfigHash)) return false;
        // Near-empty chunks produce noise vectors — keep them for FTS/structural, skip embedding.
        var minChars = _indexerOptions.CurrentValue.MinEmbeddingChars;
        return ch.Source.Length >= minChars;
    }

    /// <summary>Comment-style context header prepended to continuation windows of split chunks.</summary>
    private static string MakeCommentHeader(string filePath, string? language, string? symbolName)
    {
        var comment = (language ?? string.Empty).ToLowerInvariant() switch
        {
            "python" => "#",
            "sql" => "--",
            "vbs" => "'",
            _ => "//"
        };
        var sym = string.IsNullOrWhiteSpace(symbolName) ? string.Empty : $" :: {symbolName}";
        return $"{comment} [continued] {filePath}{sym}";
    }

    private async Task EmbedAndUpsertAsync(List<Domain.Entities.Chunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        // Все чанки в батче одного домена (группировка вызывающего кода) — берём модель/хранилище домена.
        var domain = chunks[0].EmbeddingDomain;
        var provider = _embeddingsFactory.Get(domain);
        var store = _vectorStoreFactory.Get(domain);
        var texts = chunks.Select(c => c.Source).ToList();

        _logger.LogInformation("Embedding {Count} {Domain} chunks model={Model} config={Cfg}", chunks.Count, domain.ToText(), provider.Model, provider.ConfigHash);

        float[][] vectors;
        try
        {
            vectors = await provider.EmbedAsync(texts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding failed for {Count} {Domain} chunks, FTS still available (indexing.md §5)", chunks.Count, domain.ToText());
            // Чанки остаются без вектора, FTS/structural поиск работает
            return;
        }

        var entries = new List<VectorEntry>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var ch = chunks[i];
            var vec = vectors[i];
            // Use actual vector length (auto-detected) for Dim metadata; record model context length for freshness checks
            entries.Add(new VectorEntry(ch.Id, ch.RepositoryId, vec, provider.Model, vec.Length, provider.ContextLength));

            ch.MarkEmbedded(provider.Model, provider.ConfigHash);
            await _chunkStore.UpsertAsync(ch, ct).ConfigureAwait(false);
        }

        try
        {
            await store.UpsertAsync(entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector upsert failed for {Count} {Domain} chunks", entries.Count, domain.ToText());
        }
    }

    private async Task ExecuteEmbeddingAsync(IndexingJob job, CancellationToken ct)
    {
        // Переэмбеддить все чанки без репарса (indexing.md §5, Rebuild без репарса при смене модели)
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        var allChunks = await _chunkStore.GetByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);

        // Разбираем по доменам: каждый домен сравнивается со СВОЕЙ моделью и переэмбеддится своим провайдером.
        var groups = allChunks.GroupBy(c => c.EmbeddingDomain).ToList();
        var total = groups.Sum(g => g.Count(c => c.NeedsEmbedding(_embeddingsFactory.Get(g.Key).Model, _embeddingsFactory.Get(g.Key).ConfigHash) && !string.IsNullOrWhiteSpace(c.Source)));
        var activeDomains = groups.Count(g => g.Any(c => c.NeedsEmbedding(_embeddingsFactory.Get(g.Key).Model, _embeddingsFactory.Get(g.Key).ConfigHash) && !string.IsNullOrWhiteSpace(c.Source)));

        _logger.LogInformation("Embedding job {JobId}: {Total} chunks total, need re-embed in {Domains} domain(s)", job.Id, allChunks.Count, activeDomains);

        if (total == 0) return;

        const int batchSize = 64;
        var done = 0;
        foreach (var g in groups)
        {
            var provider = _embeddingsFactory.Get(g.Key);
            var toReembed = g.Where(c => c.NeedsEmbedding(provider.Model, provider.ConfigHash) && !string.IsNullOrWhiteSpace(c.Source)).ToList();
            for (int i = 0; i < toReembed.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = toReembed.Skip(i).Take(batchSize).ToList();
                await EmbedAndUpsertAsync(batch, ct).ConfigureAwait(false);

                done += batch.Count;
                var p = (double)done / total;
                job.UpdateProgress(p);
                await _jobStore.UpdateProgressAsync(job.Id, p, ct).ConfigureAwait(false);
            }
        }

        job.UpdateProgress(1.0);
        await _jobStore.UpdateProgressAsync(job.Id, 1.0, ct).ConfigureAwait(false);
    }

    private async Task ExecuteRebuildAsync(IndexingJob job, CancellationToken ct)
    {
        // Rebuild: полная переиндексация всех файлов + переэмбеддинг.
        // При смене модели — может быть только Embedding без репарса, но Rebuild делает всё.
        _logger.LogInformation("Rebuild job {JobId} repo {Repo}", job.Id, job.RepositoryId);
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        // Full clear via stores (no raw SQL here), then re-index all files (diff null..HEAD).
        // ResetIndexAsync drops a dim-mismatched vec table (stale index) — the only place a
        // vec table is ever dropped, and only on explicit user-confirmed rebuild.
        // Code и text имеют свои модели/размерности — сбрасываем каждое пространство отдельно.
        foreach (var domain in new[] { EmbeddingDomain.Code, EmbeddingDomain.Text })
        {
            try
            {
                await _vectorStoreFactory.Get(domain).ResetIndexAsync(_embeddingsFactory.Get(domain).Dimensions, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rebuild: vec reset failed for {Domain} repo {Repo}", domain.ToText(), repo.Id);
            }
        }
        await _vectorStoreFactory.Code.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _vectorStoreFactory.Text.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _fts.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _chunkStore.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _symbolStore.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _fileStore.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        _logger.LogInformation("Rebuild job {JobId}: index for repo {Repo} cleared", job.Id, repo.Id);

        var head = job.ToCommit ?? await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);
        var diff = await _git.DiffAsync(repo.LocalPath.Value, null, head, ct).ConfigureAwait(false);
        var fakeJob = IndexingJob.Create(job.Id, job.RepositoryId, JobType.Index, JobStatus.Running, job.StartedAt, null, head);
        // reuse core
        await ExecuteIndexCoreAsync(fakeJob, diff, ct).ConfigureAwait(false);
        await CommitIndexedAsync(repo, head, ct).ConfigureAwait(false);
    }

    private async Task CommitIndexedAsync(Domain.Entities.Repository repo, string head, CancellationToken ct)
    {
        // IndexedCommit=HEAD (indexing.md §2) — persisted via the repository store
        repo.MarkIndexSucceeded(head);
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<Domain.Entities.Chunk> CreateFallbackChunks(Guid repoId, Guid fileId, string filePath, string? language, EmbeddingDomain domain, string content, int window = 3200, int overlap = 320)
    {
        // Sliding window: 800 токенов (прим. 3200 символов) overlap 80 токенов (~320 символов) по умолчанию;
        // переопределяется бюджетом модели (n_ctx), когда известен.
        window = Math.Max(256, window);
        overlap = Math.Clamp(overlap, 0, window - 1);
        var list = new List<Domain.Entities.Chunk>();
        if (string.IsNullOrEmpty(content)) return list;
        var lines = content.Split('\n');
        var sb = new System.Text.StringBuilder();
        // Простая реализация: режем по символам, сохраняя границы строк примерно
        for (int pos = 0; pos < content.Length; pos += window - overlap)
        {
            var len = Math.Min(window, content.Length - pos);
            var slice = content.Substring(pos, len);
            // Оценка start/end line
            var prefix = content[..pos];
            var startLine = prefix.Count(c => c == '\n') + 1;
            var endLine = startLine + slice.Count(c => c == '\n');
            var ch = Domain.Entities.Chunk.Create(Guid.NewGuid(), repoId, fileId, slice, language: language, startLine: startLine, endLine: endLine, domain: domain);
            list.Add(ch);
            if (pos + len >= content.Length) break;
        }
        return list;
    }

    private static Domain.Entities.Symbol MapSymbol(CodeRag.Analyzers.Abstractions.Symbol s, Guid repoId, Guid fileId)
    {
        var kind = Enum.TryParse<Domain.Enums.SymbolKind>(s.Kind, true, out var k) ? k : Domain.Enums.SymbolKind.Unknown;
        return Domain.Entities.Symbol.Create(
            s.Id == Guid.Empty ? Guid.NewGuid() : s.Id,
            repoId,
            fileId,
            s.Language ?? "unknown",
            kind,
            s.Name ?? "unknown",
            fqn: s.Fqn,
            @namespace: s.Namespace,
            parentSymbolId: s.ParentSymbolId,
            startLine: s.StartLine,
            endLine: s.EndLine,
            startCol: s.StartColumn,
            endCol: s.EndColumn,
            signature: s.Signature);
    }

    private static bool IsCSharpPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Path.GetExtension(path) is ".cs" or ".csx";
    }

    private static CodeRag.Domain.Entities.Relation? ToDomainRelation(CodeRag.Analyzers.Abstractions.Relation r)
    {
        if (r.FromSymbolId == Guid.Empty || r.ToSymbolId == Guid.Empty) return null;
        if (r.FromSymbolId == r.ToSymbolId) return null; // self-relations are noise
        if (!Enum.TryParse<CodeRag.Domain.Enums.RelationType>(r.RelationType, true, out var t)) return null;
        return new CodeRag.Domain.Entities.Relation(r.FromSymbolId, r.ToSymbolId, t);
    }

    private static readonly string[] CSharpExcludedDirs =
        { "bin", "obj", ".git", "node_modules", ".vs", "packages", ".nuget", "dist", "build", ".idea", ".gradle" };

    private static bool HasExcludedSegment(string full)
    {
        foreach (var part in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.Length > 0 && CSharpExcludedDirs.Contains(part, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private List<SourceFile> EnumerateCSharpFiles(string root, CancellationToken ct)
    {
        var files = new List<SourceFile>();
        if (!Directory.Exists(root)) return files;
        foreach (var pattern in new[] { "*.cs", "*.csx" })
        {
            foreach (var full in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (HasExcludedSegment(full)) continue;
                try
                {
                    var content = File.ReadAllText(full);
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    files.Add(new SourceFile(Path.GetRelativePath(root, full), content));
                }
                catch { /* skip unreadable file */ }
            }
        }
        return files;
    }

    private async Task RebuildCSharpRelationsAsync(Domain.Entities.Repository repo, CancellationToken ct)
    {
        var extractor = _relationExtractors.FirstOrDefault(
            e => string.Equals(e.Language, SupportedLanguages.CSharp, StringComparison.OrdinalIgnoreCase));
        if (extractor is null)
        {
            _logger.LogInformation("No C# relation extractor registered for repo {Repo}; skipping relation rebuild", repo.Id);
            return;
        }

        var files = EnumerateCSharpFiles(repo.LocalPath.Value, ct);
        if (files.Count == 0)
        {
            // No C# source remains — clear all C# relations for the repo.
            await _relationStore.ReplaceForLanguageAsync(repo.Id, SupportedLanguages.CSharp, Array.Empty<CodeRag.Domain.Entities.Relation>(), ct).ConfigureAwait(false);
            return;
        }

        var context = new RelationExtractionContext(repo.Id, files);
        var extracted = await extractor.ExtractAsync(context, ct).ConfigureAwait(false);
        var domainRelations = extracted.Select(ToDomainRelation).Where(r => r is not null)!.ToList();

        await _relationStore.ReplaceForLanguageAsync(repo.Id, SupportedLanguages.CSharp, domainRelations, ct).ConfigureAwait(false);
        _logger.LogInformation("Rebuilt C# relations for repo {Repo}: {Files} files -> {Count} relations", repo.Id, files.Count, domainRelations.Count);
    }

    private static string ComputeHash(string source) => Domain.Entities.Chunk.ComputeHash(source);

    private static string TruncateError(string msg) => msg.Length <= 2000 ? msg : msg[..2000];

    private async Task LogIndexingErrorAsync(Guid repoId, string filePath, string message, CancellationToken ct)
    {
        try
        {
            await _errorStore.AddAsync(repoId, filePath, TruncateError(message), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist indexing error for {Path}", filePath);
        }
    }
}
