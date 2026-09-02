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
    private readonly IJobRunner _jobRunner;
    private readonly IOptionsMonitor<IndexerOptions> _indexerOptions;
    private readonly CodeRag.Domain.Ports.IIndexingErrorStore _errorStore;
    private readonly CodeRag.Domain.Ports.IRelationStore _relationStore;
    private readonly IEnumerable<IRelationExtractor> _relationExtractors;
    private readonly CodeRag.Domain.Ports.IRepositoryGroupStore _groupStore;
    private readonly CodeRag.Git.Stores.IProxyStore _proxyStore;
    private readonly CodeRag.Git.Stores.ICredentialStore _credStore;
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
        IJobRunner jobRunner,
        IOptionsMonitor<IndexerOptions> indexerOptions,
        CodeRag.Domain.Ports.IIndexingErrorStore errorStore,
        CodeRag.Domain.Ports.IRelationStore relationStore,
        IEnumerable<IRelationExtractor> relationExtractors,
        CodeRag.Domain.Ports.IRepositoryGroupStore groupStore,
        CodeRag.Git.Stores.IProxyStore proxyStore,
        CodeRag.Git.Stores.ICredentialStore credStore,
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
        _jobRunner = jobRunner;
        _indexerOptions = indexerOptions;
        _errorStore = errorStore;
        _relationStore = relationStore;
        _relationExtractors = relationExtractors;
        _groupStore = groupStore;
        _proxyStore = proxyStore;
        _credStore = credStore;
        _logger = logger;

        _strategies = new Dictionary<JobType, Func<IndexingJob, CancellationToken, Task>>
        {
            [JobType.Clone] = ExecuteCloneAsync,
            [JobType.Sync] = ExecuteSyncAsync,
            [JobType.Index] = ExecuteIndexAsync,
            [JobType.Embedding] = ExecuteEmbeddingAsync,
            [JobType.Rebuild] = ExecuteRebuildAsync
        };
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

    // Strategy: JobType -> handler (убирает switch, см. IIndexingJobHandler)
    private readonly Dictionary<JobType, Func<IndexingJob, CancellationToken, Task>> _strategies;

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        _logger.LogInformation("Executing job {JobId} type={Type} repo={Repo}", job.Id, job.Type, job.RepositoryId);

        job.MarkRunning();
        await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);

        try
        {
            if (!_strategies.TryGetValue(job.Type, out var handler))
                throw new NotSupportedException($"Unknown job type {job.Type}");
            await handler(job, ct).ConfigureAwait(false);

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
            await SetJobProgressAsync(job, 1.0, ct).ConfigureAwait(false);
            return;
        }

        repo.MarkSyncing();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

        var (proxy, cred) = await GetEffectiveProxyAndCredAsync(repo, ct).ConfigureAwait(false);

        try
        {
            // 1) Update the local working copy + resolve the new HEAD.
            await SetJobStatsAsync(job, 0.25, phase: "fetch", ct: ct).ConfigureAwait(false);
            // Clone — отдельный таск: если клона нет, делаем clone здесь (видно в jobs), а не в AddAsync.
            if (!Directory.Exists(repo.LocalPath.Value) || !Directory.Exists(Path.Combine(repo.LocalPath.Value, ".git")))
            {
                _logger.LogInformation("Sync: local path {Path} not cloned, cloning branch {Branch} via proxy/cred", repo.LocalPath.Value, repo.Branch);
                await SetJobStatsAsync(job, 0.10, phase: "clone", ct: ct).ConfigureAwait(false);
                await _git.CloneAsync(repo.GitUrl.Value, repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }
            else
            {
                await _git.FetchAsync(repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }
            var newHead = await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);

            repo.UpdateCurrentCommit(newHead);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

            if (string.Equals(newHead, repo.IndexedCommit, StringComparison.Ordinal))
            {
                _logger.LogInformation("Sync done repo {Repo}: HEAD {Head} already indexed", repo.Id, newHead);
                repo.MarkActive();
                await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
                await SetJobProgressAsync(job, 1.0, ct).ConfigureAwait(false);
                return;
            }

            // 2) Record what changed (git diff IndexedCommit..newHead).
            await SetJobStatsAsync(job, 0.6, phase: "diff", ct: ct).ConfigureAwait(false);
            var diff = await _git.DiffAsync(repo.LocalPath.Value, repo.IndexedCommit, newHead, ct).ConfigureAwait(false);
            _logger.LogInformation("Sync diff repo {Repo}: {Count} changed files (full={Full})", repo.Id, diff.ChangedFiles.Count, diff.IsFullDiff);

            // 3) The structural index is a SEPARATE task: enqueue an Index job (which in turn enqueues
            //    Embedding). Sync only fetches the local copy, records the new HEAD and the diff, then
            //    hands off — so each stage reports its own progress instead of Sync sitting at 0%.
            var indexJob = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Index, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, newHead);
            await _jobRunner.EnqueueAsync(indexJob, ct).ConfigureAwait(false);
            _logger.LogInformation("Sync repo {Repo} enqueued Index job {IndexJob} ({From} -> {To})", repo.Id, indexJob.Id, repo.IndexedCommit, newHead);

            repo.MarkActive();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            await SetJobProgressAsync(job, 1.0, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            repo.MarkError(TruncateError(ex.Message));
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteCloneAsync(IndexingJob job, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        if (!repo.Enabled || repo.Status == RepositoryStatus.Paused)
        {
            _logger.LogInformation("Clone skipped repo {Repo} status={Status}", repo.Id, repo.Status);
            await SetJobProgressAsync(job, 1.0, ct).ConfigureAwait(false);
            return;
        }

        repo.MarkSyncing();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        var (proxy, cred) = await GetEffectiveProxyAndCredAsync(repo, ct).ConfigureAwait(false);

        try
        {
            await SetJobStatsAsync(job, 0.10, phase: "clone", ct: ct).ConfigureAwait(false);
            // Idempotent: если уже склонирован — пропускаем
            if (!Directory.Exists(Path.Combine(repo.LocalPath.Value, ".git")))
            {
                _logger.LogInformation("Clone job {JobId} cloning {Url} -> {Path} branch {Branch}", job.Id, repo.GitUrl.Value, repo.LocalPath.Value, repo.Branch);
                await _git.CloneAsync(repo.GitUrl.Value, repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Clone job {JobId} already cloned, fetching instead", job.Id);
                await _git.FetchAsync(repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }

            await SetJobStatsAsync(job, 0.70, phase: "head", ct: ct).ConfigureAwait(false);
            var head = await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);
            repo.UpdateCurrentCommit(head);
            repo.MarkActive();
            repo.ClearError();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            _logger.LogInformation("Clone job {JobId} done head {Head}", job.Id, head);

            // После клона — отдельный Index таск
            var indexJob = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Index, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, head);
            await _jobRunner.EnqueueAsync(indexJob, ct).ConfigureAwait(false);
            _logger.LogInformation("Clone enqueued Index job {IndexJob}", indexJob.Id);

            await SetJobProgressAsync(job, 1.0, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            repo.MarkError(TruncateError(ex.Message));
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Persist a progress value on the job (in-memory + DB) so the UI reflects the current stage.</summary>
    private async Task SetJobProgressAsync(IndexingJob job, double progress, CancellationToken ct)
    {
        job.UpdateProgress(progress);
        await _jobStore.UpdateProgressAsync(job.Id, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Persist progress + детали этапа (total/done/phase/current_file) for the UI.</summary>
    private async Task SetJobStatsAsync(IndexingJob job, double progress, int? totalItems = null, int? itemsDone = null, string? phase = null, string? currentFile = null, CancellationToken ct = default)
    {
        job.SetProgress(progress, totalItems, itemsDone, phase, currentFile);
        await _jobStore.UpdateProgressAsync(job, ct).ConfigureAwait(false);
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
        // Применить directory filter ДО обнаружения файлов — исключённые директории не сканируются вообще
        diff = ApplyDirectoryFilter(repo, diff);
        var total = diff.ChangedFiles.Count;
        if (total == 0)
        {
            _logger.LogInformation("Index job {JobId}: empty diff (after filter), nothing to do", job.Id);
            await CommitIndexedAsync(repo, to, ct).ConfigureAwait(false);
            return;
        }

        // Пресчитанный тотал до старта — UI показывает "N / total files" с самого начала.
        await SetJobStatsAsync(job, 0.0, total, 0, "indexing", null, ct).ConfigureAwait(false);

        var processed = 0;
        var csharpTouched = 0;
        var lastProgressSave = DateTime.UtcNow;
        using var progressGate = new SemaphoreSlim(1, 1);
        var pendingChunks = new System.Collections.Concurrent.ConcurrentQueue<Domain.Entities.Chunk>();
        var degree = Math.Max(1, _indexerOptions.CurrentValue.ParallelFileIndexers);

        // Параллельная индексация файлов: git show + анализатор (объекты на вызов, thread-safe)
        // + SQLite WAL с busy_timeout=300s на каждое соединение.
        await Parallel.ForEachAsync(
            diff.ChangedFiles,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
            async (cf, token) =>
            {
                if (IsCSharpPath(cf.Path) || (cf.Kind == GitChangeKind.Renamed && IsCSharpPath(cf.OldPath)))
                    Interlocked.Exchange(ref csharpTouched, 1);

                if (cf.Kind == GitChangeKind.Deleted)
                {
                    await DeleteFileAsync(repo, cf.Path, token).ConfigureAwait(false);
                }
                else
                {
                    // Renamed: delete old, add new
                    if (cf.Kind == GitChangeKind.Renamed && !string.IsNullOrWhiteSpace(cf.OldPath))
                        await DeleteFileAsync(repo, cf.OldPath!, token).ConfigureAwait(false);

                    await IndexFileAsync(repo, cf.Path, to!, pendingChunks, token).ConfigureAwait(false);
                }

                var done = Interlocked.Increment(ref processed);

                // Progress (processed/total) — пишем в БД раз в N файлов / 1s (indexing.md §4).
                // SemaphoreSlim — serializes progress writes from parallel workers (lock не может содержать await).
                await progressGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var now = DateTime.UtcNow;
                    if (done % 10 == 0 || (now - lastProgressSave).TotalSeconds >= 1)
                    {
                        var p = (double)done / total;
                        job.SetProgress(p, total, done, "indexing", cf.Path);
                        await _jobStore.UpdateProgressAsync(job, token).ConfigureAwait(false);
                        lastProgressSave = now;
                    }
                }
                finally
                {
                    progressGate.Release();
                }
            });

        // Vector embedding is a SEPARATE task (isolates the slow model calls so Index progress reflects
        // parsing, not embedding). The structural chunks are now persisted, so enqueue an Embedding job that
        // re-embeds every chunk still marked NeedsEmbedding (code and text each use their own model/vector space).
        if (pendingChunks.Count > 0)
        {
            var embeddingJob = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Embedding, JobStatus.Queued, DateTimeOffset.UtcNow, to, to);
            await _jobRunner.EnqueueAsync(embeddingJob, ct).ConfigureAwait(false);
            _logger.LogInformation("Index job {JobId} enqueued Embedding job {EmbedJob} ({Count} new chunks)", job.Id, embeddingJob.Id, pendingChunks.Count);
        }

        // C# relations are rebuilt authoritatively from the whole repo once any C# file changed
        // (per-file C# symbols get fresh IDs each run, so only a full-repo extraction stays consistent).
        if (csharpTouched == 1)
        {
            await SetJobStatsAsync(job, job.Progress, total, total, "relations", null, ct).ConfigureAwait(false);
            try
            {
                await RebuildCSharpRelationsAsync(repo, to!, ct);
            }
            catch (Exception ex)
            {
                // Never fail the index run (and never leave the graph empty) if extraction misbehaves.
                _logger.LogWarning(ex, "C# relation extraction failed for repo {Repo}; keeping existing relations", repo.Id);
            }
        }

        await CommitIndexedAsync(repo, to, ct).ConfigureAwait(false);

        await SetJobStatsAsync(job, 1.0, total, total, "commit", null, ct).ConfigureAwait(false);
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

    private async Task IndexFileAsync(Domain.Entities.Repository repo, string filePath, string commitSha, System.Collections.Concurrent.ConcurrentQueue<Domain.Entities.Chunk> pendingChunks, CancellationToken ct)
    {
        var fullPath = Path.Combine(repo.LocalPath.Value, filePath);
        string content;
        // Пытаемся прочитать через git show (обходит NAME_MAX 255 и отсутствие working tree после неудачного checkout)
        var useGitShow = !string.IsNullOrWhiteSpace(commitSha);
        if (useGitShow)
        {
            try
            {
                content = await _git.ShowFileAsync(repo.LocalPath.Value, filePath, commitSha, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "git show failed for {Path}@{Commit}, fallback to filesystem", filePath, commitSha);
                // Fallback to filesystem (legacy checkout). Без checkout-а файла на диске не будет —
                // это нормальное состояние, не удаляем из индекса, а пропускаем.
                try
                {
                    if (!File.Exists(fullPath))
                    {
                        _logger.LogWarning("git show failed and file not on disk, skipping {Path}", filePath);
                        return;
                    }
                    content = ContentEncoding.Decode(await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false));
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning(ex2, "Failed to read file {Path}, skipping", filePath);
                    await LogIndexingErrorAsync(repo.Id, filePath, ex2.Message, ct).ConfigureAwait(false);
                    return;
                }
            }
        }
        else
        {
            try
            {
                if (!File.Exists(fullPath))
                {
                    // Файл на диске не ожидается (no-checkout) — пропускаем, не трогаем индекс
                    _logger.LogWarning("File not on disk (no-checkout), skipping {Path}", filePath);
                    return;
                }
                content = ContentEncoding.Decode(await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read file {Path}, skipping", filePath);
                await LogIndexingErrorAsync(repo.Id, filePath, ex.Message, ct).ConfigureAwait(false);
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogDebug("Skipping empty/whitespace file {Path}", filePath);
            var existingEmpty = await _fileStore.GetByPathAsync(repo.Id, filePath, ct).ConfigureAwait(false);
            if (existingEmpty is not null) await DeleteFileAsync(repo, filePath, ct).ConfigureAwait(false);
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
        var fileEntry = Domain.Entities.FileEntry.Create(fileId, repo.Id, filePath, null, language, hash, DateTimeOffset.UtcNow);
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
                    pendingChunks.Enqueue(ch);
                }
            }
            return;
        }

        try
        {
            var ctx = new FileContext(repo.Id, null, filePath, content, language ?? analyzer.Language, budgetMaxChars > 0 ? budgetMaxChars : _indexerOptions.CurrentValue.MaxChunkChars);
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
                        symbolId: ac.SymbolId, language: language, startLine: ac.StartLine, endLine: ac.EndLine, domain: domain);
                    chunksToStore.Add(domainChunk);
                }

                foreach (var domainChunk in chunksToStore)
                {
                    await _chunkStore.UpsertAsync(domainChunk, ct).ConfigureAwait(false);

                    // FTS upsert
                    await _fts.UpsertAsync(domainChunk.Id, repo.Id, domainChunk.Source, symName, null, filePath, null, null, ct).ConfigureAwait(false);

                    if (ShouldEmbed(domainChunk))
                    {
                        pendingChunks.Enqueue(domainChunk);
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
                    pendingChunks.Enqueue(ch);
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

        await SetJobStatsAsync(job, total == 0 ? 1.0 : 0.0, total, 0, "embedding", null, ct).ConfigureAwait(false);

        if (total == 0)
        {
            return;
        }

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
                job.SetProgress(p, total, done, "embedding", null);
                await _jobStore.UpdateProgressAsync(job, ct).ConfigureAwait(false);
            }
        }

        await SetJobStatsAsync(job, 1.0, total, total, "done", null, ct).ConfigureAwait(false);
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
        await SetJobStatsAsync(job, 0.0, phase: "clearing", ct: ct).ConfigureAwait(false);
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

        await SetJobStatsAsync(job, job.Progress, phase: "diff", ct: ct).ConfigureAwait(false);
        var head = job.ToCommit ?? await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);
        var diff = await _git.DiffAsync(repo.LocalPath.Value, null, head, ct).ConfigureAwait(false);
        // reuse core (the REAL job entity — так финальный upsert в ExecuteAsync сохранит
        // total_items/items_done/phase, а не стёртые значения fakeJob)
        await ExecuteIndexCoreAsync(job, diff, ct).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(content)) return list;
        var lines = content.Split('\n');
        var sb = new System.Text.StringBuilder();
        // Простая реализация: режем по символам, сохраняя границы строк примерно
        for (int pos = 0; pos < content.Length; pos += window - overlap)
        {
            var len = Math.Min(window, content.Length - pos);
            var slice = content.Substring(pos, len);
            if (string.IsNullOrWhiteSpace(slice)) continue;
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

    private static GitDiff ApplyDirectoryFilter(Domain.Entities.Repository repo, GitDiff diff)
    {
        if (!repo.DirectoryFilterEnabled) return diff;
        var filtered = diff.ChangedFiles.Where(f => {
            // For deletes, check Path; for renames check both? Keep if either allowed?
            // If filter enabled, only process files whose new path is allowed; deletes of non-allowed should be ignored
            if (f.Kind == GitChangeKind.Deleted) return repo.IsPathAllowed(f.Path);
            if (f.Kind == GitChangeKind.Renamed)
            {
                // Renamed: treat as delete old + add new; allow if new path allowed (old delete will be handled via IsPathAllowed on old)
                // We'll keep entry if new path allowed; old path filter checked separately via separate handling? Keep entry.
                return repo.IsPathAllowed(f.Path);
            }
            return repo.IsPathAllowed(f.Path);
        }).ToList();
        return new GitDiff(diff.FromCommit, diff.ToCommit, filtered, diff.IsFullDiff);
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

    /// <summary>Список C# файлов на заданном коммите — прямо из git-объектов (без working tree), с учётом directory filter.</summary>
    private async Task<List<SourceFile>> EnumerateCSharpFilesAsync(string repoPath, string commitSha, CancellationToken ct)
    {
        // Need repo to check filter; find repo by localPath (lookup)
        Domain.Entities.Repository? filterRepo = null;
        try
        {
            var allRepos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
            filterRepo = allRepos.FirstOrDefault(r => string.Equals(r.LocalPath.Value, repoPath, StringComparison.Ordinal));
        }
        catch { }

        var files = new List<SourceFile>();
        var all = await _git.ListFilesAsync(repoPath, commitSha, ct).ConfigureAwait(false);
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
            catch { /* skip unreadable file */ }
        }
        return files;
    }

    private async Task RebuildCSharpRelationsAsync(Domain.Entities.Repository repo, string commitSha, CancellationToken ct)
    {
        var extractor = _relationExtractors.FirstOrDefault(
            e => string.Equals(e.Language, SupportedLanguages.CSharp, StringComparison.OrdinalIgnoreCase));
        if (extractor is null)
        {
            _logger.LogInformation("No C# relation extractor registered for repo {Repo}; skipping relation rebuild", repo.Id);
            return;
        }

        var files = await EnumerateCSharpFilesAsync(repo.LocalPath.Value, commitSha, ct).ConfigureAwait(false);
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

    private async Task<(ProxyConfig? Proxy, Credential? Credential)> GetEffectiveProxyAndCredAsync(Domain.Entities.Repository repo, CancellationToken ct)
    {
        if (!repo.GroupId.HasValue) return (null, null);
        var group = await _groupStore.GetByIdAsync(repo.GroupId.Value, ct).ConfigureAwait(false);
        if (group is null) return (null, null);
        ProxyConfig? proxy = group.ProxyId.HasValue ? await _proxyStore.GetByIdAsync(group.ProxyId.Value, ct).ConfigureAwait(false) : null;
        Credential? cred = group.CredentialId.HasValue ? await _credStore.GetByIdAsync(group.CredentialId.Value, ct).ConfigureAwait(false) : null;
        return (proxy, cred);
    }


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
