using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation.Models;
using CodeRag.Search;
using CodeRag.Search.Models;
using SymbolEntity = CodeRag.Domain.Entities.Symbol;
using SearchSymbol = CodeRag.Search.Models.Symbol;

namespace CodeRag.Documentation;

/// <summary>
/// Assembles a <see cref="DocumentationContext"/> for a target strictly from the existing index
/// (symbols, relations, on-disk source). No new parsing is performed. All lookups are defensive:
/// a missing piece degrades to an empty section / warning rather than throwing.
/// </summary>
public sealed class DocumentationContextBuilder : IDocumentationContextBuilder
{
    private readonly ISymbolStore _symbols;
    private readonly IFileStore _files;
    private readonly IRelationStore _relations;
    private readonly IRepositoryStore _repos;
    private readonly ISearchService _search;

    public DocumentationContextBuilder(
        ISymbolStore symbols,
        IFileStore files,
        IRelationStore relations,
        IRepositoryStore repos,
        ISearchService search)
    {
        _symbols = symbols;
        _files = files;
        _relations = relations;
        _repos = repos;
        _search = search;
    }

    public async Task<DocumentationContext> BuildAsync(
        DocumentationTarget target,
        DocumentationBuildOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= DocumentationBuildOptions.Default;
        var repo = await _repos.GetByIdAsync(target.RepositoryId, ct).ConfigureAwait(false);
        var context = new DocumentationContext
        {
            Target = target,
            RepositoryName = repo?.Name ?? target.RepositoryName,
            IndexedCommit = target.IndexedCommit ?? repo?.IndexedCommit,
            Branch = target.Branch ?? repo?.Branch
        };

        if (target.IsSymbol && target.SymbolId is Guid sid)
            await BuildSymbolAsync(target, sid, context, options, ct).ConfigureAwait(false);
        else if (target.Type == DocumentationTargetType.Module)
            await BuildModuleAsync(target, context, options, ct).ConfigureAwait(false);
        else
            await BuildRepositoryAsync(target, context, options, ct).ConfigureAwait(false);

        return context;
    }

    // --------------------------------------------------------------------- Symbol

