using System.Collections.Concurrent;
using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Git;
using CodeRag.Git.Helpers;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer.Services.Indexing;

internal sealed class FileIndexer
{
    private readonly IFileStore _fileStore;
    private readonly IChunkStore _chunkStore;
    private readonly ISymbolStore _symbolStore;
    private readonly IRelationStore _relationStore;
    private readonly IVectorStoreFactory _vectorFactory;
    private readonly FtsHelper _fts;
    private readonly IEmbeddingProviderFactory _embeddingsFactory;
    private readonly IEnumerable<ICodeAnalyzer> _analyzers;
    private readonly IGitService _git;
    private readonly IIndexingErrorStore _errorStore;
    private readonly IOptionsMonitor<IndexerOptions> _options;
    private readonly ILogger<FileIndexer> _logger;

    public FileIndexer(
        IFileStore fileStore,
        IChunkStore chunkStore,
        ISymbolStore symbolStore,
        IRelationStore relationStore,
        IVectorStoreFactory vectorFactory,
        FtsHelper fts,
        IEmbeddingProviderFactory embeddingsFactory,
        IEnumerable<ICodeAnalyzer> analyzers,
        IGitService git,
        IIndexingErrorStore errorStore,
        IOptionsMonitor<IndexerOptions> options,
        ILogger<FileIndexer> logger)
    {
        _fileStore = fileStore;
        _chunkStore = chunkStore;
        _symbolStore = symbolStore;
        _relationStore = relationStore;
        _vectorFactory = vectorFactory;
        _fts = fts;
        _embeddingsFactory = embeddingsFactory;
        _analyzers = analyzers;
        _git = git;
        _errorStore = errorStore;
        _options = options;
        _logger = logger;
    }

