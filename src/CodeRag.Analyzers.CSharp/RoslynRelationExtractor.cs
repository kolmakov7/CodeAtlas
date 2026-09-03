using System.Collections.Concurrent;
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
/// Обработка типов параллельна по потокам (semantic model на дерево строится один раз);
/// все состояние-на-запуске живут в Scope — экземпляр переиспользуемый и thread-safe.
/// </summary>
public sealed class RoslynRelationExtractor : IRepositoryRelationExtractor
{
    private readonly ISymbolCatalog _catalog;

    public RoslynRelationExtractor(ISymbolCatalog catalog) => _catalog = catalog;

    public string Language => SupportedLanguages.CSharp;

    // --- Фильтр путей: что относится к C# (жизненный цикл файла — за этим экстрактором) ---

    private const string Extension = ".cs";
    private const string ScriptExtension = ".csx";

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", "packages", ".nuget", "dist", "build", ".idea", ".gradle"
    };

    public bool IsRelevantPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.GetExtension(path) is not (Extension or ScriptExtension)) return false;
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.Length > 0 && ExcludedDirs.Contains(part)) return false;
        return true;
    }

    private static readonly Lazy<IReadOnlyList<MetadataReference>> BclRefsLazy = new(GetBclReferencesInternal, true);
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private static IReadOnlyList<MetadataReference> Refs => BclRefsLazy.Value;

    // Первый запуск в процессе платит JIT-компиляцией байндера Roslyn (интерпретируемые
    // name-lookup методы). Одноразовая синтетическая extraction прогревает горячий путь.
    private static readonly Lazy<bool> BinderPrewarm = new(() => { PrewarmBinder(); return true; }, true);

    public async Task<IReadOnlyList<Relation>> ExtractAsync(
        RelationExtractionContext context,
        CancellationToken ct = default,
        IProgress<RelationExtractionProgress>? progress = null)
    {
        _ = BinderPrewarm.Value;

        var files = context.Files.Where(f => !string.IsNullOrWhiteSpace(f.Content)).ToList();
        if (files.Count == 0) return Array.Empty<Relation>();

        var catalog = await _catalog.GetSymbolsAsync(context.RepositoryId, null, ct).ConfigureAwait(false);

        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Content, path: f.Path)).ToList();

        var scope = new Scope
        {
            Compilation = CSharpCompilation.Create(
                "CodeRag_Relations_" + Guid.NewGuid().ToString("N"),
                trees,
                Refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(true)
                    .WithNullableContextOptions(NullableContextOptions.Enable)
                    .WithOverflowChecks(false))
        };
        BuildIndexes(scope, catalog);

        // Работа по типам (мелкое зерно): каждое дерево обрабатывается своим semantic model (один bind на дерево).
        var typesPerTree = new List<TypeDeclarationSyntax>[trees.Count];
        var totalItems = 0;
        for (var i = 0; i < trees.Count; i++)
        {
            typesPerTree[i] = trees[i].GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
            totalItems += typesPerTree[i].Count;
        }

        var visitedTypes = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
        var treeDone = new int[trees.Count];
        var filesDone = 0;

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, trees.Count).SelectMany(i => typesPerTree[i].Select(n => (Tree: i, Node: n))),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = ct },
                async (item, _) =>
                {
                    // Bind дерева один раз (кэш в scope); GetDeclaredSymbol дёшево.
                    var model = scope.GetModel(trees[item.Tree]);
                    if (model.GetDeclaredSymbol(item.Node) is not INamedTypeSymbol type) return;
                    if (!visitedTypes.TryAdd(type, 0)) return; // partial: обрабатываем один раз
                    try
                    {
                        ProcessType(scope, type);
                    }
                    catch
                    {
                        // Один сломанный тип не роняет весь прогон — сохраняем извлечённое.
                    }

                    if (Interlocked.Increment(ref treeDone[item.Tree]) == typesPerTree[item.Tree].Count)
                    {
                        var done = Interlocked.Increment(ref filesDone);
                        progress?.Report(new RelationExtractionProgress(trees.Count, done, files[item.Tree].Path));
                    }
                });
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Leave whatever was extracted; never fail the indexing pipeline.
        }

        return scope.Relations.Keys.ToList();
    }

    private void ProcessType(Scope scope, INamedTypeSymbol t)
    {
        var selfId = ResolveSymbol(scope, t);
        if (selfId is not null)
        {
            // Inherits (base class)
            var baseType = t.BaseType;
            if (baseType is not null && !IsObject(baseType))
            {
                var baseId = ResolveSymbol(scope, baseType);
                if (baseId is not null) scope.AddRel(selfId.Value, baseId.Value, RelationType.Inherits);
            }

            // Implements (declared interfaces)
            foreach (var iface in t.Interfaces)
            {
                var ifaceId = ResolveSymbol(scope, iface);
                if (ifaceId is not null) scope.AddRel(selfId.Value, ifaceId.Value, RelationType.Implements);
            }

            // References from the type declaration itself (attributes, base/interface lists, type param defaults)
            EmitTypeHeadReferences(scope, selfId.Value, t);

            foreach (var member in t.GetMembers().Where(m => m.ContainingType == t && m.Locations.Any(l => l.IsInSource)))
            {
                // Contains (type -> member)
                var memberId = ResolveSymbol(scope, member);
                if (memberId is null) continue;
                scope.AddRel(selfId.Value, memberId.Value, RelationType.Contains);

                // DependsOn (type -> referenced user types from member declarations)
                EmitDependsOn(scope, selfId.Value, member);

                // References (member -> every symbol referenced in its declaration: types, fields, calls, ...)
                EmitReferences(scope, memberId.Value, member);
            }
        }

        // Calls / Overrides for methods (resolved by the method's own FQN, even if the type id is absent).
        foreach (var method in t.GetMembers().Where(m => m is IMethodSymbol && m.ContainingType == t && m.Locations.Any(l => l.IsInSource)))
        {
            var methodId = ResolveSymbol(scope, method);
            if (methodId is null) continue;
            EmitCalls(scope, methodId.Value, (IMethodSymbol)method);
            EmitOverride(scope, methodId.Value, (IMethodSymbol)method);
        }
    }

    private void EmitDependsOn(Scope scope, Guid selfId, ISymbol member)
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
        foreach (var t in types) CollectTypeDependencies(scope, t, selfId);
    }

    private void CollectTypeDependencies(Scope scope, ISymbol? type, Guid selfId)
    {
        if (type is null) return;
        if (type is IArrayTypeSymbol arr) { CollectTypeDependencies(scope, arr.ElementType, selfId); return; }
        if (type is IPointerTypeSymbol ptr) { CollectTypeDependencies(scope, ptr.PointedAtType, selfId); return; }
        if (type is INamedTypeSymbol nts)
        {
            var id = ResolveSymbol(scope, nts);
            if (id is not null && id.Value != selfId) scope.AddRel(selfId, id.Value, RelationType.DependsOn);
            foreach (var ga in nts.TypeArguments) CollectTypeDependencies(scope, ga, selfId);
        }
    }

    private void EmitCalls(Scope scope, Guid methodId, IMethodSymbol method)
    {
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            SyntaxNode node;
            try { node = declRef.GetSyntax(); }
            catch { continue; }

            var model = scope.GetModel(node.SyntaxTree);
            foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var target = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (target is null) continue;
                var targetId = ResolveSymbol(scope, target);
                if (targetId is not null) scope.AddRel(methodId, targetId.Value, RelationType.Calls);
            }
        }
    }

    private void EmitOverride(Scope scope, Guid methodId, IMethodSymbol method)
    {
        if (!method.IsOverride) return;
        var overriden = method.OverriddenMethod;
        if (overriden is null) return;
        var id = ResolveSymbol(scope, overriden);
        if (id is not null) scope.AddRel(methodId, id.Value, RelationType.Overrides);
    }

    /// <summary>References (member -> referenced symbol) for every resolved semantic reference in the member's declaration.</summary>
    private void EmitReferences(Scope scope, Guid selfId, ISymbol member)
    {
        var decl = GetSourceSyntax(member);
        if (decl is null) return;
        WalkReferences(scope, selfId, decl);
    }

    /// <summary>
    /// Ходит по поддереву декларации, не спускаясь во вложенные типы:
    /// их связи извлекает собственный ProcessType (без этого тело вложенного класса
    /// пересчитывалось бы для каждого внешнего члена).
    /// </summary>
    private void WalkReferences(Scope scope, Guid selfId, SyntaxNode node)
    {
        var sym = GetReferencedSymbol(scope, node);
        if (sym is not null && !IsErrorSymbol(sym))
        {
            var id = ResolveSymbol(scope, sym);
            if (id is not null && id.Value != selfId) scope.AddRel(selfId, id.Value, RelationType.References);
        }

        foreach (var child in node.ChildNodes())
        {
            if (child is TypeDeclarationSyntax) continue;
            WalkReferences(scope, selfId, child);
        }
    }

    /// <summary>References for the type declaration head: attributes, base/interface lists, type parameter constraints.</summary>
    private void EmitTypeHeadReferences(Scope scope, Guid selfId, INamedTypeSymbol t)
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
                var sym = GetReferencedSymbol(scope, node);
                if (sym is null || IsErrorSymbol(sym)) continue;
                var id = ResolveSymbol(scope, sym);
                if (id is not null && id.Value != selfId) scope.AddRel(selfId, id.Value, RelationType.References);
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

    private static ISymbol? GetReferencedSymbol(Scope scope, SyntaxNode node)
    {
        var model = scope.GetModel(node.SyntaxTree);
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

    private static void PrewarmBinder()
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(PrewarmSource);
            var compilation = CSharpCompilation.Create(
                "CodeRag_Prewarm",
                new[] { tree },
                Refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var model = compilation.GetSemanticModel(tree);
            // Две прогонки по тем же узлам/вызовам, что и основная extraction,
            // чтобы горячие методы байндера прошли порог JIT-промоушена.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var node in tree.GetRoot().DescendantNodes())
                    GetReferencedSymbol(new Scope { Compilation = compilation }, node);
            }
        }
        catch { /* prewarm is best-effort */ }
    }

    private const string PrewarmSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Prewarm
        {
            public interface IHandler<in T>
            {
                Task HandleAsync(T item, CancellationToken ct);
            }

            public record ItemDto(Guid Id, string Name, DateTimeOffset CreatedAtUtc);

            public class BaseService
            {
                protected Guid SelfId { get; set; }
                protected Dictionary<string, object> Cache { get; } = new();

                public virtual Guid Resolve(Guid? id) => id ?? Guid.NewGuid();
                public virtual async Task<string> RunAsync(ItemDto item)
                {
                    var key = $"id:{item.Id}";
                    if (Cache.TryGetValue(key, out var cached) && cached is string s) return s;
                    var result = FormatName(item.Name, item.CreatedAtUtc);
                    Cache[key] = result;
                    await Task.CompletedTask;
                    return result;
                }

                private static string FormatName(string name, DateTimeOffset at)
                    => name.Length > 32 ? name[..32] : name;
            }

            [Serializable]
            public class ItemService : BaseService, IHandler<ItemDto>
            {
                private readonly Func<Guid, ItemDto> _lookup;

                public ItemService(Func<Guid, ItemDto> lookup)
                {
                    _lookup = lookup;
                    SelfId = Guid.NewGuid();
                }

                public override Guid Resolve(Guid? id) => id ?? SelfId;

                public override async Task<string> RunAsync(ItemDto item)
                {
                    var found = _lookup(item.Id);
                    var list = new List<ItemDto> { found, item };
                    var first = list.FirstOrDefault(x => x.Id == item.Id);
                    return await base.RunAsync(first ?? item);
                }

                public async Task<int> CountAsync(IEnumerable<ItemDto> items)
                {
                    var count = 0;
                    foreach (var i in items)
                    {
                        if (i.Name.Length > 0) count++;
                        if (i.CreatedAtUtc < DateTimeOffset.UtcNow) _ = Resolve(i.Id);
                    }
                    return count;
                }
            }

            public static class Program
            {
                public static async Task Main()
                {
                    var service = new ItemService(id => new ItemDto(id, "sample", DateTimeOffset.UtcNow));
                    await service.RunAsync(new ItemDto(Guid.NewGuid(), "x", DateTimeOffset.UtcNow));
                    _ = service.Resolve(null);
                }
            }
        }
        """;

    /// <summary>Error symbols (unresolved types) render as bare names and must never resolve to catalog symbols.</summary>
    private static bool IsErrorSymbol(ISymbol? s)
        => s is not null && s.GetType().Name == "ErrorTypeSymbol";

    private Guid? ResolveSymbol(Scope scope, ISymbol s)
    {
        if (s is null || IsErrorSymbol(s)) return null;
        // Кэш на исполнение: горячие символы (string, Guid, частые DTO) разыгрываются один раз.
        return scope.SymbolIds.GetOrAdd(s, sym => ResolveSymbolCore(scope, sym));
    }

    private static Guid? ResolveSymbolCore(Scope scope, ISymbol s)
    {
        if (s is IMethodSymbol m) return ResolveMethod(scope, m);
        if (s is INamedTypeSymbol nt) return ResolveFqn(scope, nt.ToDisplayString(FqnFormat));
        if (s is IPropertySymbol or IFieldSymbol or IEventSymbol)
            return ResolveFqn(scope, MemberFqn(s));
        return ResolveFqn(scope, s.ToDisplayString(FqnFormat));
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

    private static Guid? ResolveMethod(Scope scope, IMethodSymbol m)
    {
        var fqn = MethodFqn(m);
        var byFqn = ResolveFqn(scope, fqn);
        if (byFqn is not null) return byFqn;

        var ctFqn = m.ContainingType?.ToDisplayString(FqnFormat);
        if (string.IsNullOrWhiteSpace(ctFqn)) return null;
        var key = (Normalize(ctFqn), m.Name, m.Parameters.Length);
        if (scope.ByMethodKey.TryGetValue(key, out var list) && list.Count == 1) return list[0].Id;
        return null;
    }

    private static Guid? ResolveFqn(Scope scope, string? fqn)
    {
        if (string.IsNullOrWhiteSpace(fqn)) return null;
        if (scope.ByFqn.TryGetValue(fqn, out var l1) && l1.Count == 1) return l1[0].Id;
        var nk = Normalize(fqn);
        if (scope.ByNormFqn.TryGetValue(nk, out var l2) && l2.Count == 1) return l2[0].Id;
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

    private static void BuildIndexes(Scope scope, IReadOnlyList<CatalogSymbol> catalog)
    {
        foreach (var c in catalog)
        {
            if (string.IsNullOrWhiteSpace(c.Fqn)) continue;
            AddList(scope.ByFqn, c.Fqn, c);
            AddList(scope.ByNormFqn, Normalize(c.Fqn), c);
            var mk = ParseMethodKey(c.Fqn);
            if (mk is not null) AddList(scope.ByMethodKey, mk.Value, c);
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

    /// <summary>Состояние одного прогона (экстрактор — синглтон в DI, параллельные прогоны не должны видеться).</summary>
    private sealed class Scope
    {
        public CSharpCompilation Compilation { get; init; } = null!;
        public ConcurrentDictionary<SyntaxTree, SemanticModel> Models { get; } = new();
        public ConcurrentDictionary<ISymbol, Guid?> SymbolIds { get; } = new(SymbolEqualityComparer.Default);
        public Dictionary<string, List<CatalogSymbol>> ByFqn { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<CatalogSymbol>> ByNormFqn { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Type, string Name, int Arity), List<CatalogSymbol>> ByMethodKey { get; } = new(new KeyComparer());
        public ConcurrentDictionary<Relation, byte> Relations { get; } = new();

        public SemanticModel GetModel(SyntaxTree tree) => Models.GetOrAdd(tree, t => Compilation.GetSemanticModel(t));

        public void AddRel(Guid from, Guid to, string type)
        {
            if (from == Guid.Empty || to == Guid.Empty || from == to) return;
            Relations.TryAdd(new Relation(from, to, type), 0);
        }
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
