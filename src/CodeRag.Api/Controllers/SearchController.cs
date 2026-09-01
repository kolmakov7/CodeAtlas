using CodeRag.Api.Mappers;
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

        var mode = SearchResultMapper.ParseMode(req.Mode);
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
            return Ok(SearchResultMapper.ToApiResponse(result));
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

    [HttpGet("symbols/{id:guid}")]
    public async Task<IActionResult> GetSymbol(Guid id, CancellationToken ct)
    {
        var sym = await _search.GetSymbolAsync(id, ct).ConfigureAwait(false);
        if (sym is null) return Problem(title: $"Symbol {id} not found", statusCode: 404);
        return Ok(sym);
    }

    [HttpGet("symbols/{id:guid}/relations")]
    [ProducesResponseType(typeof(IReadOnlyList<CodeRag.Search.Models.RelationEntry>), 200)]
    public async Task<IActionResult> GetSymbolRelations(
        Guid id,
        [FromQuery] string? direction,
        [FromQuery] string[]? types,
        CancellationToken ct)
    {
        var dir = CodeRag.Domain.Enums.RelationDirection.Both;
        if (!string.IsNullOrWhiteSpace(direction) &&
            !Enum.TryParse<CodeRag.Domain.Enums.RelationDirection>(direction, ignoreCase: true, out dir))
            return Problem(title: "direction must be outgoing, incoming or both", statusCode: 400);
        try
        {
            var relations = await _search.GetRelationsAsync(id, dir, types, ct).ConfigureAwait(false);
            return Ok(relations);
        }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    [HttpGet("repositories/{id:guid}/file")]
    [ProducesResponseType(typeof(GetCodeResponseDto), 200)]
    public async Task<IActionResult> GetCode(Guid id, [FromQuery] string path, [FromQuery] string? lines, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return Problem(title: "path query required", statusCode: 400);
        // Secure traversal check: normalize and ensure no ".." segment survives normalization
        var normalized = Path.GetFullPath(Path.Combine("/tmp", path));
        if (path.Split('/', '\\').Any(seg => seg == "..") || normalized.Contains("..") || Path.GetFileName(path).Contains(".."))
            return Problem(title: "path traversal not allowed", statusCode: 400);
        if (path.Contains('\0')) return Problem(title: "invalid path", statusCode: 400);

        try
        {
            var (start, end) = GetCodeResponseMapper.ParseLines(lines);
            var slice = await _search.GetCodeAsync(new GetCodeRequest(id, path, start, end), ct).ConfigureAwait(false);
            return Ok(GetCodeResponseMapper.ToDto(slice));
        }
        catch (ArgumentException ex) { return Problem(title: ex.Message, statusCode: 400); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (FileNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    private static string TruncateLog(string s) => s.Length > 100 ? s.Substring(0, 100) + "..." : s;
}
