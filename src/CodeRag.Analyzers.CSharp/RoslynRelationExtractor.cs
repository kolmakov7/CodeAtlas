using CodeRag.Analyzers.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeRag.Analyzers.CSharp;

/// <summary>
/// Целонаборный (whole-repository) экстрактор C# связей через Roslyn.
/// Компилирует все C# файлы репозитория в единую CSharpCompilation, извлекает
/// структурные связи (Contains/Inherits/Implements/DependsOn/Calls/Overrides/References)
/// и сопоставляет концы связей с уже сохранёнными символами репозитория (ISymbolCatalog).
/// References = любое семантическое обращение к символу (типы, поля, вызовы, объекты...)
/// из декларации символа-источника. Error-символы (неразрешённые внешние типы) игнорируются.
/// Возвращает только связи, чьи оба конца реально существуют в каталоге.
/// </summary>
public sealed class RoslynRelationExtractor : IRelationExtractor
{
    private readonly ISymbolCatalog _catalog;

    public RoslynRelationExtractor(ISymbolCatalog catalog) => _catalog = catalog;

    public string Language => SupportedLanguages.CSharp;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> BclRefsLazy = new(GetBclReferencesInternal, true);
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private IReadOnlyList<MetadataReference> Refs => BclRefsLazy.Value;

    private CSharpCompilation? _compilation;
    private Dictionary<string, List<CatalogSymbol>> _byFqn = new(StringComparer.Ordinal);
    private Dictionary<string, List<CatalogSymbol>> _byNormFqn = new(StringComparer.Ordinal);
    private Dictionary<(string Type, string Name, int Arity), List<CatalogSymbol>> _byMethodKey = new(new KeyComparer());