    private async Task BuildSymbolAsync(
        DocumentationTarget target, Guid symbolId, DocumentationContext ctx,
        DocumentationBuildOptions opts, CancellationToken ct)
    {
        var symbol = await _symbols.GetByIdAsync(symbolId, ct).ConfigureAwait(false);
        if (symbol is null)
        {
            ctx.Warnings = "The symbol could not be resolved from the index; only the request identity is available.";
            return;
        }

        var file = await _files.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
        var path = file?.Path;

        ctx.Language = symbol.Language;
        ctx.Namespace = symbol.Namespace;
        ctx.FilePath = path;
        ctx.StartLine = symbol.StartLine;
        ctx.EndLine = symbol.EndLine;
        ctx.Signature = symbol.Signature;

        // Primary source (the most important input).
        var source = await TryGetSourceAsync(ctx.Target.RepositoryId, path, symbol.StartLine, symbol.EndLine,
            maxLines: opts.MaxSourceLines, label: "Target source", ct: ct).ConfigureAwait(false);
        if (source is not null) ctx.Sources.Add(source);
        else ctx.Warnings = AppendNote(ctx.Warnings, "No source could be read for the target (file may be absent from disk).");

        // Parent type (for a member of a type).
        if (symbol.ParentSymbolId is Guid parentId)
        {
            var parent = await _symbols.GetByIdAsync(parentId, ct).ConfigureAwait(false);
            if (parent is not null)
            {
                var parentFile = await _files.GetByIdAsync(parent.FileId, ct).ConfigureAwait(false);
                ctx.ParentFqn = parent.Fqn ?? parent.Name;
                var parentSource = await TryGetSourceAsync(ctx.Target.RepositoryId, parentFile?.Path,
                    parent.StartLine, parent.EndLine, maxLines: opts.MaxSourceLines / 2, label: "Parent source", ct: ct)
                    .ConfigureAwait(false);
                if (parentSource is not null) ctx.ParentSource = parentSource;
            }
        }

        // Members (only meaningful for a type).
        if (IsTypeKind(symbol.Kind))
        {
            var fileSymbols = await _symbols.GetByFileAsync(symbol.FileId, ct).ConfigureAwait(false);
            var members = fileSymbols
                .Where(s => s.Id != symbol.Id && s.ParentSymbolId == symbol.Id)
                .OrderBy(s => s.StartLine ?? 0)
                .Take(opts.MaxMembers)
                .Select(s => ToContextSymbol(s, path))
                .ToList();
            ctx.Members = members;
        }

        // Relations (one hop, both directions) categorized by type.
        var entries = await _search.GetRelationsAsync(symbolId, RelationDirection.Both, null, ct).ConfigureAwait(false);
        var dependencies = new List<ContextSymbol>();
        var interfaces = new List<ContextSymbol>();
        var baseTypes = new List<ContextSymbol>();
        var callers = new List<ContextSymbol>();

        foreach (var e in entries)
        {
            bool outgoing = e.FromSymbolId == symbolId;
            var other = outgoing ? e.ToSymbol : e.FromSymbol;
            if (other is null) continue;

            switch (e.RelationType)
            {
                case nameof(RelationType.Inherits) when outgoing:
                    AddUnique(baseTypes, ToContextSymbol(other)); break;
                case nameof(RelationType.Implements) when outgoing:
                    AddUnique(interfaces, ToContextSymbol(other)); break;
                case nameof(RelationType.References) or nameof(RelationType.Calls)
                    or nameof(RelationType.Uses) or nameof(RelationType.DependsOn) when outgoing:
                    AddUnique(dependencies, ToContextSymbol(other)); break;
                case nameof(RelationType.Calls) or nameof(RelationType.References)
                    or nameof(RelationType.Uses) or nameof(RelationType.DependsOn) when !outgoing:
                    AddUnique(callers, ToContextSymbol(other)); break;
            }
        }

        ctx.Dependencies = dependencies.Take(opts.MaxDependencies).ToList();
        ctx.Interfaces = interfaces.Take(opts.MaxDependencies).ToList();
        ctx.BaseTypes = baseTypes.Take(opts.MaxDependencies).ToList();
        ctx.Callers = callers.Take(opts.MaxCallers).ToList();
        ctx.RelatedFileCount = CountDistinctFiles(entries);

        if (opts.IncludeTests)
            ctx.Tests = (await FindTestsAsync(symbol, ctx.Target.RepositoryId, ct).ConfigureAwait(false))
                .Take(opts.MaxCallers).ToList();

        ctx.RelatedSnippets = await CollectSnippetsAsync(symbol.Fqn ?? symbol.Name, ctx.Target.RepositoryId, path, opts, ct)
            .ConfigureAwait(false);
    }

    // --------------------------------------------------------------------- Module

    private async Task BuildModuleAsync(
        DocumentationTarget target, DocumentationContext ctx, DocumentationBuildOptions opts, CancellationToken ct)
    {
        var dir = DocumentationTarget.NormalizePath(target.TargetRef);
        var files = await _files.GetByRepositoryAsync(ctx.Target.RepositoryId, ct).ConfigureAwait(false);
        var inDir = files
            .Where(f => string.Equals(DocumentationTarget.NormalizePath(f.Path), dir, StringComparison.Ordinal)
                || DocumentationTarget.NormalizePath(f.Path).StartsWith(dir + "/", StringComparison.Ordinal))
            .OrderBy(f => f.Path)
            .ToList();

        if (inDir.Count == 0)
        {
            ctx.Warnings = AppendNote(ctx.Warnings, $"No files found under module path '{dir}'.");
            return;
        }

        var pathById = files.ToDictionary(f => f.Id, f => f.Path);
        var fileIds = inDir.Select(f => f.Id).ToHashSet();
        var symbols = await _symbols.GetByRepositoryAsync(ctx.Target.RepositoryId, ct).ConfigureAwait(false);
        var moduleSymbols = symbols.Where(s => fileIds.Contains(s.FileId)).ToList();

        // Representative components: top-level types in the module.
        var components = moduleSymbols
            .Where(s => IsTypeKind(s.Kind))
            .OrderBy(s => s.StartLine ?? 0)
            .Take(opts.MaxMembers)
            .Select(s => ToContextSymbol(s, pathById.GetValueOrDefault(s.FileId)))
            .ToList();
        ctx.Members = components;

        // Dependencies: relations whose source symbol is in the module.
        var moduleIds = moduleSymbols.Select(s => s.Id).ToHashSet();
        var deps = new List<ContextSymbol>();
        var seen = new HashSet<Guid>();
        foreach (var id in moduleIds)
        {
            foreach (var e in await _relations.GetBySymbolAsync(id, RelationDirection.Both, null, null, ct).ConfigureAwait(false))
            {
                Guid other = e.FromSymbolId == id ? e.ToSymbolId : e.FromSymbolId;
                if (other == id || moduleIds.Contains(other)) continue;
                if (!seen.Add(other)) continue;
                var sym = await _symbols.GetByIdAsync(other, ct).ConfigureAwait(false);
                if (sym is not null) deps.Add(ToContextSymbol(sym, pathById.GetValueOrDefault(sym.FileId)));
                if (deps.Count >= opts.MaxDependencies) break;
            }
            if (deps.Count >= opts.MaxDependencies) break;
        }
        ctx.Dependencies = deps;

        // Source samples: a few representative files (most indexed symbols first).
        var byFile = moduleSymbols.GroupBy(s => s.FileId)
            .OrderByDescending(g => g.Count())
            .Take(opts.MaxRelatedSnippets)
            .Select(g => g.Key).ToList();

        foreach (var fileId in byFile)
        {
            var fe = inDir.FirstOrDefault(f => f.Id == fileId);
            if (fe is null) continue;
            var sample = await TryGetSourceAsync(ctx.Target.RepositoryId, fe.Path, null, null,
                maxLines: opts.MaxSnippetLines, label: "Source sample", ct: ct).ConfigureAwait(false);
            if (sample is not null) ctx.Sources.Add(sample);
        }

        ctx.RelatedFileCount = inDir.Count;
    }

