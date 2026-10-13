using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation.Models;
using CodeRag.Search;
using Microsoft.Extensions.Logging;
using DocumentationEntity = CodeRag.Domain.Entities.Documentation;

namespace CodeRag.Documentation;

/// <summary>Outcome of a single-target documentation operation.</summary>
public sealed record DocumentationResult(
    DocumentationEntity? Documentation,
    bool Generated,
    string? Error = null)
{
    public bool Succeeded => Documentation is not null && Error is null;
}

/// <summary>
/// Orchestrates documentation generation for a target: assembles the context, decides whether a
/// (re)generation is needed via content-hash staleness, calls the generator, and persists the result.
/// This is the unit of work a background Documentation job performs per target.
/// </summary>
public sealed class DocumentationService
{
    private readonly IDocumentationContextBuilder _contextBuilder;
    private readonly IDocumentationGenerator _generator;
    private readonly IDocumentationStore _store;
    private readonly ITargetContentHasher _hasher;
    private readonly ISymbolStore _symbols;
    private readonly IFileStore _files;
    private readonly ILogger<DocumentationService> _logger;

    public DocumentationService(
        IDocumentationContextBuilder contextBuilder,
        IDocumentationGenerator generator,
        IDocumentationStore store,
        ITargetContentHasher hasher,
        ISymbolStore symbols,
        IFileStore files,
        ILogger<DocumentationService> logger)
    {
        _contextBuilder = contextBuilder;
        _generator = generator;
        _store = store;
        _hasher = hasher;
        _symbols = symbols;
        _files = files;
        _logger = logger;
    }

    public bool IsAvailable => _generator.IsAvailable;

    /// <summary>Generate (or return the up-to-date) documentation for one target.</summary>
    public async Task<DocumentationResult> GenerateForTargetAsync(
        DocumentationTarget target, bool force = false, CancellationToken ct = default)
    {
        if (!_generator.IsAvailable)
            return new DocumentationResult(null, false, "LLM not configured");

        var context = await _contextBuilder.BuildAsync(target, null, ct).ConfigureAwait(false);
        var currentHash = await _hasher.ComputeAsync(
            target.RepositoryId, target.Type, target.TargetRef, target.SymbolId, target.FilePath, ct)
            .ConfigureAwait(false);

        var existing = await _store.GetByTargetAsync(
            target.RepositoryId, target.Type, target.TargetRef, ct).ConfigureAwait(false);

        var stale = existing is not null && existing.IsStaleAgainst(currentHash);
        if (existing is not null && !force && !stale)
        {
            // Up to date — nothing to do.
            return new DocumentationResult(existing, Generated: false);
        }

        var doc = existing ?? DocumentationEntity.Create(
            Guid.NewGuid(), target.RepositoryId, target.Type, target.TargetRef, target.Name,
            targetId: target.SymbolId, @namespace: target.Namespace, filePath: target.FilePath,
            language: target.Language, status: DocumentationStatus.Generating);

        doc.MarkGenerating();
        await _store.UpsertAsync(doc, ct).ConfigureAwait(false);

        try
        {
            var gen = await _generator.GenerateAsync(context, ct).ConfigureAwait(false);
            doc.UpdateContent(
                gen.Content, gen.Model, gen.PromptVersion, currentHash,
                targetId: target.SymbolId, generationCommit: context.IndexedCommit,
                contextChars: gen.ContextChars, filePath: target.FilePath);
            await _store.UpsertAsync(doc, ct).ConfigureAwait(false);
            _logger.LogInformation("Generated {Type} docs for {Name} ({Chars} chars, prompt {Prompt})",
                target.Type.ToText(), target.Name, gen.OutputChars, gen.PromptVersion);
            return new DocumentationResult(doc, Generated: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Documentation generation failed for {Type} {Name}", target.Type.ToText(), target.Name);
            doc.MarkFailed(ex.Message);
            await _store.UpsertAsync(doc, ct).ConfigureAwait(false);
            return new DocumentationResult(doc, Generated: false, Error: ex.Message);
        }
    }

    /// <summary>Generate documentation for every "documentable" symbol in a repository, plus the repo-level doc.</summary>
    public async Task<DocumentationBatchResult> GenerateForRepositoryAsync(
        DocumentationTarget repoTarget,
        bool includeRepositoryLevel = true,
        bool force = false,
        int? maxSymbols = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new DocumentationBatchResult();
        var total = (includeRepositoryLevel ? 1 : 0);
        var done = 0;
        void Report() => progress?.Report((done, total));

        if (includeRepositoryLevel)
        {
            result.Add(await GenerateForTargetAsync(repoTarget, force, ct).ConfigureAwait(false));
            done++; Report();
        }

        var files = await _files.GetByRepositoryAsync(repoTarget.RepositoryId, ct).ConfigureAwait(false);
        var pathById = files.ToDictionary(f => f.Id, f => f.Path);
        var symbols = await _symbols.GetByRepositoryAsync(repoTarget.RepositoryId, ct).ConfigureAwait(false);
        var documentable = symbols
            .Where(s => IsDocumentable(s.Kind))
            .OrderBy(s => s.Fqn ?? s.Name)
            .ToList();
        if (maxSymbols is int m)
            documentable = documentable.Take(m).ToList();

        total += documentable.Count;
        Report();

        foreach (var sym in documentable)
        {
            ct.ThrowIfCancellationRequested();
            var target = new DocumentationTarget(
                DocumentationTargetType.Symbol,
                repoTarget.RepositoryId,
                DocumentationTarget.BuildTargetRef(DocumentationTargetType.Symbol, sym.Fqn, sym.Name, null, null),
                sym.Name,
                SymbolId: sym.Id,
                RepositoryName: repoTarget.RepositoryName,
                Namespace: sym.Namespace,
                FilePath: pathById.GetValueOrDefault(sym.FileId),
                Language: sym.Language,
                IndexedCommit: repoTarget.IndexedCommit,
                Branch: repoTarget.Branch);

            result.Add(await GenerateForTargetAsync(target, force, ct).ConfigureAwait(false));
            done++; Report();
        }

        return result;
    }

    /// <summary>Kinds worth a dedicated documentation entry (types; methods only when explicitly targeted).</summary>
    private static bool IsDocumentable(SymbolKind kind) => kind switch
    {
        SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct
        or SymbolKind.Record or SymbolKind.Enum => true,
        _ => false
    };
}

/// <summary>Aggregated outcome of a multi-target (repository) documentation run.</summary>
public sealed class DocumentationBatchResult
{
    private readonly List<DocumentationResult> _results = new();

    public void Add(DocumentationResult r) => _results.Add(r);

    public int Total => _results.Count;
    public int Succeeded => _results.Count(r => r.Succeeded);
    public int Failed => _results.Count(r => !r.Succeeded);

    public IReadOnlyList<DocumentationResult> Results => _results;
}
