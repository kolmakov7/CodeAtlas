using System.Collections.Concurrent;
using CodeRag.Analyzers.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace CodeRag.Analyzers.Sql;

public sealed class SqlRelationExtractor : IRepositoryRelationExtractor
{
    private readonly ISymbolCatalog _catalog;

    public SqlRelationExtractor(ISymbolCatalog catalog) => _catalog = catalog;

    public string Language => SupportedLanguages.Sql;

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase) { ".sql", ".tsql" };
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", "packages", ".nuget", "dist", "build", ".idea", ".gradle"
    };

    public bool IsRelevantPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (!Exts.Contains(ext)) return false;
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.Length > 0 && ExcludedDirs.Contains(part)) return false;
        return true;
    }

    public async Task<IReadOnlyList<Relation>> ExtractAsync(RelationExtractionContext context, CancellationToken ct = default, IProgress<RelationExtractionProgress>? progress = null)
    {
        var files = context.Files.Where(f => !string.IsNullOrWhiteSpace(f.Content) && IsRelevantPath(f.Path)).ToList();
        if (files.Count == 0) return Array.Empty<Relation>();

        var catalog = await _catalog.GetSymbolsAsync(context.RepositoryId, Language, ct).ConfigureAwait(false);
        if (catalog.Count == 0) return Array.Empty<Relation>();

        // Cross-repository fallback: symbols of linked repositories (e.g. a DB repo whose
        // objects are referenced from another DB repo). Local resolution wins.
        CatalogIndex? linkedIndex = null;
        if (context.LinkedRepositoryIds is { Count: > 0 })
        {
            var linked = await _catalog.GetSymbolsAsync(context.LinkedRepositoryIds, Language, ct).ConfigureAwait(false);
            if (linked.Count > 0) linkedIndex = new CatalogIndex(linked);
        }

        var index = new CatalogIndex(catalog, linkedIndex);
        var scope = new Scope();

        var total = files.Count;
        var done = 0;

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = ct }, (file, token) =>
        {
            try
            {
                var parser = new TSql170Parser(initialQuotedIdentifiers: true);
                using var reader = new StringReader(file.Content);
                var fragment = parser.Parse(reader, out var errors) as TSqlScript;
                if (fragment == null) return ValueTask.CompletedTask;

                // 1. Collect candidates with offsets and resolved ids
                var candidates = new List<Cand>();
                var collector = new CandidateCollector(candidates, index, file.Content);
                fragment.Accept(collector);

                // 2. Contains: parent contains child via offset containment
                foreach (var parent in candidates)
                {
                    if (parent.ResolvedId == Guid.Empty) continue;
                    foreach (var child in candidates)
                    {
                        if (ReferenceEquals(parent, child)) continue;
                        if (child.ResolvedId == Guid.Empty) continue;
                        if (child.StartOffset < 0 || parent.StartOffset < 0) continue;
                        if (child.StartOffset >= parent.StartOffset && child.EndOffset <= parent.EndOffset)
                        {
                            scope.AddRel(parent.ResolvedId, child.ResolvedId, RelationType.Contains);
                        }
                    }
                }

                // 3. Calls / References via visitor with caller stack
                var visitor = new CallVisitor(candidates, index, scope);
                fragment.Accept(visitor);
            }
            catch
            {
                // one file failure does not fail whole extraction
            }
            finally
            {
                var d = Interlocked.Increment(ref done);
                progress?.Report(new RelationExtractionProgress(total, d, file.Path));
            }
            return ValueTask.CompletedTask;
        });

        return scope.Relations.Keys.ToList();
    }

    private sealed class Cand
    {
        public TSqlFragment Node = null!;
        public string Fqn = "";
        public Guid ResolvedId = Guid.Empty;
        public int StartOffset = -1;
        public int EndOffset = -1;
    }

    private sealed class CatalogIndex
    {
        private readonly Dictionary<string, List<CatalogSymbol>> ByFqn = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CatalogSymbol>> ByNorm = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CatalogSymbol>> ByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly CatalogIndex? _fallback;

        public CatalogIndex(IReadOnlyList<CatalogSymbol> catalog, CatalogIndex? fallback = null)
        {
            _fallback = fallback;
            foreach (var c in catalog)
            {
                if (!string.IsNullOrWhiteSpace(c.Fqn))
                {
                    Add(ByFqn, c.Fqn, c);
                    Add(ByNorm, Normalize(c.Fqn), c);
                    var last = LastSeg(c.Fqn);
                    if (!string.IsNullOrWhiteSpace(last)) Add(ByName, last, c);
                }
                if (!string.IsNullOrWhiteSpace(c.Name))
                    Add(ByName, c.Name, c);
            }
        }
        private static void Add(Dictionary<string, List<CatalogSymbol>> d, string k, CatalogSymbol v)
        {
            if (!d.TryGetValue(k, out var l)) { l = new List<CatalogSymbol>(); d[k] = l; }
            if (!l.Any(x => x.Id == v.Id)) l.Add(v);
        }
        private static string Normalize(string s)
        {
            var x = s.Trim();
            if (x.StartsWith("[")) x = x.Replace("[", "").Replace("]", "");
            x = x.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
            return x.ToLowerInvariant();
        }
        private static string LastSeg(string fqn)
        {
            var idx = fqn.LastIndexOf('.');
            if (idx >= 0) return fqn[(idx + 1)..].Trim('[', ']');
            return fqn.Trim('[', ']');
        }
        public Guid? ResolveFqn(string? fqn)
        {
            if (string.IsNullOrWhiteSpace(fqn)) return null;
            if (ByFqn.TryGetValue(fqn, out var l1) && l1.Count > 0) return l1[0].Id;
            var n = Normalize(fqn);
            if (ByNorm.TryGetValue(n, out var l2) && l2.Count > 0) return l2[0].Id;
            // try bracket stripped
            var stripped = fqn.Replace("[", "").Replace("]", "");
            if (ByFqn.TryGetValue(stripped, out var l3) && l3.Count > 0) return l3[0].Id;
            if (ByNorm.TryGetValue(Normalize(stripped), out var l4) && l4.Count > 0) return l4[0].Id;
            return _fallback?.ResolveFqn(fqn);
        }
        public Guid? ResolveName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var clean = name.Replace("[", "").Replace("]", "").Trim();
            if (ByName.TryGetValue(clean, out var l1) && l1.Count > 0) return l1[0].Id;
            if (ByFqn.TryGetValue(clean, out var l2) && l2.Count > 0) return l2[0].Id;
            return _fallback?.ResolveName(name);
        }
    }

    private sealed class Scope
    {
        public ConcurrentDictionary<Relation, byte> Relations { get; } = new();
        public void AddRel(Guid from, Guid to, string type)
        {
            if (from == Guid.Empty || to == Guid.Empty || from == to) return;
            Relations.TryAdd(new Relation(from, to, type), 0);
        }
    }

    private sealed class CandidateCollector : TSqlFragmentVisitor
    {
        private readonly List<Cand> _cands;
        private readonly CatalogIndex _index;
        private readonly string _content;

        public CandidateCollector(List<Cand> cands, CatalogIndex index, string content)
        {
            _cands = cands;
            _index = index;
            _content = content;
        }

        private void Handle(TSqlFragment node, SchemaObjectName? nameObj, ProcedureReference? procRef)
        {
            try
            {
                string? name = null;
                string? schema = null;
                if (nameObj != null)
                {
                    name = nameObj.BaseIdentifier?.Value;
                    if (nameObj.SchemaIdentifier != null) schema = nameObj.SchemaIdentifier.Value;
                    else if (nameObj.Identifiers.Count == 2) schema = nameObj.Identifiers[0].Value;
                    if (string.IsNullOrWhiteSpace(name) && nameObj.Identifiers.Count > 0) name = nameObj.Identifiers[^1].Value;
                    if (string.IsNullOrWhiteSpace(schema) && nameObj.Identifiers.Count >= 2) schema = nameObj.Identifiers[^2].Value;
                }
                else if (procRef != null)
                {
                    var sname = procRef.Name;
                    if (sname != null)
                    {
                        name = sname.BaseIdentifier?.Value;
                        if (sname.SchemaIdentifier != null) schema = sname.SchemaIdentifier.Value;
                        else if (sname.Identifiers.Count == 2) schema = sname.Identifiers[0].Value;
                        if (string.IsNullOrWhiteSpace(name) && sname.Identifiers.Count > 0) name = sname.Identifiers[^1].Value;
                    }
                }
                if (string.IsNullOrWhiteSpace(name)) name = "unknown";
                var fqn = string.IsNullOrWhiteSpace(schema) ? name! : $"{schema}.{name}";
                var resolved = _index.ResolveFqn(fqn) ?? _index.ResolveName(name) ?? _index.ResolveName(fqn);
                var start = node.StartOffset;
                var len = node.FragmentLength;
                var valid = start >= 0 && len > 0 && start + len <= _content.Length;
                _cands.Add(new Cand
                {
                    Node = node,
                    Fqn = fqn,
                    ResolvedId = resolved ?? Guid.Empty,
                    StartOffset = valid ? start : -1,
                    EndOffset = valid ? start + len : -1
                });
            }
            catch { }
        }

        public override void ExplicitVisit(CreateProcedureStatement node) { Handle(node, null, node.ProcedureReference); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateFunctionStatement node) { Handle(node, node.Name, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateViewStatement node) { Handle(node, node.SchemaObjectName, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateTableStatement node) { Handle(node, node.SchemaObjectName, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateTriggerStatement node) { Handle(node, node.Name, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) { Handle(node, null, node.ProcedureReference); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) { Handle(node, node.Name, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateOrAlterViewStatement node) { Handle(node, node.SchemaObjectName, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) { Handle(node, node.Name, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(AlterProcedureStatement node) { Handle(node, null, node.ProcedureReference); base.ExplicitVisit(node); }
        public override void ExplicitVisit(AlterFunctionStatement node) { Handle(node, node.Name, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(AlterViewStatement node) { Handle(node, node.SchemaObjectName, null); base.ExplicitVisit(node); }
        public override void ExplicitVisit(AlterTriggerStatement node) { Handle(node, node.Name, null); base.ExplicitVisit(node); }
    }

    private sealed class CallVisitor : TSqlFragmentVisitor
    {
        private readonly List<Cand> _cands;
        private readonly CatalogIndex _index;
        private readonly Scope _scope;
        private readonly Stack<Guid> _stack = new();
        private int _execDepth;

        public CallVisitor(List<Cand> cands, CatalogIndex index, Scope scope)
        {
            _cands = cands;
            _index = index;
            _scope = scope;
        }

        private Guid? CurrentCaller => _stack.Count > 0 ? _stack.Peek() : null;

        private void PushIfCand(TSqlFragment node)
        {
            var cand = _cands.FirstOrDefault(c => ReferenceEquals(c.Node, node));
            if (cand != null && cand.ResolvedId != Guid.Empty) _stack.Push(cand.ResolvedId);
            else _stack.Push(Guid.Empty);
        }
        private void PopIfPushed() { if (_stack.Count > 0) _stack.Pop(); }

        public override void ExplicitVisit(CreateProcedureStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateFunctionStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateViewStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateTableStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateTriggerStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateOrAlterViewStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(AlterProcedureStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(AlterFunctionStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(AlterViewStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }
        public override void ExplicitVisit(AlterTriggerStatement node) { PushIfCand(node); base.ExplicitVisit(node); PopIfPushed(); }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            var caller = CurrentCaller;
            if (caller != null && caller != Guid.Empty)
            {
                try
                {
                    var entity = node.ExecuteSpecification?.ExecutableEntity;
                    if (entity is ExecutableProcedureReference pr)
                    {
                        var procName = pr.ProcedureReference?.ProcedureReference?.Name;
                        string? fqn = null;
                        string? name = null;
                        if (procName != null)
                        {
                            name = procName.BaseIdentifier?.Value;
                            var schema = procName.SchemaIdentifier?.Value;
                            if (string.IsNullOrWhiteSpace(name) && procName.Identifiers.Count > 0) name = procName.Identifiers[^1].Value;
                            if (string.IsNullOrWhiteSpace(schema) && procName.Identifiers.Count >= 2) schema = procName.Identifiers[^2].Value;
                            fqn = string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) ? name : $"{schema}.{name}";
                        }
                        var target = _index.ResolveFqn(fqn) ?? _index.ResolveName(name) ?? _index.ResolveName(fqn);
                        if (target != null) _scope.AddRel(caller.Value, target.Value, RelationType.Calls);
                    }
                }
                catch { }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var caller = CurrentCaller;
            if (caller != null && caller != Guid.Empty)
            {
                try
                {
                    var procName = node.ProcedureReference?.ProcedureReference?.Name;
                    string? fqn = null;
                    string? name = null;
                    if (procName != null)
                    {
                        name = procName.BaseIdentifier?.Value;
                        var schema = procName.SchemaIdentifier?.Value;
                        if (string.IsNullOrWhiteSpace(name) && procName.Identifiers.Count > 0) name = procName.Identifiers[^1].Value;
                        if (string.IsNullOrWhiteSpace(schema) && procName.Identifiers.Count >= 2) schema = procName.Identifiers[^2].Value;
                        fqn = string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) ? name : $"{schema}.{name}";
                        var target = _index.ResolveFqn(fqn) ?? _index.ResolveName(name);
                        if (target != null) _scope.AddRel(caller.Value, target.Value, RelationType.Calls);
                    }
                }
                catch { }
            }
            _execDepth++;
            base.ExplicitVisit(node);
            _execDepth--;
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            var caller = CurrentCaller;
            if (caller != null && caller != Guid.Empty)
            {
                try
                {
                    var sobj = node.SchemaObject;
                    if (sobj != null)
                    {
                        var name = sobj.BaseIdentifier?.Value;
                        if (string.IsNullOrWhiteSpace(name) && sobj.Identifiers.Count > 0) name = sobj.Identifiers[^1].Value;
                        var schema = sobj.SchemaIdentifier?.Value;
                        if (string.IsNullOrWhiteSpace(schema) && sobj.Identifiers.Count >= 2) schema = sobj.Identifiers[^2].Value;
                        var fqn = string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) ? name : $"{schema}.{name}";
                        var target = _index.ResolveFqn(fqn) ?? _index.ResolveName(name);
                        if (target != null && target.Value != caller.Value)
                            _scope.AddRel(caller.Value, target.Value, RelationType.References);
                    }
                }
                catch { }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectName node)
        {
            if (_execDepth > 0) { base.ExplicitVisit(node); return; }
            var caller = CurrentCaller;
            if (caller != null && caller != Guid.Empty && node.BaseIdentifier != null)
            {
                var name = node.BaseIdentifier.Value;
                var schema = node.SchemaIdentifier?.Value;
                var fqn = string.IsNullOrWhiteSpace(schema) ? name : $"{schema}.{name}";
                var selfFqn = _cands.FirstOrDefault(c => c.ResolvedId == caller.Value)?.Fqn;
                if (!string.Equals(fqn, selfFqn, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, selfFqn?.Split('.').Last(), StringComparison.OrdinalIgnoreCase))
                {
                    var target = _index.ResolveFqn(fqn) ?? _index.ResolveName(name);
                    if (target != null && target.Value != caller.Value)
                        _scope.AddRel(caller.Value, target.Value, RelationType.References);
                }
            }
            base.ExplicitVisit(node);
        }
    }
}