    // --------------------------------------------------------------------- Repository

    private async Task BuildRepositoryAsync(
        DocumentationTarget target, DocumentationContext ctx, DocumentationBuildOptions opts, CancellationToken ct)
    {
        var files = await _files.GetByRepositoryAsync(ctx.Target.RepositoryId, ct).ConfigureAwait(false);
        ctx.RelatedFileCount = files.Count;
        if (files.Count == 0)
        {
            ctx.Warnings = AppendNote(ctx.Warnings, "The repository index contains no files.");
            return;
        }

        var pathById = files.ToDictionary(f => f.Id, f => f.Path);
        var symbols = await _symbols.GetByRepositoryAsync(ctx.Target.RepositoryId, ct).ConfigureAwait(false);

        // Languages present (informational, helps the writer frame conventions).
        var languages = files.Select(f => f.Language).Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l!).Take(8).ToList();
        if (languages.Count > 0)
            ctx.Warnings = AppendNote(ctx.Warnings, "Languages present: " + string.Join(", ", languages));

        // Key components: top-level types (no parent) across the repo, most referenced first.
        var refCount = new Dictionary<Guid, int>();
        var relations = await _relations.GetByRepositoryAsync(ctx.Target.RepositoryId, ct).ConfigureAwait(false);
        foreach (var r in relations)
        {
            if (refCount.TryGetValue(r.ToSymbolId, out var c)) refCount[r.ToSymbolId] = c + 1;
            else refCount[r.ToSymbolId] = 1;
        }

        var topTypes = symbols
            .Where(s => IsTypeKind(s.Kind) && s.ParentSymbolId == null)
            .OrderByDescending(s => refCount.TryGetValue(s.Id, out var c) ? c : 0)
            .ThenBy(s => s.Fqn ?? s.Name)
            .Take(opts.MaxMembers)
            .Select(s => ToContextSymbol(s, pathById.GetValueOrDefault(s.FileId)))
            .ToList();
        ctx.Members = topTypes;

        // Sample source: files with the most indexed symbols (closest to core).
        var byFileCount = symbols.GroupBy(s => s.FileId).ToDictionary(g => g.Key, g => g.Count());
        var sampleFileIds = byFileCount.OrderByDescending(kv => kv.Value).Take(opts.MaxRelatedSnippets).Select(kv => kv.Key).ToList();