    public async Task<IReadOnlyList<Relation>> ExtractAsync(RelationExtractionContext context, CancellationToken ct = default)
    {
        var files = context.Files.Where(f => !string.IsNullOrWhiteSpace(f.Content)).ToList();
        if (files.Count == 0) return Array.Empty<Relation>();

        var catalog = await _catalog.GetSymbolsAsync(context.RepositoryId, null, ct).ConfigureAwait(false);
        BuildIndexes(catalog);

        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Content, path: f.Path)).ToList();
        _compilation = CSharpCompilation.Create(
            "CodeRag_Relations_" + Guid.NewGuid().ToString("N"),
            trees,
            Refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithOverflowChecks(false));

        var seen = new HashSet<Relation>();
        var relations = new List<Relation>();
        void AddRel(Guid from, Guid to, string type)
        {
            if (from == Guid.Empty || to == Guid.Empty || from == to) return;
            var rel = new Relation(from, to, type);
            if (seen.Add(rel)) relations.Add(rel);
        }

        var visitedNs = new HashSet<INamespaceSymbol>();
        void WalkNamespace(INamespaceSymbol ns)
        {
            if (!visitedNs.Add(ns)) return;
            foreach (var m in ns.GetMembers())
            {
                if (m is INamedTypeSymbol t) ProcessType(t, AddRel);
                else if (m is INamespaceSymbol sub && !visitedNs.Contains(sub)) WalkNamespace(sub);
            }
        }

        try
        {
            WalkNamespace(_compilation.GlobalNamespace);
        }
        catch
        {
            // Leave whatever was extracted; never fail the indexing pipeline.
        }

        return relations;
    }

    private void ProcessType(INamedTypeSymbol t, Action<Guid, Guid, string> add)
    {
        var selfId = ResolveSymbol(t);
        if (selfId is not null)
        {
            // Inherits (base class)
            var baseType = t.BaseType;
            if (baseType is not null && !IsObject(baseType))
            {
                var baseId = ResolveSymbol(baseType);
                if (baseId is not null) add(selfId.Value, baseId.Value, RelationType.Inherits);
            }

            // Implements (declared interfaces)
            foreach (var iface in t.Interfaces)
            {
                var ifaceId = ResolveSymbol(iface);
                if (ifaceId is not null) add(selfId.Value, ifaceId.Value, RelationType.Implements);
            }

            // References from the type declaration itself (attributes, base/interface lists, type param defaults)
            EmitTypeHeadReferences(selfId.Value, t, add);

            foreach (var member in t.GetMembers().Where(m => m.ContainingType == t && m.Locations.Any(l => l.IsInSource)))
            {
                // Contains (type -> member)
                var memberId = ResolveSymbol(member);
                if (memberId is null) continue;
                add(selfId.Value, memberId.Value, RelationType.Contains);

                // DependsOn (type -> referenced user types from member declarations)
                EmitDependsOn(selfId.Value, member, add);

                // References (member -> every symbol referenced in its declaration: types, fields, calls, ...)
                EmitReferences(memberId.Value, member, add);
            }
        }

        // Calls / Overrides for methods (resolved by the method's own FQN, even if the type id is absent).
        foreach (var method in t.GetMembers().Where(m => m is IMethodSymbol && m.ContainingType == t && m.Locations.Any(l => l.IsInSource)))
        {
            var methodId = ResolveSymbol(method);
            if (methodId is null) continue;
            EmitCalls(methodId.Value, (IMethodSymbol)method, add);
            EmitOverride(methodId.Value, (IMethodSymbol)method, add);
        }
    }

    private void EmitDependsOn(Guid selfId, ISymbol member, Action<Guid, Guid, string> add)
    {
        var types = new List<ISymbol?>();
        switch (member)
        {
            case IFieldSymbol f:
                types.Add(f.Type);
                break;
            case IPropertySymbol p:
                types.Add(p.Type);
                break;
            case IMethodSymbol m:
                types.Add(m.ReturnType);
                foreach (var pr in m.Parameters) types.Add(pr.Type);
                break;
            default:
                return;
        }
        foreach (var t in types) CollectTypeDependencies(t, selfId, add);
    }

    private void CollectTypeDependencies(ISymbol? type, Guid selfId, Action<Guid, Guid, string> add)
    {
        if (type is null) return;
        if (type is IArrayTypeSymbol arr) { CollectTypeDependencies(arr.ElementType, selfId, add); return; }
        if (type is IPointerTypeSymbol ptr) { CollectTypeDependencies(ptr.PointedAtType, selfId, add); return; }
        if (type is INamedTypeSymbol nts)
        {
            var id = ResolveSymbol(nts);
            if (id is not null && id.Value != selfId) add(selfId, id.Value, RelationType.DependsOn);
            foreach (var ga in nts.TypeArguments) CollectTypeDependencies(ga, selfId, add);
        }
    }

    private void EmitCalls(Guid methodId, IMethodSymbol method, Action<Guid, Guid, string> add)
    {
        if (_compilation is null) return;
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            SyntaxNode node;
            try { node = declRef.GetSyntax(); }
            catch { continue; }

            var model = _compilation.GetSemanticModel(node.SyntaxTree);
            foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var target = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (target is null) continue;
                var targetId = ResolveSymbol(target);
                if (targetId is not null) add(methodId, targetId.Value, RelationType.Calls);
            }
        }
    }

    private void EmitOverride(Guid methodId, IMethodSymbol method, Action<Guid, Guid, string> add)
    {
        if (!method.IsOverride) return;
        var overriden = method.OverriddenMethod;
        if (overriden is null) return;
        var id = ResolveSymbol(overriden);
        if (id is not null) add(methodId, id.Value, RelationType.Overrides);
    }

    /// <summary>References (member -> referenced symbol) for every resolved semantic reference in the member's declaration.</summary>
    private void EmitReferences(Guid selfId, ISymbol member, Action<Guid, Guid, string> add)
    {
        var decl = GetSourceSyntax(member);
        if (decl is null) return;
        foreach (var node in decl.DescendantNodes())
        {
            var sym = GetReferencedSymbol(node);
            if (sym is null || IsErrorSymbol(sym)) continue;
            var id = ResolveSymbol(sym);
            if (id is not null && id.Value != selfId) add(selfId, id.Value, RelationType.References);
        }
    }

    /// <summary>References for the type declaration head: attributes, base/interface lists, type parameter constraints.</summary>
    private void EmitTypeHeadReferences(Guid selfId, INamedTypeSymbol t, Action<Guid, Guid, string> add)
    {
        var decl = GetSourceSyntax(t);
        if (decl is not TypeDeclarationSyntax tds) return;
        // BaseList also carries type-parameter constraints (`where T : X`), so it covers all head references.
        var heads = new List<SyntaxNode>();
        heads.AddRange(tds.AttributeLists);
        if (tds.BaseList is not null) heads.Add(tds.BaseList);
        foreach (var head in heads)
        {
            foreach (var node in head.DescendantNodesAndSelf())
            {
                var sym = GetReferencedSymbol(node);
                if (sym is null || IsErrorSymbol(sym)) continue;
                var id = ResolveSymbol(sym);
                if (id is not null && id.Value != selfId) add(selfId, id.Value, RelationType.References);
            }
        }
    }

    private static SyntaxNode? GetSourceSyntax(ISymbol symbol)
    {
        foreach (var sr in symbol.DeclaringSyntaxReferences)
        {
            try
            {
                // All trees in this compilation are source trees (whole-repo parse).
                return sr.GetSyntax();
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private ISymbol? GetReferencedSymbol(SyntaxNode node)
    {
        if (_compilation is null) return null;
        var model = _compilation.GetSemanticModel(node.SyntaxTree);
        switch (node)
        {
            case IdentifierNameSyntax:
            case GenericNameSyntax:
            case QualifiedNameSyntax:
            case AliasQualifiedNameSyntax:
            case MemberAccessExpressionSyntax:
            case MemberBindingExpressionSyntax:
                return model.GetSymbolInfo(node).Symbol;
            case BaseObjectCreationExpressionSyntax: // covers new X(...), new base(...), new()
            case TypeOfExpressionSyntax:
            case SizeOfExpressionSyntax:
            case RefTypeSyntax:
            case DefaultExpressionSyntax:
                return model.GetTypeInfo(node).Type;
            case AttributeSyntax a:
                return model.GetSymbolInfo(a.Name).Symbol;
            default:
                return null;
        }
    }

    /// <summary>Error symbols (unresolved types) render as bare names and must never resolve to catalog symbols.</summary>
    private static bool IsErrorSymbol(ISymbol? s)
        => s is not null && s.GetType().Name == "ErrorTypeSymbol";

    private Guid? ResolveSymbol(ISymbol s)
    {
        if (s is null || IsErrorSymbol(s)) return null;
        if (s is IMethodSymbol m) return ResolveMethod(m);
        if (s is INamedTypeSymbol nt) return ResolveFqn(nt.ToDisplayString(FqnFormat));
        if (s is IPropertySymbol or IFieldSymbol or IEventSymbol)
            return ResolveFqn(MemberFqn(s));
        return ResolveFqn(s.ToDisplayString(FqnFormat));
    }

    /// <summary>FQN for member symbols: `ContainingTypeFQN.MemberName` (member symbols render as bare names under FullyQualifiedFormat).</summary>
    private static string MemberFqn(ISymbol member)
    {
        if (member.ContainingType is INamedTypeSymbol ct)
        {
            var ctFqn = ct.ToDisplayString(FqnFormat);
            if (!string.IsNullOrWhiteSpace(ctFqn) && ctFqn != "global::<global namespace>")
                return $"{ctFqn}.{member.Name}";
        }
        return member.ToDisplayString(FqnFormat);
    }

    private Guid? ResolveMethod(IMethodSymbol m)
    {
        var fqn = MethodFqn(m);
        var byFqn = ResolveFqn(fqn);
        if (byFqn is not null) return byFqn;

        var ctFqn = m.ContainingType?.ToDisplayString(FqnFormat);
        if (string.IsNullOrWhiteSpace(ctFqn)) return null;
        var key = (Normalize(ctFqn), m.Name, m.Parameters.Length);
        if (_byMethodKey.TryGetValue(key, out var list) && list.Count == 1) return list[0].Id;
        return null;
    }

    private Guid? ResolveFqn(string? fqn)
    {
        if (string.IsNullOrWhiteSpace(fqn)) return null;
        if (_byFqn.TryGetValue(fqn, out var l1) && l1.Count == 1) return l1[0].Id;
        var nk = Normalize(fqn);
        if (_byNormFqn.TryGetValue(nk, out var l2) && l2.Count == 1) return l2[0].Id;
        return null;
    }

    private static string MethodFqn(IMethodSymbol m)
    {
        var containingTypeFqn = m.ContainingType?.ToDisplayString(FqnFormat) ?? "";
        var paramList = string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(FqnFormat)));
        return string.IsNullOrEmpty(containingTypeFqn) || containingTypeFqn == "global::<global namespace>"
            ? $"{m.Name}({paramList})"
            : $"{containingTypeFqn}.{m.Name}({paramList})";
    }

    private void BuildIndexes(IReadOnlyList<CatalogSymbol> catalog)
    {
        _byFqn = new Dictionary<string, List<CatalogSymbol>>(StringComparer.Ordinal);
        _byNormFqn = new Dictionary<string, List<CatalogSymbol>>(StringComparer.Ordinal);
        _byMethodKey = new Dictionary<(string, string, int), List<CatalogSymbol>>(new KeyComparer());

        foreach (var c in catalog)
        {
            if (string.IsNullOrWhiteSpace(c.Fqn)) continue;
            AddList(_byFqn, c.Fqn, c);
            AddList(_byNormFqn, Normalize(c.Fqn), c);
            var mk = ParseMethodKey(c.Fqn);
            if (mk is not null) AddList(_byMethodKey, mk.Value, c);
        }
    }

    private static void AddList<TKey, T>(Dictionary<TKey, List<T>> dict, TKey key, T value)
    {
        if (!dict.TryGetValue(key, out var list)) { list = new List<T>(); dict[key] = list; }
        list.Add(value);
    }

    private static (string Type, string Name, int Arity)? ParseMethodKey(string fqn)
    {
        int paren = fqn.IndexOf('(');
        if (paren < 0) return null;
        string head = fqn[..paren];
        int dot = head.LastIndexOf('.');
        string type = dot > 0 ? head[..dot] : "";
        string name = dot > 0 ? head[(dot + 1)..] : head;
        int close = fqn.LastIndexOf(')');
        string paramsText = close > paren + 1 ? fqn[(paren + 1)..close] : "";
        return (Normalize(type), name, CountArity(paramsText));
    }

    private static int CountArity(string paramsText)
    {
        if (string.IsNullOrWhiteSpace(paramsText)) return 0;
        int depth = 0, commas = 0;
        foreach (var c in paramsText)
        {
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) commas++;
        }
        return commas + 1;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var x = s;
        if (x.StartsWith("global::")) x = x["global::".Length..];
        x = x.Replace("<global namespace>", "").Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
        return x.ToLowerInvariant();
    }

    private static bool IsObject(INamedTypeSymbol t)
        => t.SpecialType == SpecialType.System_Object
           || (t.Name == "object" && t.ContainingNamespace.IsGlobalNamespace);

    private sealed class KeyComparer : IEqualityComparer<(string Type, string Name, int Arity)>
    {
        public bool Equals((string Type, string Name, int Arity) a, (string Type, string Name, int Arity) b)
            => a.Type == b.Type && a.Name == b.Name && a.Arity == b.Arity;
        public int GetHashCode((string Type, string Name, int Arity) obj)
            => HashCode.Combine(obj.Type, obj.Name, obj.Arity);
    }

    private static IReadOnlyList<MetadataReference> GetBclReferencesInternal()
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MetadataReference>();
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !refs.Add(path)) return;
            try { result.Add(MetadataReference.CreateFromFile(path)); } catch { /* ignore */ }
        }

        Add(typeof(object).Assembly.Location);
        Add(typeof(Console).Assembly.Location);
        Add(typeof(Enumerable).Assembly.Location);
        Add(typeof(List<>).Assembly.Location);
        Add(typeof(System.Runtime.GCSettings).Assembly.Location);

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            foreach (var dll in new[] { "System.Runtime.dll", "System.Collections.dll", "netstandard.dll", "System.Private.CoreLib.dll", "System.Linq.dll", "System.Threading.dll", "System.Collections.Immutable.dll", "System.Memory.dll" })
                Add(Path.Combine(runtimeDir, dll));
        }

        var tpa = (string?)AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var p in tpa.Split(Path.PathSeparator))
            {
                if (p.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("System.Collections.dll", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("System.Linq.dll", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("System.Console.dll", StringComparison.OrdinalIgnoreCase))
                    Add(p);
            }
        }

        if (result.Count == 0) Add(typeof(object).Assembly.Location);
        return result;
    }
}