    public async Task DeleteFileAsync(Repository repo, string filePath, CancellationToken ct)
    {
        var file = await _fileStore.GetByPathAsync(repo.Id, filePath, ct).ConfigureAwait(false);
        if (file is null) return;
        var chunks = await _chunkStore.GetByFileAsync(file.Id, ct).ConfigureAwait(false);
        var chunkIds = chunks.Select(c => c.Id).ToList();
        if (chunkIds.Count > 0)
        {
            await _fts.DeleteByChunkIdsAsync(chunkIds, ct).ConfigureAwait(false);
            try { await _vectorFactory.Code.DeleteByChunkIdsAsync(chunkIds, ct).ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Vector delete failed for file {Path}", filePath); }
            try { await _vectorFactory.Text.DeleteByChunkIdsAsync(chunkIds, ct).ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Vector delete failed for file {Path}", filePath); }
        }
        await _fileStore.DeleteAsync(file.Id, ct).ConfigureAwait(false);
        _logger.LogDebug("Deleted file {Path} ({Chunks} chunks)", filePath, chunkIds.Count);
    }

    public async Task IndexFileAsync(Repository repo, string filePath, string commitSha, ConcurrentQueue<Domain.Entities.Chunk> pendingChunks, CancellationToken ct)
    {
        var fullPath = Path.Combine(repo.LocalPath.Value, filePath);
        string content;
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
                    await LogErrorAsync(repo.Id, filePath, ex2.Message, ct).ConfigureAwait(false);
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
                    _logger.LogWarning("File not on disk (no-checkout), skipping {Path}", filePath);
                    return;
                }
                content = ContentEncoding.Decode(await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read file {Path}, skipping", filePath);
                await LogErrorAsync(repo.Id, filePath, ex.Message, ct).ConfigureAwait(false);
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

        var hash = IndexingHelper.ComputeHash(content);
        var existing = await _fileStore.GetByPathAsync(repo.Id, filePath, ct).ConfigureAwait(false);
        if (existing is not null && string.Equals(existing.Hash, hash, StringComparison.Ordinal))
        {
            _logger.LogDebug("File unchanged, skip {Path}", filePath);
            return;
        }

        var language = LanguageDetector.Detect(filePath, content);
        var analyzer = _analyzers.FirstOrDefault(a => a.CanAnalyze(filePath));
        var domain = ContentClassifier.Classify(filePath, language);

        if (existing is not null)
        {
            var oldChunks = await _chunkStore.GetByFileAsync(existing.Id, ct).ConfigureAwait(false);
            var oldIds = oldChunks.Select(c => c.Id).ToList();
            if (oldIds.Count > 0)
            {
                await _fts.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false);
                try { await _vectorFactory.Code.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false); } catch { }
                try { await _vectorFactory.Text.DeleteByChunkIdsAsync(oldIds, ct).ConfigureAwait(false); } catch { }
            }
            await _chunkStore.DeleteByFileAsync(existing.Id, ct).ConfigureAwait(false);
            await _symbolStore.DeleteByFileAsync(existing.Id, ct).ConfigureAwait(false);
        }

        var fileId = existing?.Id ?? Guid.NewGuid();
        var fileEntry = FileEntry.Create(fileId, repo.Id, filePath, null, language, hash, DateTimeOffset.UtcNow);
        await _fileStore.UpsertAsync(fileEntry, ct).ConfigureAwait(false);

        var (budgetMaxChars, budgetOverlapChars) = GetChunkBudget(domain);

        if (analyzer is null)
        {
            var fallbackChunks = CreateFallbackChunks(repo.Id, fileId, filePath, language, domain, content, budgetMaxChars, budgetOverlapChars);
            await PersistChunksAsync(fallbackChunks.Select(ch => (Chunk: ch, (string?)null)), repo.Id, filePath, pendingChunks, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var ctx = new FileContext(repo.Id, null, filePath, content, language ?? analyzer.Language, budgetMaxChars > 0 ? budgetMaxChars : _options.CurrentValue.MaxChunkChars);
            var result = await analyzer.AnalyzeAsync(ctx, ct).ConfigureAwait(false);

            var symbols = result.Symbols.Select(s => MapSymbol(s, repo.Id, fileId)).ToList();
            if (symbols.Count > 0)
                await _symbolStore.UpsertBatchAsync(symbols, ct).ConfigureAwait(false);

            var allChunks = new List<(Domain.Entities.Chunk Chunk, string? SymbolName)>();
            foreach (var ac in result.Chunks)
            {
                var symName = ac.SymbolId.HasValue ? result.Symbols.FirstOrDefault(s => s.Id == ac.SymbolId.Value)?.Name : null;
                var chunksToStore = new List<Domain.Entities.Chunk>();
                if (budgetMaxChars > 0 && ac.Source.Length > budgetMaxChars)
                {
                    var header = MakeCommentHeader(filePath, language, symName);
                    var sub = ChunkingHelper.SplitByCharBudget(ac.Source, ac.StartLine, ac.EndLine, budgetMaxChars, budgetOverlapChars, header);
                    foreach (var slice in sub)
                    {
                        chunksToStore.Add(Domain.Entities.Chunk.Create(Guid.NewGuid(), repo.Id, fileId, slice.Text, symbolId: ac.SymbolId, language: language, startLine: slice.StartLine, endLine: slice.EndLine, domain: domain));
                    }
                }
                if (chunksToStore.Count == 0)
                {
                    var domainChunk = Domain.Entities.Chunk.Create(Guid.NewGuid(), repo.Id, fileId, ac.Source, symbolId: ac.SymbolId, language: language, startLine: ac.StartLine, endLine: ac.EndLine, domain: domain);
                    chunksToStore.Add(domainChunk);
                }
                foreach (var domainChunk in chunksToStore)
                    allChunks.Add((domainChunk, symName));
            }
            await PersistChunksAsync(allChunks, repo.Id, filePath, pendingChunks, ct).ConfigureAwait(false);

            if (language != SupportedLanguages.CSharp && result.Relations.Count > 0)
            {
                var domainRelations = result.Relations.Select(ToDomainRelation).Where(r => r is not null)!.ToList();
                if (domainRelations.Count > 0)
                    await _relationStore.UpsertBatchAsync(repo.Id, domainRelations, ct).ConfigureAwait(false);
            }

            if (result.Errors.Count > 0)
                foreach (var e in result.Errors)
                    await LogErrorAsync(repo.Id, filePath, e.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Analyzer failed for {Path} lang={Lang}", filePath, language);
            await LogErrorAsync(repo.Id, filePath, ex.Message, ct).ConfigureAwait(false);
            var fallbackChunks = CreateFallbackChunks(repo.Id, fileId, filePath, language, domain, content, budgetMaxChars, budgetOverlapChars);
            await PersistChunksAsync(fallbackChunks.Select(ch => (Chunk: ch, (string?)null)), repo.Id, filePath, pendingChunks, ct).ConfigureAwait(false);
        }
    }

    private (int MaxChars, int OverlapChars) GetChunkBudget(EmbeddingDomain domain)
    {
        var o = _options.CurrentValue;
        var maxChars = o.MaxChunkChars;
        if (_embeddingsFactory.Get(domain).ContextLength is > 0)
        {
            var contextChars = (int)Math.Max(256, _embeddingsFactory.Get(domain).ContextLength.Value * o.CharsPerToken);
            maxChars = Math.Min(maxChars, contextChars);
        }
        maxChars = Math.Max(256, maxChars);
        var overlapChars = (int)(maxChars * o.ChunkOverlapFraction);
        return (maxChars, overlapChars);
    }

    private bool IsBinaryPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext is not { Length: > 1 }) return false;
        return _options.CurrentValue.BinaryExtensions.Contains(ext[1..]);
    }