        foreach (var fileId in sampleFileIds)
        {
            var fe = files.FirstOrDefault(f => f.Id == fileId);
            if (fe is null) continue;
            var sample = await TryGetSourceAsync(ctx.Target.RepositoryId, fe.Path, null, null,
                maxLines: opts.MaxSnippetLines, label: "Source sample", ct: ct).ConfigureAwait(false);
            if (sample is not null) ctx.Sources.Add(sample);
        }
    }

    // --------------------------------------------------------------------- Helpers

    private static bool IsTypeKind(SymbolKind kind) => kind switch
    {
        SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct
        or SymbolKind.Record or SymbolKind.Enum => true,
        _ => false
    };

    private static ContextSymbol ToContextSymbol(SymbolEntity s, string? filePath = null) => new(
        Name: s.Name,
        Fqn: s.Fqn,
        Kind: s.Kind.ToString(),
        Signature: s.Signature,
        FilePath: filePath,
        StartLine: s.StartLine,
        EndLine: s.EndLine);

    private static ContextSymbol ToContextSymbol(SearchSymbol s) => new(
        Name: s.Name,
        Fqn: s.Fqn,
        Kind: s.Kind,
        Signature: s.Signature,
        FilePath: s.FilePath,
        StartLine: s.StartLine,
        EndLine: s.EndLine);

    private static void AddUnique(List<ContextSymbol> list, ContextSymbol item)
    {
        if (list.All(x => !ReferenceEquals(x, item) && x.Display != item.Display && x.Fqn != item.Fqn))
            list.Add(item);
    }

    private static int CountDistinctFiles(IReadOnlyList<RelationEntry> entries) =>
        entries.Select(e => e.FromSymbol?.FilePath).Concat(entries.Select(e => e.ToSymbol?.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Count();

    private async Task<ContextSource?> TryGetSourceAsync(
        Guid repoId, string? path, int? startLine, int? endLine,
        int? maxLines, string label, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var slice = await _search.GetCodeAsync(
                new GetCodeRequest(repoId, path, startLine, endLine, maxLines), ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(slice.Content)) return null;
            return new ContextSource(label, slice.Language, slice.Path, slice.StartLine, slice.EndLine, slice.Content);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<ContextSymbol>> FindTestsAsync(SymbolEntity symbol, Guid repoId, CancellationToken ct)
    {
        try
        {
            var result = await _search.SearchCodeAsync(new SearchRequest(
                Query: symbol.Name,
                RepositoryIds: new[] { repoId },
                Mode: SearchMode.FullText,
                Limit: 20), ct).ConfigureAwait(false);

            return result.Items
                .Where(i => i.FilePath.Contains("test", StringComparison.OrdinalIgnoreCase)
                    || i.FilePath.Contains("spec", StringComparison.OrdinalIgnoreCase))
                .Select(i => new ContextSymbol(
                    Name: i.SymbolName ?? Path.GetFileName(i.FilePath),
                    Fqn: i.Fqn,
                    Kind: "test",
                    Signature: null,
                    FilePath: i.FilePath,
                    StartLine: i.StartLine,
                    EndLine: i.EndLine))
                .ToList();
        }
        catch
        {
            return new List<ContextSymbol>();
        }
    }

    private async Task<List<ContextSource>> CollectSnippetsAsync(
        string query, Guid repoId, string? excludePath, DocumentationBuildOptions opts, CancellationToken ct)
    {
        try
        {
            var result = await _search.SearchCodeAsync(new SearchRequest(
                Query: query,
                RepositoryIds: new[] { repoId },
                Mode: SearchMode.Hybrid,
                Limit: opts.MaxRelatedSnippets * 2), ct).ConfigureAwait(false);

            var snippets = new List<ContextSource>();
            foreach (var item in result.Items)
            {
                if (snippets.Count >= opts.MaxRelatedSnippets) break;
                if (excludePath is not null && string.Equals(item.FilePath, excludePath, StringComparison.Ordinal)) continue;
                try
                {
                    var slice = await _search.GetCodeAsync(new GetCodeRequest(
                        repoId, item.FilePath, item.StartLine, item.EndLine, opts.MaxSnippetLines), ct)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(slice.Content)) continue;
                    snippets.Add(new ContextSource("Related snippet", slice.Language, slice.Path,
                        slice.StartLine, slice.EndLine, TruncateToLines(slice.Content, opts.MaxSnippetLines)));
                }
                catch { /* skip unreadable snippet */ }
            }
            return snippets;
        }
        catch
        {
            return new List<ContextSource>();
        }
    }

    private static string TruncateToLines(string content, int maxLines)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines) return content;
        return string.Join('\n', lines.Take(maxLines)) + "\n…";
    }

    private static string? AppendNote(string? existing, string note)
        => string.IsNullOrWhiteSpace(existing) ? note : existing + "\n" + note;
}
