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
    private readonly IVectorStore _vectorStore;
    private readonly FtsHelper _fts;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IEnumerable<ICodeAnalyzer> _analyzers;
    private readonly IGitService _git;
    private readonly IndexingJobStore _jobStore;
    private readonly IOptionsMonitor<IndexerOptions> _indexerOptions;
    private readonly CodeRag.Domain.Ports.IIndexingErrorStore _errorStore;
    private readonly ILogger<IndexingService> _logger;

    public IndexingService(
        IRepositoryStore repoStore,
        IFileStore fileStore,
        IChunkStore chunkStore,
        ISymbolStore symbolStore,
        IVectorStore vectorStore,
        FtsHelper fts,
        IEmbeddingProvider embeddings,
        IEnumerable<ICodeAnalyzer> analyzers,
        IGitService git,
        IndexingJobStore jobStore,
        IOptionsMonitor<IndexerOptions> indexerOptions,
        CodeRag.Domain.Ports.IIndexingErrorStore errorStore,
        ILogger<IndexingService> logger)
    {
        _repoStore = repoStore;
        _fileStore = fileStore;
        _chunkStore = chunkStore;
        _symbolStore = symbolStore;
        _vectorStore = vectorStore;
        _fts = fts;
        _embeddings = embeddings;
        _analyzers = analyzers;
        _git = git;
        _jobStore = jobStore;
        _indexerOptions = indexerOptions;
        _errorStore = errorStore;
        _logger = logger;
    }

    /// <summary>
    /// (maxChars, overlapChars) chunk budget. Uses the RAG-tuned fixed <see cref="IndexerOptions.MaxChunkChars"/>,
    /// capped by the embedding model context length (n_ctx from /v1/models) so a chunk never overflows the
    /// window. Always applied — oversized chunks are split even before the context length is detected,
    /// so a whole class/method is never embedded as one diluted vector.
    /// </summary>
    private (int MaxChars, int OverlapChars) GetChunkBudget()
    {
        var o = _indexerOptions.CurrentValue;
        var maxChars = o.MaxChunkChars;
        if (_embeddings.ContextLength is > 0)
        {
            var contextChars = (int)Math.Max(256, _embeddings.ContextLength.Value * o.CharsPerToken);
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
        var pendingTexts = new List<string>();

        foreach (var cf in diff.ChangedFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (cf.Kind == GitChangeKind.Deleted)
            {
                await DeleteFileAsync(repo, cf.Path, ct).ConfigureAwait(false);
            }
            else
            {
                // Renamed: delete old, add new
                if (cf.Kind == GitChangeKind.Renamed && !string.IsNullOrWhiteSpace(cf.OldPath))
                    await DeleteFileAsync(repo, cf.OldPath!, ct).ConfigureAwait(false);

                await IndexFileAsync(repo, cf.Path, pendingChunks, pendingTexts, ct).ConfigureAwait(false);
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

        // Embedding батчами (indexing.md §5) с кэшем по ContentHash+Model+ConfigHash
        if (pendingChunks.Count > 0)
        {
            await EmbedAndUpsertAsync(pendingChunks, pendingTexts, ct).ConfigureAwait(false);
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
            try { await _vectorStore.DeleteByChunkIdsAsync(chunkIds, ct).ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Vector delete failed for file {Path}", filePath); }
        }

        await _fileStore.DeleteAsync(file.Id, ct).ConfigureAwait(false);
        _logger.LogDebug("Deleted file {Path} ({Chunks} chunks)", filePath, chunkIds.Count);
    }

    private async Task IndexFileAsync(Domain.Entities.Repository repo, string filePath, List<Domain.Entities.Chunk> pendingChunks, List<string> pendingTexts, CancellationToken ct)
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

        // Если файла уже был — удалить старые данные перед вставкой (diff vs DB)
        if (existing is not null)
        {
            var oldChunks = await _chunkStore.GetByFileAsync(existing.Id, ct).ConfigureAwait(false);
            var oldIds = oldChunks.Select(c => c.Id).ToList();
            if (oldIds.Count > 0)
            {
                await _fts.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false);
                try { await _vectorStore.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false); } catch { }
            }
            // Символы/реляции каскадно через удаление файла? Удаляем явно
            await _chunkStore.DeleteByFileAsync(existing.Id, ct).ConfigureAwait(false);
            await _symbolStore.DeleteByFileAsync(existing.Id, ct).ConfigureAwait(false);
        }

        // Создать/обновить FileEntry
        var fileId = existing?.Id ?? Guid.NewGuid();
        var fileEntry = Domain.Entities.FileEntry.Create(fileId, repo.Id, filePath, null, language, hash);
        await _fileStore.UpsertAsync(fileEntry, ct).ConfigureAwait(false);

        var (budgetMaxChars, budgetOverlapChars) = GetChunkBudget();

        if (analyzer is null)
        {
            // Fallback чанкинг: sliding window (indexing.md §6), sized from the model budget when known
            var fallbackChunks = CreateFallbackChunks(repo.Id, fileId, filePath, language, content, budgetMaxChars, budgetOverlapChars);
            foreach (var ch in fallbackChunks)
            {
                await _chunkStore.UpsertAsync(ch, ct).ConfigureAwait(false);
                await _fts.UpsertAsync(ch.Id, repo.Id, ch.Source, null, null, filePath, null, null, ct).ConfigureAwait(false);
                if (ShouldEmbed(ch))
                {
                    pendingChunks.Add(ch);
                    pendingTexts.Add(ch.Source);
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
                            symbolId: ac.SymbolId, language: language, startLine: slice.StartLine, endLine: slice.EndLine));
                    }
                }
                if (chunksToStore.Count == 0)
                {
                    var domainChunk = Domain.Entities.Chunk.Create(
                        Guid.NewGuid(), repo.Id, fileId, ac.Source,
                        language: language, startLine: ac.StartLine, endLine: ac.EndLine);
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
                        pendingTexts.Add(domainChunk.Source);
                    }
                }
            }

            // Relations — если анализатор вернул, вставить (пока stub)
            // TODO: IRelationStore when available

            if (result.Errors.Count > 0)
                foreach (var e in result.Errors)
                    await LogIndexingErrorAsync(repo.Id, filePath, e.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Analyzer failed for {Path} lang={Lang}", filePath, language);
            await LogIndexingErrorAsync(repo.Id, filePath, ex.Message, ct).ConfigureAwait(false);
            // Fallback чанки при ошибке парсера (indexing.md §7)
            var fallbackChunks = CreateFallbackChunks(repo.Id, fileId, filePath, language, content, budgetMaxChars, budgetOverlapChars);
            foreach (var ch in fallbackChunks)
            {
                await _chunkStore.UpsertAsync(ch, ct).ConfigureAwait(false);
                await _fts.UpsertAsync(ch.Id, repo.Id, ch.Source, null, null, filePath, null, null, ct).ConfigureAwait(false);
                if (ShouldEmbed(ch))
                {
                    pendingChunks.Add(ch);
                    pendingTexts.Add(ch.Source);
                }
            }
        }
    }

    private bool ShouldEmbed(Domain.Entities.Chunk ch)
    {
        // indexing.md §5: ContentHash не менялся AND Model/ConfigHash не менялся => skip
        // Chunk.NeedsEmbedding уже отражает это
        if (!ch.NeedsEmbedding(_embeddings.Model, _embeddings.ConfigHash)) return false;
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

    private async Task EmbedAndUpsertAsync(List<Domain.Entities.Chunk> chunks, List<string> texts, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        _logger.LogInformation("Embedding {Count} chunks model={Model} config={Cfg}", chunks.Count, _embeddings.Model, _embeddings.ConfigHash);

        float[][] vectors;
        try
        {
            vectors = await _embeddings.EmbedAsync(texts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding failed for {Count} chunks, FTS still available (indexing.md §5)", chunks.Count);
            // Чанки остаются без вектора, FTS/structural поиск работает
            return;
        }

        var entries = new List<VectorEntry>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var ch = chunks[i];
            var vec = vectors[i];
            // Use actual vector length (auto-detected) for Dim metadata; record model context length for freshness checks
            entries.Add(new VectorEntry(ch.Id, ch.RepositoryId, vec, _embeddings.Model, vec.Length, _embeddings.ContextLength));

            ch.MarkEmbedded(_embeddings.Model, _embeddings.ConfigHash);
            await _chunkStore.UpsertAsync(ch, ct).ConfigureAwait(false);
        }

        try
        {
            await _vectorStore.UpsertAsync(entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector upsert failed for {Count} chunks", entries.Count);
        }
    }

    private async Task ExecuteEmbeddingAsync(IndexingJob job, CancellationToken ct)
    {
        // Переэмбеддить все чанки без репарса (indexing.md §5, Rebuild без репарса при смене модели)
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        var allChunks = await _chunkStore.GetByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        var toReembed = allChunks.Where(c => c.NeedsEmbedding(_embeddings.Model, _embeddings.ConfigHash) && !string.IsNullOrWhiteSpace(c.Source)).ToList();
        var skippedEmpty = allChunks.Count(c => c.NeedsEmbedding(_embeddings.Model, _embeddings.ConfigHash) && string.IsNullOrWhiteSpace(c.Source));

        _logger.LogInformation("Embedding job {JobId}: {Total} chunks, {Need} need re-embed (model {Old}->{New}, {Skipped} skipped empty)", job.Id, allChunks.Count, toReembed.Count, allChunks.FirstOrDefault()?.EmbeddingModel, _embeddings.Model, skippedEmpty);

        var total = toReembed.Count;
        if (total == 0) return;

        var batchSize = 64;
        for (int i = 0; i < toReembed.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = toReembed.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Source).ToList();
            await EmbedAndUpsertAsync(batch, texts, ct).ConfigureAwait(false);

            var p = (double)Math.Min(i + batchSize, total) / total;
            job.UpdateProgress(p);
            await _jobStore.UpdateProgressAsync(job.Id, p, ct).ConfigureAwait(false);
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
        try
        {
            await _vectorStore.ResetIndexAsync(_embeddings.Dimensions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rebuild: vec reset failed for repo {Repo}", repo.Id);
        }
        await _vectorStore.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
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

    private static IReadOnlyList<Domain.Entities.Chunk> CreateFallbackChunks(Guid repoId, Guid fileId, string filePath, string? language, string content, int window = 3200, int overlap = 320)
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
            var ch = Domain.Entities.Chunk.Create(Guid.NewGuid(), repoId, fileId, slice, language: language, startLine: startLine, endLine: endLine);
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
