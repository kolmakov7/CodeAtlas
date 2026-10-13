using System.Security.Cryptography;
using System.Text;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;

namespace CodeRag.Documentation;

/// <summary>
/// Computes a stable content fingerprint for a documentation target from existing index metadata
/// (per-file hashes + one-hop dependency symbol files) — never by re-parsing source. The fingerprint
/// is a SHA-256 over the sorted set of "path:hash" lines, so any change to the target's own source or
/// a direct dependency's source produces a different value.
/// </summary>
public sealed class TargetContentHasher : ITargetContentHasher
{
    // Outgoing relation kinds whose endpoints are the target's direct dependencies.
    // (Contains is excluded — child symbols live in the target's own file, already covered.)
    private static readonly IReadOnlyList<RelationType> DependencyRelationTypes = new[]
    {
        RelationType.Calls,
        RelationType.Inherits,
        RelationType.Implements,
        RelationType.References,
        RelationType.Overrides,
        RelationType.Uses,
        RelationType.DependsOn,
        RelationType.Imports
    };

    private readonly IFileStore _files;
    private readonly ISymbolStore _symbols;
    private readonly IRelationStore _relations;

    public TargetContentHasher(IFileStore files, ISymbolStore symbols, IRelationStore relations)
    {
        _files = files;
        _symbols = symbols;
        _relations = relations;
    }

    public async Task<string?> ComputeAsync(
        Guid repositoryId,
        DocumentationTargetType targetType,
        string? targetRef,
        Guid? targetSymbolId,
        string? filePath,
        CancellationToken ct = default)
    {
        var byId = new Dictionary<Guid, FileEntry>();
        void Add(FileEntry? f) { if (f is not null) byId[f.Id] = f; }

        if (targetType == DocumentationTargetType.Symbol)
        {
            if (targetSymbolId is Guid sid)
            {
                var symbol = await _symbols.GetByIdAsync(sid, ct).ConfigureAwait(false);
                if (symbol is not null)
                    Add(await _files.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false));

                // Direct dependencies: other endpoints of the target's outgoing relations.
                try
                {
                    var rels = await _relations.GetBySymbolAsync(
                        sid, RelationDirection.Outgoing, repositoryId, DependencyRelationTypes, ct).ConfigureAwait(false);
                    foreach (var r in rels)
                    {
                        var other = r.FromSymbolId == sid ? r.ToSymbolId : r.FromSymbolId;
                        if (other == sid) continue;
                        var dep = await _symbols.GetByIdAsync(other, ct).ConfigureAwait(false);
                        if (dep is not null)
                            Add(await _files.GetByIdAsync(dep.FileId, ct).ConfigureAwait(false));
                    }
                }
                catch
                {
                    // Relations unavailable — degrade to just the target file.
                }
            }
            else if (!string.IsNullOrWhiteSpace(filePath))
            {
                Add(await _files.GetByPathAsync(repositoryId, filePath!, ct).ConfigureAwait(false));
            }
        }
        else
        {
            var prefix = targetType == DocumentationTargetType.Module
                ? Models.DocumentationTarget.NormalizePath(targetRef ?? filePath)
                : null;

            foreach (var f in await _files.GetByRepositoryAsync(repositoryId, ct).ConfigureAwait(false))
                if (prefix is null || MatchesModule(f.Path, prefix))
                    Add(f);
        }

        if (byId.Count == 0) return null;
        return HashFileSet(byId.Values.ToList());
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ComputeManyAsync(
        Guid repositoryId, IEnumerable<CodeRag.Domain.Entities.Documentation> existing, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var doc in existing)
        {
            var hash = await ComputeAsync(
                repositoryId, doc.TargetType, doc.TargetRef, doc.TargetId, doc.FilePath, ct).ConfigureAwait(false);
            if (hash is not null) result[doc.Id] = hash;
        }
        return result;
    }

    private static bool MatchesModule(string path, string prefix)
    {
        var p = path.Replace('\\', '/');
        if (prefix is "" or "root") return true;
        return p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            || p.Equals(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? HashFileSet(IReadOnlyList<FileEntry> files)
    {
        var relevant = files
            .Where(f => f.Hash is not null)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        if (relevant.Count == 0) return null;

        var input = new StringBuilder();
        for (int i = 0; i < relevant.Count; i++)
        {
            if (i > 0) input.Append('\n');
            input.Append(relevant[i].Path).Append(':').Append(relevant[i].Hash);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
