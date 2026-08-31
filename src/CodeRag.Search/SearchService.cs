using System.Diagnostics;
using System.Text;
using CodeRag.Domain.Enums;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Search.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Search;

public sealed class SearchService : ISearchService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly FtsHelper _fts;
    private readonly IVectorStoreFactory _vectorStoreFactory;
    private readonly IEmbeddingProviderFactory _embeddingFactory;
    private readonly ILogger<SearchService> _logger;
    private readonly IOptionsMonitor<SearchOptions> _optionsMonitor;
    private SearchOptions Opts => _optionsMonitor.CurrentValue;

    public SearchService(
        SqliteConnectionFactory factory,
        FtsHelper fts,
        IVectorStoreFactory vectorStoreFactory,
        IEmbeddingProviderFactory embeddingFactory,
        ILogger<SearchService> logger,
        IOptionsMonitor<SearchOptions> options)
    {
        _factory = factory;
        _fts = fts;
        _vectorStoreFactory = vectorStoreFactory;
        _embeddingFactory = embeddingFactory;
        _logger = logger;
        _optionsMonitor = options;
    }

    public async Task<SearchResult> SearchCodeAsync(SearchRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var query = req.Query?.Trim() ?? string.Empty;
        var warnings = new List<string>();
        var limit = req.Limit <= 0 ? Opts.DefaultResultCount : Math.Min(req.Limit, Opts.MaxResultCount);
        var offset = Math.Max(0, req.Offset);
        var mode = req.Mode;

        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogInformation("Search query empty mode={Mode} limit={Limit} offset={Offset}", mode, limit, offset);
            return new SearchResult(Array.Empty<SearchResultItem>(), 0, Array.Empty<string>()) { ElapsedMs = sw.ElapsedMilliseconds };
        }

        var repoFilter = req.RepositoryIds?.Length > 0 ? req.RepositoryIds : null;
        var langFilter = req.Languages?.Length > 0 ? req.Languages.Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToArray() : null;
        var symbolKindFilter = string.IsNullOrWhiteSpace(req.SymbolKind) ? null : req.SymbolKind.Trim();

        // Candidate accumulator dedup by chunkId:6306daf-xxx
        var candidates = new Dictionary<Guid, CandidateAccumulator>();

        int exactCount = 0, ftsCount = 0, vectorCount = 0, structCount = 0;
        List<Guid> exactSymbolIds = new();

        // Determine which branches to run
        bool runExact = mode == SearchMode.Hybrid || mode == SearchMode.Symbol;
        bool runFts = mode == SearchMode.Hybrid || mode == SearchMode.FullText;
        bool runVector = mode == SearchMode.Hybrid || mode == SearchMode.Semantic;
        bool runStructural = mode == SearchMode.Hybrid;

        // Exact / Symbol branch
        if (runExact)
        {
            try
            {
                var (chunkIds, symbolIds) = await GetExactCandidatesAsync(query, repoFilter, langFilter, symbolKindFilter, ct).ConfigureAwait(false);
                exactSymbolIds = symbolIds;
                foreach (var cid in chunkIds)
                {
                    if (!candidates.TryGetValue(cid, out var acc))
                    {
                        acc = new CandidateAccumulator(cid);
                        candidates[cid] = acc;
                    }
                    acc.Origins.Add("exact");
                    acc.HasExact = true;
                    acc.ExactBonus = Opts.ExactBonus;
                }
                exactCount = chunkIds.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exact branch failed query={Query}", query);
            }
        }

        // FTS branch
        if (runFts)
        {
            try
            {
                var ftsResults = await GetFtsCandidatesAsync(query, repoFilter, limit, ct).ConfigureAwait(false);
                foreach (var r in ftsResults)
                {
                    if (!candidates.TryGetValue(r.ChunkId, out var acc))
                    {
                        acc = new CandidateAccumulator(r.ChunkId);
                        candidates[r.ChunkId] = acc;
                    }
                    acc.Origins.Add("fts");
                    // FTS rank stored; lower (more negative) is better, keep raw
                    if (!acc.FtsRank.HasValue || r.Rank < acc.FtsRank.Value)
                        acc.FtsRank = r.Rank;
                }
                ftsCount = ftsResults.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FTS branch failed query={Query}", query);
            }
        }

        // Vector branch
        if (runVector)
        {
            var vecResults = await GetVectorCandidatesAsync(query, repoFilter, warnings, ct).ConfigureAwait(false);
            // Drop low-confidence vector matches so weak/ambiguous queries return no results instead of
            // "least-irrelevant" random code (see SearchOptions.MinSemanticScore).
            var relevantVec = vecResults.Where(r => r.Score >= Opts.MinSemanticScore).ToList();
            foreach (var r in relevantVec)
            {
                if (!candidates.TryGetValue(r.ChunkId, out var acc))
                {
                    acc = new CandidateAccumulator(r.ChunkId);
                    candidates[r.ChunkId] = acc;
                }
                acc.Origins.Add("vector");
                // Keep best score (higher better)
                if (!acc.VectorScore.HasValue || r.Score > acc.VectorScore.Value)
                {
                    acc.VectorScore = r.Score;
                    acc.VectorDistance = r.Distance;
                }
            }
            vectorCount = relevantVec.Count;
        }

        // Structural branch (only for Hybrid)
        if (runStructural)
        {
            try
            {
                var structChunkIds = await GetStructuralCandidatesAsync(query, exactSymbolIds, repoFilter, langFilter, ct).ConfigureAwait(false);
                foreach (var cid in structChunkIds)
                {
                    if (!candidates.TryGetValue(cid, out var acc))
                    {
                        acc = new CandidateAccumulator(cid);
                        candidates[cid] = acc;
                    }
                    if (acc.Origins.Add("structural"))
                    {
                        acc.HasStructural = true;
                    }
                    else
                    {
                        acc.HasStructural = true;
                    }
                }
                structCount = structChunkIds.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Structural branch failed query={Query}", query);
            }
        }

        if (candidates.Count == 0)
        {
            sw.Stop();
            _logger.LogInformation(
                "Search query={Query} mode={Mode} latency={Latency}ms candidates=0 exact={Exact} fts={Fts} vector={Vec} struct={Struct} total=0 weights lex={Lex} sem={Sem} struct={StructW} warnings={Warnings}",
                query, mode, sw.ElapsedMilliseconds, exactCount, ftsCount, vectorCount, structCount,
                Opts.LexicalWeight, Opts.SemanticWeight, Opts.StructuralWeight,
                warnings.Count == 0 ? "none" : string.Join(",", warnings));
            return new SearchResult(Array.Empty<SearchResultItem>(), 0, warnings) { ElapsedMs = sw.ElapsedMilliseconds };
        }

        // Load chunk metadata for ranking/filter
        var chunkInfos = await LoadChunkInfosAsync(candidates.Keys.ToList(), ct).ConfigureAwait(false);

        // Post-filter by languages / symbolKind / repositoryIds if not already filtered via branch
        var filtered = new List<(CandidateAccumulator acc, ChunkInfo info)>();
        foreach (var kv in candidates)
        {
            if (!chunkInfos.TryGetValue(kv.Key, out var info))
                continue;

            if (repoFilter != null && repoFilter.Length > 0 && !repoFilter.Contains(info.RepositoryId))
                continue;

            if (langFilter != null && langFilter.Length > 0)
            {
                var lang = info.Language ?? info.FileLanguage ?? string.Empty;
                if (!langFilter.Any(l => string.Equals(l, lang, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            if (symbolKindFilter != null)
            {
                var kind = info.SymbolKind ?? string.Empty;
                if (!string.Equals(kind, symbolKindFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            filtered.Add((kv.Value, info));
        }

        if (filtered.Count == 0)
        {
            sw.Stop();
            _logger.LogInformation(
                "Search query={Query} mode={Mode} latency={Latency}ms candidates={Cand} filtered=0 weights lex={Lex} sem={Sem} struct={StructW}",
                query, mode, sw.ElapsedMilliseconds, candidates.Count,
                Opts.LexicalWeight, Opts.SemanticWeight, Opts.StructuralWeight);
            return new SearchResult(Array.Empty<SearchResultItem>(), 0, warnings) { ElapsedMs = sw.ElapsedMilliseconds };
        }

        // Compute normalization bounds
        var ftsRanks = filtered.Where(x => x.acc.FtsRank.HasValue).Select(x => x.acc.FtsRank!.Value).ToList();
        double ftsMin = ftsRanks.Count > 0 ? ftsRanks.Min() : 0;
        double ftsMax = ftsRanks.Count > 0 ? ftsRanks.Max() : 0;
        bool ftsHasRange = ftsRanks.Count > 0 && Math.Abs(ftsMax - ftsMin) > 1e-9;

        var vecScores = filtered.Where(x => x.acc.VectorScore.HasValue).Select(x => x.acc.VectorScore!.Value).ToList();
        double vecMin = vecScores.Count > 0 ? vecScores.Min() : 0;
        double vecMax = vecScores.Count > 0 ? vecScores.Max() : 0;
        bool vecHasRange = vecScores.Count > 0 && Math.Abs(vecMax - vecMin) > 1e-9;

        // Compute final scores
        var scored = new List<(CandidateAccumulator acc, ChunkInfo info, double finalScore)>(filtered.Count);
        foreach (var (acc, info) in filtered)
        {
            // Semantic-only mode: rank by the merged semantic score (0..1). This is the per-domain
            // min-max-normalized cosine similarity from the code/text vector spaces (see GetVectorCandidatesAsync).
            if (mode == SearchMode.Semantic)
            {
                var semScore = Math.Clamp(acc.VectorScore ?? 0.0, 0.0, 1.0);
                scored.Add((acc, info, semScore));
                continue;
            }

            double normFts = 0, normVec = 0;
            if (acc.FtsRank.HasValue)
            {
                if (!ftsHasRange) normFts = 1.0;
                else normFts = (ftsMax - acc.FtsRank.Value) / (ftsMax - ftsMin);
            }
            if (acc.VectorScore.HasValue)
            {
                if (!vecHasRange) normVec = 1.0;
                else normVec = (acc.VectorScore.Value - vecMin) / (vecMax - vecMin);
            }
            double structuralBoost = acc.HasStructural ? 1.0 : 0.0;
            double exactBonus = acc.HasExact ? Opts.ExactBonus : 0.0;

            double finalScore = Opts.LexicalWeight * normFts
                              + Opts.SemanticWeight * normVec
                              + Opts.StructuralWeight * structuralBoost
                              + exactBonus;
            scored.Add((acc, info, finalScore));
        }

        // Deterministic tie-break: repository, language, path, chunkId
        var ranked = scored
            .OrderByDescending(x => x.finalScore)
            .ThenBy(x => x.info.RepositoryId)
            .ThenBy(x => x.info.Language ?? x.info.FileLanguage ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.info.FilePath, StringComparer.Ordinal)
            .ThenBy(x => x.acc.ChunkId)
            .ToList();

        var total = ranked.Count;
        var paged = ranked.Skip(offset).Take(limit).ToList();

        var items = new List<SearchResultItem>(paged.Count);
        foreach (var (acc, info, score) in paged)
        {
            var preview = BuildPreview(info.Source, Opts.PreviewMaxLines);
            var origin = string.Join(",", acc.Origins.OrderBy(o => o, StringComparer.Ordinal));
            var startLine = info.StartLine ?? 1;
            var endLine = info.EndLine ?? startLine;
            // Ensure preview limited; if source empty preview maybe empty
            items.Add(new SearchResultItem(
                RepositoryId: info.RepositoryId,
                FileId: info.FileId,
                SymbolId: info.SymbolId,
                Language: info.Language ?? info.FileLanguage ?? info.SymbolLanguage ?? "unknown",
                FilePath: info.FilePath,
                StartLine: startLine,
                EndLine: endLine,
                Score: score,
                Preview: preview,
                Origin: origin)
            {
                ChunkId = acc.ChunkId,
                SymbolName = info.SymbolName,
                Fqn = info.Fqn,
            });
        }

        sw.Stop();
        _logger.LogInformation(
            "Search query={Query} mode={Mode} latency={Latency}ms candidates={Cand} total={Total} returned={Returned} exact={Exact} fts={Fts} vector={Vec} struct={Struct} weights lex={Lex} sem={Sem} structW={StructW} warnings={Warnings}",
            query, mode, sw.ElapsedMilliseconds, candidates.Count, total, items.Count,
            exactCount, ftsCount, vectorCount, structCount,
            Opts.LexicalWeight, Opts.SemanticWeight, Opts.StructuralWeight,
            warnings.Count == 0 ? "none" : string.Join(",", warnings));

        return new SearchResult(items, total, warnings) { ElapsedMs = sw.ElapsedMilliseconds };
    }

    public async Task<IReadOnlyList<Symbol>> FindSymbolAsync(FindSymbolRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
            return Array.Empty<Symbol>();

        var limit = req.Limit <= 0 ? 20 : Math.Min(req.Limit, 100);
        var offset = Math.Max(0, req.Offset);
        var q = req.Query.Trim();

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var sql = new StringBuilder("SELECT s.*, f.path as file_path FROM symbols s JOIN files f ON f.id = s.file_id WHERE (s.name LIKE @like COLLATE NOCASE OR s.fqn LIKE @like COLLATE NOCASE OR s.namespace LIKE @like COLLATE NOCASE) ");
        var p = new DynamicParameters();
        p.Add("like", $"%{EscapeLike(q)}%");
        p.Add("limit", limit);
        p.Add("offset", offset);

        if (req.RepositoryId.HasValue)
        {
            sql.Append(" AND s.repository_id = @repo ");
            p.Add("repo", req.RepositoryId.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(req.Language))
        {
            sql.Append(" AND s.language = @lang ");
            p.Add("lang", req.Language.Trim());
        }
        if (!string.IsNullOrWhiteSpace(req.SymbolKind))
        {
            sql.Append(" AND s.kind = @kind ");
            p.Add("kind", req.SymbolKind.Trim());
        }

        sql.Append(" ORDER BY s.fqn, s.name LIMIT @limit OFFSET @offset;");

        var rows = await conn.QueryAsync<SymbolRow>(sql.ToString(), p).ConfigureAwait(false);
        return rows.Select(MapSymbolRow).ToList();
    }

    public async Task<IReadOnlyList<Reference>> FindReferencesAsync(Guid symbolId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RelationRow>(
            "SELECT from_symbol_id, to_symbol_id, relation_type FROM relations WHERE to_symbol_id = @id AND relation_type = @type;",
            new { id = symbolId.ToString(), type = "References" }).ConfigureAwait(false);
        return rows.Select(r => new Reference(Guid.Parse(r.from_symbol_id), Guid.Parse(r.to_symbol_id), r.relation_type)).ToList();
    }

    public async Task<IReadOnlyList<RelationEntry>> GetRelationsAsync(
        Guid symbolId,
        CodeRag.Domain.Enums.RelationDirection direction = CodeRag.Domain.Enums.RelationDirection.Both,
        string[]? types = null,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var cond = direction switch
        {
            CodeRag.Domain.Enums.RelationDirection.Outgoing => "r.from_symbol_id = @id",
            CodeRag.Domain.Enums.RelationDirection.Incoming => "r.to_symbol_id = @id",
            _ => "(r.from_symbol_id = @id OR r.to_symbol_id = @id)",
        };
        var sql = $"SELECT r.from_symbol_id, r.to_symbol_id, r.relation_type FROM relations r WHERE {cond};";
        var p = new Dapper.DynamicParameters();
        p.Add("id", symbolId.ToString());
        if (types is { Length: > 0 })
        {
            sql += " AND r.relation_type IN (" + string.Join(",", types.Select((_, i) => $"@t{i}")) + ")";
            for (int i = 0; i < types.Length; i++) p.Add($"t{i}", types[i]);
        }

        var rows = (await conn.QueryAsync<RelationRow>(sql, p).ConfigureAwait(false)).ToList();
        if (rows.Count == 0) return Array.Empty<RelationEntry>();

        // Hydrate both endpoints in a single batched query.
        var allIds = rows
            .SelectMany(r => new[] { r.from_symbol_id, r.to_symbol_id })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var symbols = (await LoadSymbolsByIdsAsync(allIds, ct).ConfigureAwait(false))
            .ToDictionary(s => s.Id);

        return rows
            .Select(r => new RelationEntry(
                Guid.Parse(r.from_symbol_id),
                Guid.Parse(r.to_symbol_id),
                r.relation_type,
                symbols.TryGetValue(Guid.Parse(r.from_symbol_id), out var f) ? f : null,
                symbols.TryGetValue(Guid.Parse(r.to_symbol_id), out var t) ? t : null))
            .ToList();
    }

    public async Task<IReadOnlyList<Symbol>> FindImplementationsAsync(Guid symbolId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RelationRow>(
            "SELECT from_symbol_id, to_symbol_id, relation_type FROM relations WHERE to_symbol_id = @id AND relation_type = @type;",
            new { id = symbolId.ToString(), type = "Implements" }).ConfigureAwait(false);
        if (!rows.Any()) return Array.Empty<Symbol>();
        var ids = rows.Select(r => r.from_symbol_id).Distinct().ToArray();
        return await LoadSymbolsByIdsAsync(ids, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Symbol>> FindCallersAsync(Guid symbolId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RelationRow>(
            "SELECT from_symbol_id, to_symbol_id, relation_type FROM relations WHERE to_symbol_id = @id AND relation_type = @type;",
            new { id = symbolId.ToString(), type = "Calls" }).ConfigureAwait(false);
        if (!rows.Any()) return Array.Empty<Symbol>();
        var ids = rows.Select(r => r.from_symbol_id).Distinct().ToArray();
        return await LoadSymbolsByIdsAsync(ids, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Symbol>> FindCalleesAsync(Guid symbolId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RelationRow>(
            "SELECT from_symbol_id, to_symbol_id, relation_type FROM relations WHERE from_symbol_id = @id AND relation_type = @type;",
            new { id = symbolId.ToString(), type = "Calls" }).ConfigureAwait(false);
        if (!rows.Any()) return Array.Empty<Symbol>();
        var ids = rows.Select(r => r.to_symbol_id).Distinct().ToArray();
        return await LoadSymbolsByIdsAsync(ids, ct).ConfigureAwait(false);
    }

    public async Task<SourceSlice> GetCodeAsync(GetCodeRequest req, CancellationToken ct = default)
    {
        if (req.RepositoryId == Guid.Empty) throw new ArgumentException("RepositoryId must not be empty.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Path)) throw new ArgumentException("Path must not be empty.", nameof(req));

        var requestedPath = req.Path.Trim();
        // Path traversal check: no absolute path that escapes, no ".."
        if (requestedPath.Contains('\0')) throw new ArgumentException("Path contains invalid characters.", nameof(req));
        // Normalize slashes
        var normalizedReq = requestedPath.Replace('\\', '/').TrimStart('/');
        if (normalizedReq.Contains("..")) throw new ArgumentException("Path traversal not allowed: '..' in path.", nameof(req));
        if (Path.IsPathRooted(requestedPath) && !requestedPath.StartsWith("/data/repos", StringComparison.Ordinal))
        {
            // Disallow absolute outside /data/repos
            throw new ArgumentException("Absolute path not allowed outside /data/repos.", nameof(req));
        }

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        // Load repository to get local_path
        var repoRow = await conn.QuerySingleOrDefaultAsync<RepoRow>(
            "SELECT id, local_path FROM repositories WHERE id = @id;",
            new { id = req.RepositoryId.ToString() }).ConfigureAwait(false);
        if (repoRow is null) throw new InvalidOperationException($"Repository not found: {req.RepositoryId}");

        var localPath = repoRow.local_path;
        if (string.IsNullOrWhiteSpace(localPath)) throw new InvalidOperationException("Repository local_path is empty.");
        // Ensure repo path inside /data/repos (Domain LocalPath already ensures, but double-check)
        var normalizedLocal = Path.GetFullPath(localPath);
        var reposRoot = Path.GetFullPath("/data/repos");
        if (!normalizedLocal.StartsWith(reposRoot, StringComparison.Ordinal))
            throw new InvalidOperationException($"Repository local_path must be inside {reposRoot}: '{localPath}'");

        // Load file entry
        var fileRow = await conn.QuerySingleOrDefaultAsync<FileRow>(
            "SELECT id, repository_id, path, language FROM files WHERE repository_id = @repo AND path = @path;",
            new { repo = req.RepositoryId.ToString(), path = normalizedReq }).ConfigureAwait(false);

        // Also try with original path if not found (case where stored path has leading ./ etc)
        if (fileRow is null)
        {
            fileRow = await conn.QuerySingleOrDefaultAsync<FileRow>(
                "SELECT id, repository_id, path, language FROM files WHERE repository_id = @repo AND path = @path2;",
                new { repo = req.RepositoryId.ToString(), path2 = requestedPath }).ConfigureAwait(false);
        }

        if (fileRow is null)
            throw new InvalidOperationException($"File not indexed: repository={req.RepositoryId} path='{normalizedReq}'");

        var fileId = Guid.Parse(fileRow.id);
        var language = fileRow.language ?? "unknown";

        // Build absolute path and verify traversal
        var combined = Path.Combine(normalizedLocal, fileRow.path.Replace('/', Path.DirectorySeparatorChar));
        var fullFilePath = Path.GetFullPath(combined);
        if (!fullFilePath.StartsWith(normalizedLocal, StringComparison.Ordinal))
            throw new InvalidOperationException("Path traversal detected: resolved path outside repository.");

        string[] lines;
        if (File.Exists(fullFilePath))
        {
            var text = await File.ReadAllTextAsync(fullFilePath, ct).ConfigureAwait(false);
            lines = text.Split('\n');
        }
        else
        {
            // Fallback: reconstruct from chunks source (if file not on disk but indexed)
            var chunkRows = await conn.QueryAsync<ChunkSourceRow>(
                "SELECT source, start_line, end_line FROM chunks WHERE file_id = @fid ORDER BY start_line;",
                new { fid = fileId.ToString() }).ConfigureAwait(false);
            if (!chunkRows.Any())
                throw new InvalidOperationException($"File not found on disk and no chunks available: {fullFilePath}");

            // Approximate file reconstruction: join chunk sources
            var sb = new StringBuilder();
            foreach (var cr in chunkRows)
            {
                sb.AppendLine(cr.source);
            }
            lines = sb.ToString().Split('\n');
        }

        // Trim trailing \r from each line
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith("\r")) lines[i] = lines[i][..^1];

        int totalLines = lines.Length;
        int start = req.StartLine.HasValue ? Math.Clamp(req.StartLine.Value, 1, totalLines) : 1;
        int end = req.EndLine.HasValue ? Math.Clamp(req.EndLine.Value, start, totalLines) : totalLines;

        // Limit preview 20 lines
        int maxLines = Opts.PreviewMaxLines;
        if (end - start + 1 > maxLines)
            end = start + maxLines - 1;

        var sliceLines = lines.Skip(start - 1).Take(end - start + 1).ToArray();
        var content = string.Join("\n", sliceLines);
        var preview = content; // preview same as content but limited to 20 lines already

        return new SourceSlice(
            RepositoryId: req.RepositoryId,
            FileId: fileId,
            Path: fileRow.path,
            Language: language,
            StartLine: start,
            EndLine: end,
            TotalLines: totalLines,
            Content: content,
            Preview: preview);
    }

    // ---------- Private helpers ----------

    private async Task<(List<Guid> chunkIds, List<Guid> symbolIds)> GetExactCandidatesAsync(
        string query, Guid[]? repoIds, string[]? languages, string? symbolKind, CancellationToken ct)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var sql = new StringBuilder("SELECT id FROM symbols WHERE (fqn = @q OR name = @q) ");
        var p = new DynamicParameters();
        p.Add("q", query);
        if (repoIds != null && repoIds.Length > 0)
        {
            var ids = repoIds.Select(g => g.ToString()).ToArray();
            sql.Append($" AND repository_id IN ({string.Join(",", ids.Select((_, i) => $"@r{i}"))}) ");
            for (int i = 0; i < ids.Length; i++) p.Add($"r{i}", ids[i]);
        }
        if (languages != null && languages.Length > 0)
        {
            // languages filter on symbols.language
            sql.Append($" AND language IN ({string.Join(",", languages.Select((_, i) => $"@l{i}"))}) ");
            for (int i = 0; i < languages.Length; i++) p.Add($"l{i}", languages[i]);
        }
        if (!string.IsNullOrWhiteSpace(symbolKind))
        {
            sql.Append(" AND kind = @kind ");
            p.Add("kind", symbolKind);
        }

        var rows = await conn.QueryAsync<IdRow>(sql.ToString(), p).ConfigureAwait(false);
        var symbolIds = rows.Select(r => Guid.Parse(r.id)).ToList();
        if (symbolIds.Count == 0) return (new List<Guid>(), new List<Guid>());

        // Get chunks for those symbols
        var chunkIds = await GetChunkIdsForSymbolsAsync(symbolIds, ct).ConfigureAwait(false);
        // Also include chunks where symbol_id is those ids? Already done. Could also include file-level chunks but minimal.

        return (chunkIds, symbolIds);
    }

    private async Task<List<Guid>> GetChunkIdsForSymbolsAsync(List<Guid> symbolIds, CancellationToken ct)
    {
        if (symbolIds.Count == 0) return new List<Guid>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ids = symbolIds.Select(g => g.ToString()).ToArray();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@s{i}"));
        var sql = $"SELECT id FROM chunks WHERE symbol_id IN ({placeholders});";
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"s{i}", ids[i]);
        var rows = await conn.QueryAsync<IdRow>(sql, p).ConfigureAwait(false);
        return rows.Select(r => Guid.Parse(r.id)).ToList();
    }

    private async Task<IReadOnlyList<FtsResult>> GetFtsCandidatesAsync(string query, Guid[]? repoIds, int limit, CancellationToken ct)
    {
        // For FTS, we need to handle trigram tokenization: query may need quoting
        // Use FtsHelper.SearchAsync which already handles escaping
        int fetchLimit = Math.Max(limit * 3, 50);
        if (repoIds != null && repoIds.Length == 1)
        {
            return await _fts.SearchAsync(query, fetchLimit, repoIds[0], ct).ConfigureAwait(false);
        }
        else if (repoIds != null && repoIds.Length > 1)
        {
            // Fetch per repo and merge? Simpler fetch without filter then filter
            var all = await _fts.SearchAsync(query, fetchLimit * 2, null, ct).ConfigureAwait(false);
            var set = new HashSet<Guid>(repoIds);
            return all.Where(r => set.Contains(r.RepositoryId)).Take(fetchLimit).ToList();
        }
        else
        {
            return await _fts.SearchAsync(query, fetchLimit, null, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<ScoredChunk>> GetVectorCandidatesAsync(string query, Guid[]? repoIds, List<string> warnings, CancellationToken ct)
    {
        // ДВА независимых семантических пространства: code и text. Запрос эмбеддится
        // каждой моделью отдельно, ищем в своём store, нормализуем распределение скор
        // каждого домена в [0,1] и сливаем в один семантический сигнал.
        var merged = new List<ScoredChunk>();
        var filter = (repoIds != null && repoIds.Length > 0) ? new VectorSearchFilter(repoIds) : VectorSearchFilter.Empty;
        var anySucceeded = false;

        foreach (var domain in new[] { EmbeddingDomain.Code, EmbeddingDomain.Text })
        {
            IEmbeddingProvider provider;
            IVectorStore store;
            try
            {
                provider = _embeddingFactory.Get(domain);
                store = _vectorStoreFactory.Get(domain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector branch skipped for {Domain}: factory unavailable query={Query}", domain.ToText(), query);
                continue;
            }

            float[] queryVector;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                var vectors = await provider.EmbedAsync(new[] { query }, timeoutCts.Token).ConfigureAwait(false);
                if (vectors.Length == 0 || vectors[0] is null || vectors[0].Length == 0)
                    throw new InvalidOperationException("Empty embedding result");
                queryVector = vectors[0];
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Vector branch timeout for {Domain} query={Query}", domain.ToText(), query);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector branch embeddings failed for {Domain} query={Query}", domain.ToText(), query);
                continue;
            }

            IReadOnlyList<ScoredChunk> results;
            try
            {
                results = await store.SearchAsync(queryVector, Opts.VectorTopK, filter, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector store search failed for {Domain} query={Query}", domain.ToText(), query);
                continue;
            }

            if (results.Count == 0) continue;
            anySucceeded = true;

            // Мин-макс нормализация ВРЯДУ с распределением скор этого домена, чтобы
            // две разные модели давали сопоставимые значения до слияния.
            var scores = results.Select(r => r.Score).ToArray();
            var min = scores.Min();
            var max = scores.Max();
            var range = max - min;
            foreach (var r in results)
            {
                var norm = range <= 1e-9 ? 1.0 : (r.Score - min) / range;
                merged.Add(new ScoredChunk(r.ChunkId, (double)Math.Clamp(norm, 0.0, 1.0), r.Distance));
            }
        }

        if (!anySucceeded)
        {
            warnings.Add("embeddings unavailable");
            _logger.LogWarning("Vector branch skipped: no domain produced candidates (embeddings unavailable) query={Query}", query);
        }

        return merged;
    }

    private async Task<List<Guid>> GetStructuralCandidatesAsync(string query, List<Guid> exactSymbolIds, Guid[]? repoIds, string[]? languages, CancellationToken ct)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var relatedChunkIds = new HashSet<Guid>();

        // If we have exact symbols, expand via relations (Implements, Inherits, References, Calls etc) one hop
        if (exactSymbolIds.Count > 0)
        {
            var ids = exactSymbolIds.Select(g => g.ToString()).ToArray();
            var placeholders = string.Join(",", ids.Select((_, i) => $"@e{i}"));
            var p = new DynamicParameters();
            for (int i = 0; i < ids.Length; i++) p.Add($"e{i}", ids[i]);

            // Relations where from = exact or to = exact (any type) — but limit to reliable types
            var sql = $"SELECT from_symbol_id, to_symbol_id FROM relations WHERE from_symbol_id IN ({placeholders}) OR to_symbol_id IN ({placeholders})";
            // Re-add params for second set (same ids)
            // Need duplicate param names? We'll just create second set with different names
            var placeholders2 = string.Join(",", ids.Select((_, i) => $"@e2{i}"));
            sql = $"SELECT from_symbol_id, to_symbol_id FROM relations WHERE from_symbol_id IN ({placeholders}) OR to_symbol_id IN ({placeholders2})";
            for (int i = 0; i < ids.Length; i++) p.Add($"e2{i}", ids[i]);

            var rels = await conn.QueryAsync<RelationPairRow>(sql, p).ConfigureAwait(false);
            var relatedSymbolIds = new HashSet<Guid>(exactSymbolIds);
            foreach (var r in rels)
            {
                if (Guid.TryParse(r.from_symbol_id, out var f) && !exactSymbolIds.Contains(f)) relatedSymbolIds.Add(f);
                if (Guid.TryParse(r.to_symbol_id, out var t) && !exactSymbolIds.Contains(t)) relatedSymbolIds.Add(t);
            }

            // Exclude original exact ids for structural distinction? Keep them but they already counted
            var relatedOnly = relatedSymbolIds.Where(g => !exactSymbolIds.Contains(g)).ToList();
            if (relatedOnly.Count > 0)
            {
                var chunkIds = await GetChunkIdsForSymbolsAsync(relatedOnly, ct).ConfigureAwait(false);
                foreach (var cid in chunkIds) relatedChunkIds.Add(cid);
            }
        }

        // Additionally, LIKE search for symbols as structural (approx symbol search)
        // This provides structural candidates even when exact missed, using reliable LIKE
        try
        {
            var likeParam = $"%{EscapeLike(query)}%";
            var sqlLike = new StringBuilder("SELECT id FROM symbols WHERE (name LIKE @like COLLATE NOCASE OR fqn LIKE @like COLLATE NOCASE) ");
            var pLike = new DynamicParameters();
            pLike.Add("like", likeParam);
            pLike.Add("limit", 20);
            if (repoIds != null && repoIds.Length > 0)
            {
                var ids = repoIds.Select(g => g.ToString()).ToArray();
                sqlLike.Append($" AND repository_id IN ({string.Join(",", ids.Select((_, i) => $"@r{i}"))}) ");
                for (int i = 0; i < ids.Length; i++) pLike.Add($"r{i}", ids[i]);
            }
            sqlLike.Append(" LIMIT @limit;");
            var likeRows = await conn.QueryAsync<IdRow>(sqlLike.ToString(), pLike).ConfigureAwait(false);
            var likeSymbolIds = likeRows.Select(r => Guid.Parse(r.id)).ToList();
            // Exclude those already in exact
            var newIds = likeSymbolIds.Where(id => !exactSymbolIds.Contains(id)).ToList();
            if (newIds.Count > 0)
            {
                var chunkIds2 = await GetChunkIdsForSymbolsAsync(newIds, ct).ConfigureAwait(false);
                foreach (var cid in chunkIds2) relatedChunkIds.Add(cid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Structural LIKE branch failed query={Query}", query);
        }

        return relatedChunkIds.ToList();
    }

    private async Task<Dictionary<Guid, ChunkInfo>> LoadChunkInfosAsync(List<Guid> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0) return new Dictionary<Guid, ChunkInfo>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ids = chunkIds.Select(g => g.ToString()).ToArray();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@c{i}"));
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"c{i}", ids[i]);

        var sql = $@"
SELECT c.id as chunk_id, c.repository_id, c.file_id, c.symbol_id, c.language as chunk_language, c.start_line, c.end_line, c.source,
       f.path as file_path, f.language as file_language,
       s.fqn, s.name as symbol_name, s.kind as symbol_kind, s.language as symbol_language
FROM chunks c
JOIN files f ON f.id = c.file_id
LEFT JOIN symbols s ON s.id = c.symbol_id
WHERE c.id IN ({placeholders});";

        var rows = await conn.QueryAsync<ChunkInfoRow>(sql, p).ConfigureAwait(false);
        var dict = new Dictionary<Guid, ChunkInfo>();
        foreach (var r in rows)
        {
            if (!Guid.TryParse(r.chunk_id, out var cid)) continue;
            if (!Guid.TryParse(r.repository_id, out var rid)) continue;
            if (!Guid.TryParse(r.file_id, out var fid)) continue;
            Guid? sid = Guid.TryParse(r.symbol_id, out var sg) ? sg : null;
            dict[cid] = new ChunkInfo(
                ChunkId: cid,
                RepositoryId: rid,
                FileId: fid,
                SymbolId: sid,
                Language: r.chunk_language,
                FileLanguage: r.file_language,
                SymbolLanguage: r.symbol_language,
                FilePath: r.file_path ?? string.Empty,
                StartLine: r.start_line,
                EndLine: r.end_line,
                Source: r.source ?? string.Empty,
                SymbolName: r.symbol_name,
                Fqn: r.fqn,
                SymbolKind: r.symbol_kind);
        }
        return dict;
    }

    private async Task<IReadOnlyList<Symbol>> LoadSymbolsByIdsAsync(string[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return Array.Empty<Symbol>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"id{i}", ids[i]);
        var sql = $"SELECT s.*, f.path as file_path FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.id IN ({placeholders});";
        var rows = await conn.QueryAsync<SymbolRow>(sql, p).ConfigureAwait(false);
        return rows.Select(MapSymbolRow).ToList();
    }

    private static Symbol MapSymbolRow(SymbolRow r)
    {
        return new Symbol(
            Id: Guid.Parse(r.id),
            RepositoryId: Guid.Parse(r.repository_id),
            FileId: Guid.Parse(r.file_id),
            Language: r.language,
            Kind: r.kind,
            Name: r.name,
            Fqn: r.fqn,
            Namespace: r.@namespace,
            Signature: r.signature,
            StartLine: r.start_line,
            EndLine: r.end_line,
            FilePath: r.file_path ?? string.Empty);
    }

    private static string BuildPreview(string source, int maxLines)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;
        var lines = source.Split('\n');
        if (lines.Length > maxLines) lines = lines.Take(maxLines).ToArray();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith("\r")) lines[i] = lines[i][..^1];
        var preview = string.Join("\n", lines);
        // Hard truncate if too long (>4000 chars)
        if (preview.Length > 4000) preview = preview[..4000] + "\n...";
        return preview;
    }

    private static string EscapeLike(string s)
    {
        // Escape % _ and \ for LIKE
        return s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
    }

    private sealed class CandidateAccumulator
    {
        public Guid ChunkId { get; }
        public HashSet<string> Origins { get; } = new(StringComparer.Ordinal);
        public double? FtsRank { get; set; }
        public double? VectorScore { get; set; }
        public double? VectorDistance { get; set; }
        public bool HasExact { get; set; }
        public double ExactBonus { get; set; }
        public bool HasStructural { get; set; }
        public CandidateAccumulator(Guid chunkId) => ChunkId = chunkId;
    }

    private sealed record ChunkInfo(
        Guid ChunkId,
        Guid RepositoryId,
        Guid FileId,
        Guid? SymbolId,
        string? Language,
        string? FileLanguage,
        string? SymbolLanguage,
        string FilePath,
        int? StartLine,
        int? EndLine,
        string Source,
        string? SymbolName,
        string? Fqn,
        string? SymbolKind);

    private sealed class ChunkInfoRow
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string file_id { get; set; } = "";
        public string? symbol_id { get; set; }
        public string? chunk_language { get; set; }
        public int? start_line { get; set; }
        public int? end_line { get; set; }
        public string? source { get; set; }
        public string? file_path { get; set; }
        public string? file_language { get; set; }
        public string? fqn { get; set; }
        public string? symbol_name { get; set; }
        public string? symbol_kind { get; set; }
        public string? symbol_language { get; set; }
    }

    private sealed class SymbolRow
    {
        public string id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string file_id { get; set; } = "";
        public string language { get; set; } = "";
        public string kind { get; set; } = "";
        public string name { get; set; } = "";
        public string? fqn { get; set; }
        public string? @namespace { get; set; }
        public string? signature { get; set; }
        public int? start_line { get; set; }
        public int? end_line { get; set; }
        public string? file_path { get; set; }
    }

    private sealed class IdRow { public string id { get; set; } = ""; }
    private sealed class RelationRow { public string from_symbol_id { get; set; } = ""; public string to_symbol_id { get; set; } = ""; public string relation_type { get; set; } = ""; }
    private sealed class RelationPairRow { public string from_symbol_id { get; set; } = ""; public string to_symbol_id { get; set; } = ""; }
    private sealed class RepoRow { public string id { get; set; } = ""; public string local_path { get; set; } = ""; }
    private sealed class FileRow { public string id { get; set; } = ""; public string repository_id { get; set; } = ""; public string path { get; set; } = ""; public string? language { get; set; } = ""; }
    private sealed class ChunkSourceRow { public string source { get; set; } = ""; public int? start_line { get; set; } public int? end_line { get; set; } }
}
