using System.Collections.Concurrent;
using CodeRag.Analyzers.Abstractions;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter.Relations;

/// <summary>
/// Базовый whole-repo экстрактор для TreeSitter языков: Contains + Calls (cross-file Name-based).
/// </summary>
public abstract class BaseTreeSitterRelationExtractor : IRepositoryRelationExtractor
{
    private readonly ISymbolCatalog _catalog;

    protected BaseTreeSitterRelationExtractor(ISymbolCatalog catalog) => _catalog = catalog;

    public abstract string Language { get; }

    // Переопределяет фильтр путей: расширения + excluded dirs
    public abstract bool IsRelevantPath(string path);

    // Имя TreeSitter грамматики для primary языка (например "Python")
    protected abstract string PrimaryTreeSitterName { get; }

    // Допустимые расширения для этого языка (например .py/.pyw/.pyi)
    protected abstract IReadOnlySet<string> AllowedExtensions { get; }

    // Для языков с несколькими грамматиками (JS/TS) — маппит путь в TreeSitter имя
    protected virtual string? GetTreeSitterLanguageForFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (AllowedExtensions.Contains(ext)) return PrimaryTreeSitterName;
        return null;
    }

    public async Task<IReadOnlyList<Relation>> ExtractAsync(RelationExtractionContext context, CancellationToken ct = default, IProgress<RelationExtractionProgress>? progress = null)
    {
        var files = context.Files.Where(f => !string.IsNullOrWhiteSpace(f.Content) && IsRelevantPath(f.Path)).ToList();
        if (files.Count == 0) return Array.Empty<Relation>();

        var catalog = await _catalog.GetSymbolsAsync(context.RepositoryId, Language, ct).ConfigureAwait(false);
        if (catalog.Count == 0) return Array.Empty<Relation>();

        var index = new TreeSitterRelationHelpers.CatalogIndex(catalog);
        var scope = new Scope();

        // Загружаем Language инстансы per TreeSitterName (кеш на extractor)
        var langCache = new Dictionary<string, Language>(StringComparer.Ordinal);

        Language? GetOrLoad(string name)
        {
            if (langCache.TryGetValue(name, out var l)) return l;
            var loaded = TreeSitterRelationHelpers.TryLoadLanguage(name);
            if (loaded != null) langCache[name] = loaded;
            return loaded;
        }

        var total = files.Count;
        var done = 0;

        // Параллельно по файлам (как Roslyn)
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = ct }, (file, token) =>
        {
            var tsLang = GetTreeSitterLanguageForFile(file.Path);
            if (tsLang == null) { Interlocked.Increment(ref done); progress?.Report(new RelationExtractionProgress(total, Volatile.Read(ref done), file.Path)); return ValueTask.CompletedTask; }

            var language = GetOrLoad(tsLang);
            if (language == null) { Interlocked.Increment(ref done); progress?.Report(new RelationExtractionProgress(total, Volatile.Read(ref done), file.Path)); return ValueTask.CompletedTask; }

            try
            {
                using var parser = new Parser(language);
                using var tree = parser.Parse(file.Content);
                if (tree == null) return ValueTask.CompletedTask;
                var root = tree.RootNode;

                // Walk с локальным стеком
                var stack = new Stack<TempSymbol>();
                Walk(root, stack, file.Path, tsLang, index, scope, token);
            }
            catch
            {
                // Один битый файл не роняет весь прогон
            }
            finally
            {
                var d = Interlocked.Increment(ref done);
                progress?.Report(new RelationExtractionProgress(total, d, file.Path));
            }
            return ValueTask.CompletedTask;
        });

        // Освобождаем Language инстансы
        foreach (var l in langCache.Values) try { l.Dispose(); } catch { }

        return scope.Relations.Keys.ToList();
    }

    private sealed class TempSymbol
    {
        public string Name = "";
        public string Fqn = "";
        public string Kind = "";
        public int StartLine;
        public int EndLine;
        public Guid ResolvedId;
    }

    private void Walk(Node node, Stack<TempSymbol> stack, string filePath, string treeLang, TreeSitterRelationHelpers.CatalogIndex index, Scope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Определяем kind/name через те же handlers что и TreeSitterAnalyzer
        var kind = DetectKind(node, treeLang, stack);
        var name = DetectName(node, treeLang, kind);

        TempSymbol? created = null;
        Guid resolvedId = Guid.Empty;

        if (kind != null && !string.IsNullOrWhiteSpace(name))
        {
            string fqn;
            if (stack.Count > 0)
                fqn = string.Join(".", stack.Reverse().Select(s => s.Name)) + "." + name;
            else
                fqn = name!;

            var id = index.ResolveFqn(fqn) ?? index.ResolveByName(name);
            // Fallback: если FQN не нашёлся, пробуем нормализованный? ResolveFqn уже пробует норм.
            if (id != null)
            {
                resolvedId = id.Value;
                var tmp = new TempSymbol { Name = name!, Fqn = fqn, Kind = kind, StartLine = (int)node.StartPosition.Row + 1, EndLine = (int)node.EndPosition.Row + 1, ResolvedId = resolvedId };

                // Contains: parent -> child
                if (stack.Count > 0)
                {
                    var parent = stack.Peek();
                    if (parent.ResolvedId != Guid.Empty && parent.ResolvedId != resolvedId)
                        scope.AddRel(parent.ResolvedId, resolvedId, RelationType.Contains);
                }

                stack.Push(tmp);
                created = tmp;
            }
            else
            {
                // Если символ из файла не в каталоге (фильтрация или расхождение FQN) — всё равно пушим synthetic чтобы Calls внутри нашли caller via stack name, но без Id не эмитим Contains
                var tmp = new TempSymbol { Name = name!, Fqn = fqn, Kind = kind, StartLine = (int)node.StartPosition.Row + 1, EndLine = (int)node.EndPosition.Row + 1, ResolvedId = Guid.Empty };
                stack.Push(tmp);
                created = tmp;
            }
        }

        // Calls: call / call_expression
        if (node.Type is "call" or "call_expression")
        {
            var (callee, qualifier) = TreeSitterRelationHelpers.ExtractCallInfo(node);
            if (!string.IsNullOrWhiteSpace(callee))
            {
                var caller = stack.FirstOrDefault(s => s.ResolvedId != Guid.Empty && (s.Kind == SymbolKind.Function || s.Kind == SymbolKind.Method || s.Kind == SymbolKind.Class));
                // fallback: любой resolved в стеке
                caller ??= stack.FirstOrDefault(s => s.ResolvedId != Guid.Empty);
                if (caller != null)
                {
                    var targetId = index.ResolveCall(callee, qualifier);
                    if (targetId != null && targetId.Value != caller.ResolvedId)
                        scope.AddRel(caller.ResolvedId, targetId.Value, RelationType.Calls);
                }
            }
        }

        foreach (var child in node.Children)
            Walk(child, stack, filePath, treeLang, index, scope, ct);

        if (created != null) stack.Pop();
    }

    // Определяет kind по типу узла (копия NodeHandlers логики но без делегатов для производительности)
    private static string? DetectKind(Node node, string treeLang, Stack<TempSymbol> stack)
    {
        return node.Type switch
        {
            "class_definition" => SymbolKind.Class,
            "class_declaration" => SymbolKind.Class,
            "class_specifier" when treeLang == "C++" => SymbolKind.Class,
            "type_declaration" when treeLang == "Go" => SymbolKind.Class, // struct type
            "function_definition" when treeLang is "Python" or "C++" => (stack.Count > 0 && stack.Peek().Kind == SymbolKind.Class) ? SymbolKind.Method : SymbolKind.Function,
            "function_declaration" => SymbolKind.Function,
            "method_declaration" => SymbolKind.Method,
            "method_definition" => SymbolKind.Method,
            "arrow_function" => SymbolKind.Function,
            "function_expression" => SymbolKind.Function,
            _ => null
        };
    }

    private static string? DetectName(Node node, string treeLang, string? kind)
    {
        if (kind == null) return null;
        if (node.Type is "arrow_function")
        {
            var parentVar = node.Parent?.Type == "variable_declarator" ? TreeSitterRelationHelpers.ExtractName(node.Parent!, treeLang) : null;
            return parentVar ?? "arrow";
        }
        if (node.Type == "function_expression")
        {
            var parentVar = node.Parent?.Type == "variable_declarator" ? TreeSitterRelationHelpers.ExtractName(node.Parent!, treeLang) : null;
            return parentVar ?? "anonymous";
        }
        var n = TreeSitterRelationHelpers.ExtractName(node, treeLang);
        if (string.IsNullOrWhiteSpace(n) && node.Type == "type_declaration" && treeLang == "Go")
        {
            // fallback already via ExtractName
        }
        return n;
    }

    private sealed class Scope
    {
        public ConcurrentDictionary<Relation, byte> Relations { get; } = new();
        public void AddRel(Guid from, Guid to, string type)
        {
            if (from == Guid.Empty || to == Guid.Empty || from == to) return;
            // Domain validation: Contains allows self? но мы уже исключили
            Relations.TryAdd(new Relation(from, to, type), 0);
        }
    }
}


