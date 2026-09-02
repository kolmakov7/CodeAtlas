using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Search.Models;

namespace CodeRag.Search.Services;

public sealed class RelationService
{
    private readonly IRelationStore _relations;
    private readonly SymbolService _symbols;
    public RelationService(IRelationStore relations, SymbolService symbols) { _relations = relations; _symbols = symbols; }

    private static readonly RelationType[] ReferenceTypes = { RelationType.References, RelationType.Uses, RelationType.Calls, RelationType.DependsOn, RelationType.Imports };

    private static readonly Dictionary<string, string> RelationAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["call"] = nameof(RelationType.Calls),
        ["calls"] = nameof(RelationType.Calls),
        ["contain"] = nameof(RelationType.Contains),
        ["contains"] = nameof(RelationType.Contains),
        ["depend"] = nameof(RelationType.DependsOn),
        ["depends"] = nameof(RelationType.DependsOn),
        ["dependson"] = nameof(RelationType.DependsOn),
        ["inherit"] = nameof(RelationType.Inherits),
        ["inherits"] = nameof(RelationType.Inherits),
        ["implement"] = nameof(RelationType.Implements),
        ["implements"] = nameof(RelationType.Implements),
        ["override"] = nameof(RelationType.Overrides),
        ["overrides"] = nameof(RelationType.Overrides),
        ["ref"] = nameof(RelationType.References),
        ["refs"] = nameof(RelationType.References),
        ["reference"] = nameof(RelationType.References),
        ["references"] = nameof(RelationType.References),
        ["use"] = nameof(RelationType.References),
        ["uses"] = nameof(RelationType.References),
        ["import"] = nameof(RelationType.References),
        ["imports"] = nameof(RelationType.References),
    };

    public async Task<IReadOnlyList<Reference>> FindReferencesAsync(Guid symbolId, CancellationToken ct)
    {
        var rows = await _relations.QueryEndpointsAsync(symbolId, RelationDirection.Incoming, ReferenceTypes.Select(t => t.ToString()).ToArray(), ct).ConfigureAwait(false);
        return rows.Select(r => new Reference(r.FromSymbolId, r.ToSymbolId, r.RelationType)).ToList();
    }

    public async Task<IReadOnlyList<RelationEntry>> GetRelationsAsync(Guid symbolId, RelationDirection direction = RelationDirection.Both, string[]? types = null, CancellationToken ct = default)
    {
        string[]? normalized = null;
        if (types is { Length: > 0 })
        {
            var list = types.Select(Normalize).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).Distinct(StringComparer.Ordinal).ToArray();
            if (list.Length > 0) normalized = list;
        }
        var rows = await _relations.QueryEndpointsAsync(symbolId, direction, normalized, ct).ConfigureAwait(false);
        if (rows.Count == 0) return Array.Empty<RelationEntry>();
        var allIds = rows.SelectMany(r => new[] { r.FromSymbolId, r.ToSymbolId }).Distinct().ToArray();
        var symbols = (await _symbols.LoadByIdsAsync(allIds, ct).ConfigureAwait(false)).ToDictionary(s => s.Id);
        return rows.Select(r => new RelationEntry(r.FromSymbolId, r.ToSymbolId, r.RelationType,
            symbols.TryGetValue(r.FromSymbolId, out var f) ? f : null,
            symbols.TryGetValue(r.ToSymbolId, out var t) ? t : null)).ToList();
    }

    public static string? Normalize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var key = new string(token.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return RelationAliases.TryGetValue(key, out var value) ? value : token.Trim();
    }

    public async Task<IReadOnlyList<Symbol>> FindImplementationsAsync(Guid symbolId, CancellationToken ct) => await FindByTypeAsync(symbolId, nameof(RelationType.Implements), to: false, ct).ConfigureAwait(false);
    public async Task<IReadOnlyList<Symbol>> FindCallersAsync(Guid symbolId, CancellationToken ct) => await FindByTypeAsync(symbolId, nameof(RelationType.Calls), to: true, ct).ConfigureAwait(false);
    public async Task<IReadOnlyList<Symbol>> FindCalleesAsync(Guid symbolId, CancellationToken ct) => await FindByTypeAsync(symbolId, nameof(RelationType.Calls), to: false, ct, outgoing: true).ConfigureAwait(false);

    private async Task<IReadOnlyList<Symbol>> FindByTypeAsync(Guid symbolId, string type, bool to, CancellationToken ct, bool outgoing = false)
    {
        var direction = outgoing ? RelationDirection.Outgoing : RelationDirection.Incoming;
        var rows = await _relations.QueryEndpointsAsync(symbolId, direction, new[] { type }, ct).ConfigureAwait(false);
        if (rows.Count == 0) return Array.Empty<Symbol>();
        var ids = rows.Select(r => outgoing ? r.ToSymbolId : r.FromSymbolId).Distinct().ToArray();
        return await _symbols.LoadByIdsAsync(ids, ct).ConfigureAwait(false);
    }
}
