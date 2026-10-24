using CodeRag.Api.Models;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation;
using CodeRag.Documentation.Models;
using CodeRag.Indexer;
using Microsoft.AspNetCore.Mvc;
using DocumentationEntity = CodeRag.Domain.Entities.Documentation;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class DocumentationController : ControllerBase
{
    private readonly DocumentationService _docs;
    private readonly IDocumentationStore _store;
    private readonly IRepositoryStore _repoStore;
    private readonly ISymbolStore _symbols;
    private readonly IFileStore _files;
    private readonly IJobRunner _jobRunner;
    private readonly ILogger<DocumentationController> _logger;

    public DocumentationController(
        DocumentationService docs,
        IDocumentationStore store,
        IRepositoryStore repoStore,
        ISymbolStore symbols,
        IFileStore files,
        IJobRunner jobRunner,
        ILogger<DocumentationController> logger)
    {
        _docs = docs;
        _store = store;
        _repoStore = repoStore;
        _symbols = symbols;
        _files = files;
        _jobRunner = jobRunner;
        _logger = logger;
    }

    // ------------------------------------------------------------------ Generate

    /// <summary>Enqueue whole-repository documentation generation (background job).</summary>
    [HttpPost("repositories/{repoId:guid}/generate")]
    public async Task<IActionResult> GenerateRepository(Guid repoId, [FromBody] GenerateRepositoryDocumentationRequest? req, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(repoId, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {repoId} not found", statusCode: 404);

        _logger.LogInformation("POST /api/documentation/repositories/{Repo}/generate force={Force} maxSymbols={Max}",
            repoId, req?.Force ?? false, req?.MaxSymbols);
        var maxSymbols = req?.MaxSymbols is int m and > 0 ? m : (int?)null;
        var strategy = string.Equals(req?.Strategy, "kind", StringComparison.OrdinalIgnoreCase) ? "kind" : "usage";
        var options = System.Text.Json.JsonSerializer.Serialize(new
        {
            force = req?.Force ?? false,
            maxSymbols,
            strategy,
            minUsage = Math.Max(0, req?.MinUsage ?? 0)
        });
        var job = IndexingJob.Create(Guid.NewGuid(), repoId, JobType.Documentation, options: options);
        await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
        return Accepted(new { jobId = job.Id });
    }

    /// <summary>Generate documentation for a single target (symbol or module) inline.</summary>
    [HttpPost("targets/generate")]
    public async Task<IActionResult> GenerateTarget([FromBody] GenerateDocumentationRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "Body required", statusCode: 400);

        if (!DocumentationTargetTypeExtensions.TryParseType(req.TargetType, out var targetType))
            return Problem(title: $"Unknown targetType '{req.TargetType}' (expected symbol|module)", statusCode: 400);

        var repo = await _repoStore.GetByIdAsync(req.RepositoryId, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {req.RepositoryId} not found", statusCode: 404);

        DocumentationTarget target;
        if (targetType == DocumentationTargetType.Symbol)
        {
            Symbol? symbol;
            if (req.SymbolId is Guid sid)
            {
                symbol = await _symbols.GetByIdAsync(sid, ct).ConfigureAwait(false);
                if (symbol is null) return Problem(title: $"Symbol {sid} not found", statusCode: 404);
            }
            else
            {
                // Resolve a symbol from a human-typed name / FQN when no id is supplied.
                var term = (req.TargetRef ?? req.Name ?? req.Namespace)?.Trim();
                if (string.IsNullOrWhiteSpace(term))
                    return Problem(title: "Provide symbolId, or targetRef/name with the symbol name or FQN", statusCode: 400);

                var match = (await _symbols.SearchAsync(term, repo.Id, limit: 1, ct: ct).ConfigureAwait(false)).FirstOrDefault();
                if (match is null) return Problem(title: $"Symbol '{term}' not found in this repository", statusCode: 404);
                symbol = await _symbols.GetByIdAsync(match.Id, ct).ConfigureAwait(false);
                if (symbol is null) return Problem(title: $"Symbol '{term}' not found in this repository", statusCode: 404);
            }

            var file = (await GetFilePathAsync(repo.Id, symbol, ct).ConfigureAwait(false));
            target = new DocumentationTarget(
                DocumentationTargetType.Symbol, repo.Id,
                DocumentationTarget.BuildTargetRef(DocumentationTargetType.Symbol, symbol.Fqn, symbol.Name, null, null),
                symbol.Name,
                SymbolId: symbol.Id,
                RepositoryName: repo.Name,
                Namespace: symbol.Namespace,
                FilePath: file,
                Language: symbol.Language,
                IndexedCommit: repo.IndexedCommit,
                Branch: repo.Branch);
        }
        else // Module
        {
            var moduleRef = DocumentationTarget.NormalizePath(req.TargetRef ?? req.FilePath);
            target = new DocumentationTarget(
                DocumentationTargetType.Module, repo.Id, moduleRef,
                string.IsNullOrWhiteSpace(req.Name) ? moduleRef : req.Name!,
                RepositoryName: repo.Name,
                FilePath: req.FilePath,
                IndexedCommit: repo.IndexedCommit,
                Branch: repo.Branch);
        }

        _logger.LogInformation("POST /api/documentation/targets/generate repo={Repo} type={Type} ref={Ref} force={Force}",
            repo.Id, targetType.ToText(), target.TargetRef, req.Force);

        var result = await _docs.GenerateForTargetAsync(target, req.Force, ct).ConfigureAwait(false);
        if (result.Documentation is null)
            return Problem(title: result.Error ?? "Documentation generation failed", statusCode: 422);

        return Ok(new GenerateDocumentationResponse(MapDetail(result.Documentation), result.Generated));
    }

    // ------------------------------------------------------------------ Query

    /// <summary>List all documentation entries for a repository (any target type), most recently updated first.</summary>
    [HttpGet("repositories/{repoId:guid}")]
    public async Task<IActionResult> ListForRepository(Guid repoId, [FromQuery] string? targetType, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(repoId, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {repoId} not found", statusCode: 404);

        var docs = string.IsNullOrWhiteSpace(targetType)
            ? await _store.GetByRepositoryAsync(repoId, ct).ConfigureAwait(false)
            : await _store.GetByRepositoryAndTypeAsync(repoId, ParseType(targetType), ct).ConfigureAwait(false);

        return Ok(docs.Select(MapList).ToList());
    }

    /// <summary>Get a single documentation entry by id (with content).</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var doc = await _store.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (doc is null) return Problem(title: $"Documentation {id} not found", statusCode: 404);
        return Ok(MapDetail(doc));
    }

    /// <summary>Get a documentation entry by repository + target (with content).</summary>
    [HttpGet("repositories/{repoId:guid}/targets/{targetType}")]
    public async Task<IActionResult> GetByTarget(Guid repoId, string targetType, [FromQuery] string targetRef, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(repoId, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {repoId} not found", statusCode: 404);

        var doc = await _store.GetByTargetAsync(repoId, ParseType(targetType), targetRef, ct).ConfigureAwait(false);
        if (doc is null) return Problem(title: "Documentation not generated for this target yet", statusCode: 404);
        return Ok(MapDetail(doc));
    }

    /// <summary>Delete a documentation entry (e.g. to force a clean regeneration).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var doc = await _store.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (doc is null) return Problem(title: $"Documentation {id} not found", statusCode: 404);
        await _store.DeleteAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    // ------------------------------------------------------------------ Mapping

    private static DocumentationTargetType ParseType(string raw)
    {
        _ = DocumentationTargetTypeExtensions.TryParseType(raw, out var type);
        return type;
    }

    private static DocumentationDto MapList(DocumentationEntity d) => new(
        d.Id, d.RepositoryId, d.TargetType.ToText(), d.TargetId, d.TargetRef, d.Name,
        d.Namespace, d.FilePath, d.Language, d.Status.ToString().ToLowerInvariant(), d.Error,
        d.Model, d.PromptVersion, d.SourceHash, d.GenerationCommit, d.ContextChars, d.OutputChars,
        d.GeneratedAt.ToString("o"), d.UpdatedAt.ToString("o"));

    private static DocumentationDetailDto MapDetail(DocumentationEntity d) => new(
        d.Id, d.RepositoryId, d.TargetType.ToText(), d.TargetId, d.TargetRef, d.Name,
        d.Namespace, d.FilePath, d.Language, d.Status.ToString().ToLowerInvariant(), d.Error,
        d.Model, d.PromptVersion, d.SourceHash, d.GenerationCommit, d.ContextChars, d.OutputChars,
        d.GeneratedAt.ToString("o"), d.UpdatedAt.ToString("o"), d.Content);

    private async Task<string?> GetFilePathAsync(Guid repoId, Symbol symbol, CancellationToken ct)
    {
        var file = await _files.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
        return file?.Path;
    }
}
