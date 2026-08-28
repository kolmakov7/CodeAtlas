using CodeRag.Api.Models;
using CodeRag.Search;
using CodeRag.Search.Models;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class SearchController : ControllerBase
{
    private readonly ISearchService _search;
    private readonly ILogger<SearchController> _logger;

    public SearchController(ISearchService search, ILogger<SearchController> logger)
    {
        _search = search;
        _logger = logger;
    }

    [HttpPost("search")]
    [ProducesResponseType(typeof(SearchApiResponse), 200)]
    public async Task<IActionResult> Search([FromBody] SearchApiRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Query))
            return Problem(title: "Query must not be empty", statusCode: 400);

        var mode = ParseMode(req.Mode);
        var searchReq = new SearchRequest(
            req.Query,
            req.RepositoryIds,
            req.Languages,
            req.SymbolKind,
            mode,
            req.Limit ?? 20,
            req.Offset ?? 0);

        _logger.LogInformation("POST /api/search queryLen={Len} mode={Mode} repos={Repos} limit={Limit}",
            req.Query.Length, mode, req.RepositoryIds?.Length ?? 0, searchReq.Limit);

        try
        {
            var result = await _search.SearchCodeAsync(searchReq, ct).ConfigureAwait(false);
            var items = result.Items.Select(i => new SearchResultItemDto(
                i.RepositoryId,
                i.RepositoryName ?? i.RepositoryId.ToString(),
                i.ChunkId,
                i.FileId,
                i.FilePath,
                i.SymbolId,
                i.SymbolName,
                i.Fqn,
                i.Language,
                i.StartLine,
                i.EndLine,
                i.Score,
                i.Origin,
                Truncate(i.Preview, 20))).ToList();

            return Ok(new SearchApiResponse(items, result.Total, result.Warnings, result.ElapsedMs));
        }
        catch (ArgumentException ex)
        {
            return Problem(title: ex.Message, statusCode: 400);
        }
    }

    [HttpPost("symbols/find")]
    public async Task<IActionResult> FindSymbol([FromBody] FindSymbolApiRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Query))
            return Problem(title: "Query must not be empty", statusCode: 400);

        var findReq = new FindSymbolRequest(req.Query, req.RepositoryId, req.Language, null, req.Limit ?? 20, req.Offset ?? 0);
        _logger.LogInformation("POST /api/symbols/find query='{Query}' repo={Repo}", TruncateLog(req.Query), req.RepositoryId);
        var result = await _search.FindSymbolAsync(findReq, ct).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("references")]
    public async Task<IActionResult> FindReferences([FromBody] SymbolIdRequest req, CancellationToken ct)
    {
        if (req is null || req.SymbolId == Guid.Empty) return Problem(title: "symbolId required", statusCode: 400);
        try
        {
            var refs = await _search.FindReferencesAsync(req.SymbolId, ct).ConfigureAwait(false);
            return Ok(refs);
        }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    [HttpPost("implementations")]
    public async Task<IActionResult> FindImplementations([FromBody] SymbolIdRequest req, CancellationToken ct)
    {
        if (req is null || req.SymbolId == Guid.Empty) return Problem(title: "symbolId required", statusCode: 400);
        try
        {
            var impls = await _search.FindImplementationsAsync(req.SymbolId, ct).ConfigureAwait(false);
            return Ok(impls);
        }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    [HttpPost("callers")]
    public async Task<IActionResult> FindCallers([FromBody] SymbolIdRequest req, CancellationToken ct)
    {
        if (req is null || req.SymbolId == Guid.Empty) return Problem(title: "symbolId required", statusCode: 400);
        try
        {
            var callers = await _search.FindCallersAsync(req.SymbolId, ct).ConfigureAwait(false);
            return Ok(callers);
        }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    [HttpPost("callees")]
    public async Task<IActionResult> FindCallees([FromBody] SymbolIdRequest req, CancellationToken ct)
    {
        if (req is null || req.SymbolId == Guid.Empty) return Problem(title: "symbolId required", statusCode: 400);
        try
        {
            var callees = await _search.FindCalleesAsync(req.SymbolId, ct).ConfigureAwait(false);
            return Ok(callees);
        }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    [HttpGet("repositories/{id:guid}/file")]
    public async Task<IActionResult> GetCode(Guid id, [FromQuery] string path, [FromQuery] string? lines, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return Problem(title: "path query required", statusCode: 400);
        if (path.Contains("..")) return Problem(title: "path traversal not allowed", statusCode: 400);

        try
        {
            var (start, end) = ParseLines(lines);
            var slice = await _search.GetCodeAsync(new GetCodeRequest(id, path, start, end), ct).ConfigureAwait(false);
            var preview = Truncate(slice.Preview, 20);
            return Ok(new
            {
                slice.RepositoryId,
                slice.FileId,
                slice.Path,
                slice.Language,
                slice.StartLine,
                slice.EndLine,
                slice.TotalLines,
                content = slice.Content,
                preview
            });
        }
        catch (ArgumentException ex) { return Problem(title: ex.Message, statusCode: 400); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (FileNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    private static SearchMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return SearchMode.Hybrid;
        return Enum.TryParse<SearchMode>(mode, true, out var m) ? m : SearchMode.Hybrid;
    }

    private static (int? Start, int? End) ParseLines(string? lines)
    {
        if (string.IsNullOrWhiteSpace(lines)) return (null, null);
        var s = lines.Trim();
        char[] seps = ['-', ':', ','];
        var sep = seps.FirstOrDefault(c => s.Contains(c));
        if (sep != default)
        {
            var parts = s.Split(sep);
            if (parts.Length == 2)
            {
                int? start = int.TryParse(parts[0].Trim(), out var a) ? a : null;
                int? end = int.TryParse(parts[1].Trim(), out var b) ? b : null;
                return (start, end);
            }
        }
        else if (int.TryParse(s, out var single))
        {
            return (single, single);
        }
        return (null, null);
    }

    private static string Truncate(string s, int maxLines)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var lines = s.Split('\n');
        if (lines.Length <= maxLines) return s;
        return string.Join("\n", lines.Take(maxLines));
    }

    private static string TruncateLog(string s) => s.Length > 100 ? s.Substring(0, 100) + "..." : s;
}