    private static bool LooksBinary(string content)
    {
        var limit = Math.Min(content.Length, 8192);
        for (var i = 0; i < limit; i++)
            if (content[i] == '\0') return true;
        return false;
    }

    private bool ShouldEmbed(Domain.Entities.Chunk ch)
    {
        var provider = _embeddingsFactory.Get(ch.EmbeddingDomain);
        if (!ch.NeedsEmbedding(provider.Model, provider.ConfigHash)) return false;
        var minChars = _options.CurrentValue.MinEmbeddingChars;
        return ch.Source.Length >= minChars;
    }

    /// <summary>
    /// Батч-запись чанков файла: chunks + FTS в одной транзакции на каждый
    /// (вместо отдельного соединения на запись). Затем очередь на эмбеддинг.
    /// </summary>
    private async Task PersistChunksAsync(
        IEnumerable<(Domain.Entities.Chunk Chunk, string? SymbolName)> items,
        Guid repoId, string filePath,
        ConcurrentQueue<Domain.Entities.Chunk> pendingChunks,
        CancellationToken ct)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var chunks = list.Select(i => i.Chunk).ToList();
        await _chunkStore.UpsertBatchAsync(chunks, ct).ConfigureAwait(false);
        var ftsRows = list.Select(i => new FtsUpsertRow(i.Chunk.Id, repoId, i.Chunk.Source, i.SymbolName, null, filePath, null, null));
        await _fts.UpsertBatchAsync(ftsRows, ct).ConfigureAwait(false);
        foreach (var ch in chunks)
            if (ShouldEmbed(ch)) pendingChunks.Enqueue(ch);
    }

    private static string MakeCommentHeader(string filePath, string? language, string? symbolName)
    {
        var comment = (language ?? string.Empty).ToLowerInvariant() switch
        {
            SupportedLanguages.Python => "#",
            SupportedLanguages.Sql => "--",
            SupportedLanguages.Vbs => "'",
            _ => "//"
        };
        var sym = string.IsNullOrWhiteSpace(symbolName) ? string.Empty : $" :: {symbolName}";
        return $"{comment} [continued] {filePath}{sym}";
    }

    private static IReadOnlyList<Domain.Entities.Chunk> CreateFallbackChunks(Guid repoId, Guid fileId, string filePath, string? language, EmbeddingDomain domain, string content, int window = 3200, int overlap = 320)
    {
        window = Math.Max(256, window);
        overlap = Math.Clamp(overlap, 0, window - 1);
        var list = new List<Domain.Entities.Chunk>();
        if (string.IsNullOrWhiteSpace(content)) return list;
        for (int pos = 0; pos < content.Length; pos += window - overlap)
        {
            var len = Math.Min(window, content.Length - pos);
            var slice = content.Substring(pos, len);
            if (string.IsNullOrWhiteSpace(slice)) continue;
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
        return Domain.Entities.Symbol.Create(s.Id == Guid.Empty ? Guid.NewGuid() : s.Id, repoId, fileId, s.Language ?? "unknown", kind, s.Name ?? "unknown", fqn: s.Fqn, @namespace: s.Namespace, parentSymbolId: s.ParentSymbolId, startLine: s.StartLine, endLine: s.EndLine, startCol: s.StartColumn, endCol: s.EndColumn, signature: s.Signature);
    }

    private static Domain.Entities.Relation? ToDomainRelation(CodeRag.Analyzers.Abstractions.Relation r)
    {
        if (r.FromSymbolId == Guid.Empty || r.ToSymbolId == Guid.Empty) return null;
        if (r.FromSymbolId == r.ToSymbolId) return null;
        if (!Enum.TryParse<Domain.Enums.RelationType>(r.RelationType, true, out var t)) return null;
        return new Domain.Entities.Relation(r.FromSymbolId, r.ToSymbolId, t);
    }

    private Task LogErrorAsync(Guid repoId, string filePath, string message, CancellationToken ct)
    {
        try { return _errorStore.AddAsync(repoId, filePath, IndexingHelper.TruncateError(message), ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist indexing error for {Path}", filePath); return Task.CompletedTask; }
    }
}
