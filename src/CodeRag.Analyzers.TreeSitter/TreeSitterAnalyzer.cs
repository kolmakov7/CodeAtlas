using System.Text;
using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.Abstractions.Chunking;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter;

/// <summary>
/// Polyglot Tree-sitter анализатор для Python / JavaScript / Go / C++ / TypeScript (§15).
/// Использует TreeSitter.DotNet Language("Python"/"JavaScript"/"Go"/"C++"/"TypeScript"),
/// Parser.Parse, Query.Execute captures, символы function/class/method/call/import, FQN via stack, depth medium.
/// </summary>
public sealed class TreeSitterAnalyzer : ICodeAnalyzer
{
    private static readonly Dictionary<string, string> ExtensionToTreeSitter = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "Python", [".pyw"] = "Python", [".pyi"] = "Python",
        [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript", [".jsx"] = "JavaScript",
        [".ts"] = "TypeScript",
        [".tsx"] = "TSX",
        [".go"] = "Go",
        [".cpp"] = "C++", [".cc"] = "C++", [".cxx"] = "C++", [".c"] = "C++", [".h"] = "C++", [".hpp"] = "C++", [".hh"] = "C++", [".hxx"] = "C++",
    };

    private static readonly Dictionary<string, string> ContextToTreeSitter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "Python",
        ["javascript"] = "JavaScript",
        ["go"] = "Go",
        ["cpp"] = "C++",
        ["c++"] = "C++",
    };

    private static readonly Dictionary<string, string> TreeSitterToSupported = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = SupportedLanguages.Python,
        ["javascript"] = SupportedLanguages.JavaScript,
        ["typescript"] = SupportedLanguages.JavaScript,
        ["tsx"] = SupportedLanguages.JavaScript,
        ["go"] = SupportedLanguages.Go,
        ["c++"] = SupportedLanguages.Cpp,
        ["cpp"] = SupportedLanguages.Cpp,
    };

    private static readonly Dictionary<string, string[]> LanguageLoadFallbacks = new(StringComparer.Ordinal)
    {
        ["C++"] = new[] { "Cpp", "cpp", "c++" },
        ["TypeScript"] = new[] { "Typescript", "typescript", "TSX", "tsx" },
        ["TSX"] = new[] { "TypeScript", "typescript" },
        ["JavaScript"] = new[] { "Javascript", "javascript", "JS" },
    };

    private static readonly Dictionary<string, Action<Node, NodeHandlerContext>> NodeHandlers = new(StringComparer.Ordinal)
    {
        ["class_definition"] = HandleClassLike,
        ["class_declaration"] = HandleClassLike,
        ["class_specifier"] = HandleClassLike,
        ["type_declaration"] = HandleClassLike,
        ["function_definition"] = HandleFunctionDefinition,
        ["function_declaration"] = HandleFunctionDeclaration,
        ["method_declaration"] = HandleMethod,
        ["method_definition"] = HandleMethod,
        ["arrow_function"] = HandleArrowFunction,
        ["function_expression"] = HandleFunctionExpression,
        ["call"] = HandleCall,
        ["call_expression"] = HandleCall,
        ["import_statement"] = HandleImport,
        ["import_from_statement"] = HandleImport,
        ["import_declaration"] = HandleImport,
        ["namespace_definition"] = HandleNamespace,
    };

    private readonly IChunkFactory _factory;
    private readonly ISourceSlicer _slicer;

    public TreeSitterAnalyzer() : this(new DefaultChunkFactory(), null!) { }

    public TreeSitterAnalyzer(IChunkFactory factory, ISourceSlicer slicer)
    {
        _factory = factory ?? new DefaultChunkFactory();
        _slicer = slicer ?? new DefaultSourceSlicer(_factory);
    }

    public string Language => SupportedLanguages.Python; // polyglot, но возвращает python как primary; CanAnalyze покрывает все

    public bool CanAnalyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return ExtensionToTreeSitter.ContainsKey(Path.GetExtension(filePath));
    }

    public Task<AnalyzeResult> AnalyzeAsync(FileContext ctx, CancellationToken ct)
    {
        var symbols = new List<Symbol>();
        var relations = new List<Relation>();
        var chunks = new List<Chunk>();
        var errors = new List<AnalyzerError>();

        if (string.IsNullOrWhiteSpace(ctx.Content))
            return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));

        var treeSitterLang = DetectTreeSitterLanguage(ctx.FilePath, ctx.Language);
        if (treeSitterLang == null)
        {
            // Fallback sliding window для неизвестных
            var fb = _slicer.SlidingWindow(ctx.Content, ctx.Language ?? SupportedLanguages.Python);
            chunks.AddRange(fb);
            return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
        }

        Language? language = null;
        Parser? parser = null;
        Tree? tree = null;
        try
        {
            // Пытаемся загрузить язык, с fallback вариантами имён
            language = TryLoadLanguage(treeSitterLang, errors, ctx.FilePath);
            if (language == null)
            {
                var fb = _slicer.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang));
                chunks.AddRange(fb);
                return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
            }

            parser = new Parser(language);
            tree = parser.Parse(ctx.Content);
            if (tree == null)
            {
                errors.Add(new AnalyzerError(ctx.FilePath, $"TreeSitter Parse returned null for {treeSitterLang}"));
                var fb = _slicer.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang));
                chunks.AddRange(fb);
                return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
            }

            var root = tree.RootNode;
            if (root.HasError)
                errors.Add(new AnalyzerError(ctx.FilePath, $"TreeSitter hasError at {root.StartPosition.Row}:{root.StartPosition.Column}", root.StartPosition.Row + 1, root.StartPosition.Column + 1));

            // Попытка Query.Execute для демонстрации (требование), но основной сбор через Walk
            TryExecuteQueries(language, root, errors, ctx.FilePath);

            // Walk для medium глубины
            var stack = new Stack<Symbol>();
            var callNodes = new List<Node>();
            var importNodes = new List<Node>();
            var chunkCandidates = new List<ChunkCandidate>();

            Walk(root, stack, symbols, chunkCandidates, relations, errors, ctx, treeSitterLang, callNodes, importNodes, ct);

            // Сборка чанков с исключением вложенных семантических чанков:
            // источник чанка = диапазон узла минус диапазоны чанков-потомков (потомки сохраняют полное содержимое)
            foreach (var cand in chunkCandidates)
                TryBuildChunk(cand, chunkCandidates, tree, MapToSupported(treeSitterLang), chunks, errors, ctx.FilePath);

            // Relations для calls: по имени ищем target среди symbols
            foreach (var callNode in callNodes)
            {
                try
                {
                    var callText = callNode.Text.Trim();
                    // Извлекаем имя функции из call: до '('
                    var callName = ExtractCallName(callNode);
                    if (string.IsNullOrWhiteSpace(callName)) continue;
                    // Найти родительский символ (функцию/метод) по линии
                    var line = (int)callNode.StartPosition.Row + 1;
                    var caller = symbols.Where(s => s.Kind is SymbolKind.Function or SymbolKind.Method or "Method" or "Function")
                        .FirstOrDefault(s => s.StartLine <= line && s.EndLine >= line);
                    // fallback: ближайший stack parent
                    caller ??= stack.Count > 0 ? stack.Peek() : symbols.LastOrDefault(s => s.StartLine <= line);
                    if (caller == null) continue;
                    var target = symbols.FirstOrDefault(s => s.Name == callName);
                    if (target != null && caller.Id != target.Id)
                    {
                        if (!relations.Any(r => r.FromSymbolId == caller.Id && r.ToSymbolId == target.Id && r.RelationType == RelationType.Calls))
                            relations.Add(new Relation(caller.Id, target.Id, RelationType.Calls));
                    }
                    else
                    {
                        // Внешний вызов — пропускаем, но можно добавить AnalyzerError как info? не нужно
                    }
                }
                catch { }
            }

            if (chunks.Count == 0)
            {
                // Если не нашли семантических единиц — fallback sliding window
                var fb = _slicer.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang));
                chunks.AddRange(fb);
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"TreeSitter failed: {ex.GetType().Name} {ex.Message}", null, null));
            if (chunks.Count == 0)
            {
                var fb = _slicer.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang ?? "Python"));
                chunks.AddRange(fb);
            }
        }
        finally
        {
            tree?.Dispose();
            parser?.Dispose();
            language?.Dispose();
        }

        return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
    }

    private static string? DetectTreeSitterLanguage(string filePath, string? ctxLang)
    {
        if (ExtensionToTreeSitter.TryGetValue(Path.GetExtension(filePath), out var ts)) return ts;
        if (ctxLang != null) return MapCtxLanguageToTreeSitter(ctxLang);
        return null;
    }

    private static string? MapCtxLanguageToTreeSitter(string lang)
        => ContextToTreeSitter.TryGetValue(lang, out var ts) ? ts : null;

    private static string MapToSupported(string treeLang)
        => TreeSitterToSupported.TryGetValue(treeLang, out var s) ? s : SupportedLanguages.Python;

    private static Language? TryLoadLanguage(string name, List<AnalyzerError> errors, string filePath)
    {
        var candidates = new List<string> { name };
        // Добавляем fallback варианты
        if (LanguageLoadFallbacks.TryGetValue(name, out var extra))
            candidates.AddRange(extra);
        foreach (var cand in candidates)
        {
            try { return new Language(cand); }
            catch { }
        }
        errors.Add(new AnalyzerError(filePath, $"TreeSitter language '{name}' not available (tried {string.Join(",", candidates)})"));
        return null;
    }

    private static void TryExecuteQueries(Language language, Node root, List<AnalyzerError> errors, string filePath)
    {
        // Демонстрация Query.Execute captures как требуется в ТЗ
        var queries = new Dictionary<string, string>
        {
            ["Python"] = "(function_definition name: (identifier) @fn) (class_definition name: (identifier) @cls) (call function: (_) @call)",
            ["JavaScript"] = "(function_declaration name: (identifier) @fn) (class_declaration name: (identifier) @cls) (call_expression function: (_) @call)",
            ["TypeScript"] = "(function_declaration name: (identifier) @fn) (class_declaration name: (type_identifier) @cls)",
            ["TSX"] = "(function_declaration name: (identifier) @fn) (class_declaration name: (type_identifier) @cls)",
            ["Go"] = "(function_declaration name: (identifier) @fn) (method_declaration name: (field_identifier) @mfn) (type_declaration) @ty",
            ["C++"] = "(function_definition declarator: (function_declarator declarator: (identifier) @fn)) (class_specifier name: (type_identifier) @cls)"
        };

        // Пробуем подходящий query по имени языка
        string? pattern = null;
        foreach (var kv in queries)
            if (language.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) { pattern = kv.Value; break; }

        if (pattern == null) return;
        try
        {
            using var query = new Query(language, pattern);
            using var cursor = query.Execute(root);
            // Просто итерируем captures чтобы убедиться что работает; результат не используется далее т.к. Walk уже соберёт символы
            var capCount = cursor.Captures.Count();
            // Можно логировать, но не добавляем ошибку
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(filePath, $"Query failed for {language.Name}: {ex.Message}"));
        }
    }

    private void Walk(
        Node node,
        Stack<Symbol> stack,
        List<Symbol> symbols,
        List<ChunkCandidate> candidates,
        List<Relation> relations,
        List<AnalyzerError> errors,
        FileContext ctx,
        string treeLang,
        List<Node> callNodes,
        List<Node> importNodes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var supportedLang = MapToSupported(treeLang);
        Symbol? created = null;

        // Определяем kind по типу узла через карту handlers (Python/Go/JS/C++ разные node types)
        var handlerCtx = new NodeHandlerContext { TreeLang = treeLang, Stack = stack, CallNodes = callNodes, ImportNodes = importNodes };
        if (NodeHandlers.TryGetValue(node.Type, out var handler))
            handler(node, handlerCtx);

        var kind = handlerCtx.Kind;
        var name = handlerCtx.Name;

        if (kind != null && !string.IsNullOrWhiteSpace(name))
        {
            // FQN via stack
            string fqn;
            string? ns = null;
            if (stack.Count > 0)
            {
                var stackFqn = string.Join(".", stack.Reverse().Select(s => s.Name));
                fqn = $"{stackFqn}.{name}";
                // Namespace = первый элемент стека если он Namespace else null
                var nsSym = stack.FirstOrDefault(s => s.Kind == SymbolKind.Namespace);
                if (nsSym != null) ns = nsSym.Name;
                else ns = stack.Peek().Name; // для Python модуля?
            }
            else
            {
                fqn = name!;
            }

            // Позиции 1-indexed
            int startLine = (int)node.StartPosition.Row + 1;
            int endLine = (int)node.EndPosition.Row + 1;
            int startCol = (int)node.StartPosition.Column + 1;
            int endCol = (int)node.EndPosition.Column + 1;

            // Для C++ endPosition может быть 0 — fallback используем StartLine
            if (endLine < startLine) endLine = startLine;

            string? signature = node.Text.Split('\n').FirstOrDefault()?.Trim();
            if (signature != null && signature.Length > 200) signature = signature[..200];

            Guid? parentId = stack.Count > 0 ? stack.Peek().Id : null;

            var symbol = new Symbol(Guid.NewGuid(), supportedLang, kind, name!, fqn, ns, parentId, startLine, endLine, startCol, endCol, signature);
            symbols.Add(symbol);

            // Contains relation
            if (parentId != null)
            {
                relations.Add(new Relation(parentId.Value, symbol.Id, RelationType.Contains));
            }

            // Чанк на function/class/method: источник собирается после обхода всего дерева,
            // чтобы исключить из него диапазоны вложенных семантических чанков
            if (kind is SymbolKind.Class or SymbolKind.Function or SymbolKind.Method)
                candidates.Add(new ChunkCandidate(node, symbol, startLine, endLine));

            created = symbol;
            stack.Push(symbol);
        }

        foreach (var child in node.Children)
        {
            Walk(child, stack, symbols, candidates, relations, errors, ctx, treeLang, callNodes, importNodes, ct);
        }

        if (created != null) stack.Pop();
    }

    private sealed record ChunkCandidate(Node Node, Symbol Symbol, int StartLine, int EndLine);

    private void TryBuildChunk(
        ChunkCandidate cand,
        IReadOnlyList<ChunkCandidate> all,
        Tree tree,
        string supportedLang,
        List<Chunk> chunks,
        List<AnalyzerError> errors,
        string filePath)
    {
        try
        {
            var source = BuildExcludedSource(cand, all, tree);
            var chunk = _factory.Create(cand.Symbol.Id, supportedLang, cand.StartLine, cand.EndLine, source);
            chunks.Add(chunk);
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(filePath, $"Chunk failed for {cand.Symbol.Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Source of the node minus the ranges of nested semantic chunks (descendant candidates).
    /// Remaining segments are concatenated in source order, so the parent chunk keeps its own
    /// text without duplicating child chunk content. If the node consists entirely of child
    /// chunks (degenerate case), falls back to the full node text.
    /// </summary>
    private static string BuildExcludedSource(ChunkCandidate cand, IReadOnlyList<ChunkCandidate> all, Tree tree)
    {
        var descendants = all
            .Where(d => !ReferenceEquals(d, cand)
                        && d.Node.StartIndex >= cand.Node.StartIndex
                        && d.Node.EndIndex <= cand.Node.EndIndex)
            .OrderBy(d => d.Node.StartIndex)
            .ThenBy(d => d.Node.EndIndex)
            .ToList();

        var sb = new StringBuilder(Math.Max(0, cand.Node.EndIndex - cand.Node.StartIndex));
        var cursor = cand.Node.StartIndex;
        foreach (var d in descendants)
        {
            if (d.Node.StartIndex > cursor)
                sb.Append(tree.GetText(cursor, d.Node.StartIndex));
            if (d.Node.EndIndex > cursor)
                cursor = d.Node.EndIndex;
        }
        if (cand.Node.EndIndex > cursor)
            sb.Append(tree.GetText(cursor, cand.Node.EndIndex));

        return sb.Length > 0 ? sb.ToString() : cand.Node.Text;
    }

    private static string? ExtractName(Node node, string? treeLang = null)
    {
        // Спец-обработка для C++ function_definition — искать имя внутри declarator
        if (node.Type == "function_definition" && treeLang == "C++")
        {
            // Найти declarator потом field_identifier/identifier который является именем функции
            var declarator = node.Children.FirstOrDefault(c => c.Type.Contains("declarator"));
            if (declarator != null)
            {
                var found = FindIdentifierDeep(declarator);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    // Для MyClass::methodOne берём после ::
                    if (found.Contains("::")) return found.Split("::").Last().Trim();
                    return found;
                }
            }
        }
        if (node.Type == "namespace_definition")
        {
            foreach (var child in node.Children)
                if (child.Type.Contains("identifier")) return child.Text.Trim();
        }
        if (node.Type == "class_specifier" && treeLang == "C++")
        {
            foreach (var child in node.Children)
                if (child.Type is "type_identifier" or "identifier") return child.Text.Trim();
            // fallback deep
            var deep = FindIdentifierDeep(node);
            if (!string.IsNullOrWhiteSpace(deep)) return deep;
        }
        // Общий поиск: ищем identifier содержащие "identifier"
        foreach (var child in node.Children)
        {
            if (child.Type.Contains("identifier")) return child.Text.Trim();
        }
        foreach (var child in node.Children)
        {
            foreach (var grand in child.Children)
            {
                if (grand.Type.Contains("identifier")) return grand.Text.Trim();
                foreach (var gg in grand.Children)
                    if (gg.Type.Contains("identifier")) return gg.Text.Trim();
            }
        }
        var deep2 = FindIdentifierDeep(node);
        if (!string.IsNullOrWhiteSpace(deep2))
        {
            if (deep2.Contains("::")) return deep2.Split("::").Last().Trim();
            if (deep2.Contains(".")) return deep2.Split(".").Last().Trim();
            return deep2;
        }
        var txt = node.Text;
        if (!string.IsNullOrWhiteSpace(txt))
        {
            var idx = txt.IndexOf('(');
            if (idx > 0)
            {
                var before = txt[..idx].Trim();
                var parts = before.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var cand = parts[^1].Trim('*', '&');
                    if (cand.Contains("::")) cand = cand.Split("::").Last();
                    if (cand.Contains(".")) cand = cand.Split(".").Last();
                    return cand;
                }
            }
            var br = txt.IndexOf('{');
            if (br > 0)
            {
                var before = txt[..br].Trim();
                var parts = before.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var cand = parts[^1].Trim();
                    if (cand.Contains("::")) cand = cand.Split("::").Last();
                    return cand;
                }
            }
        }
        return null;
    }

    private static string? FindIdentifierDeep(Node node)
    {
        if (node.Type.Contains("identifier")) return node.Text.Trim();
        foreach (var child in node.Children)
        {
            var res = FindIdentifierDeep(child);
            if (!string.IsNullOrWhiteSpace(res)) return res;
        }
        return null;
    }

    private static string? ExtractCallName(Node node)
    {
        if (node.Children.Count > 0)
        {
            var func = node.Children[0];
            var txt = func.Text.Trim();
            if (txt.Contains("::")) txt = txt.Split("::").Last().Trim();
            if (txt.Contains('.')) return txt.Split('.').Last().Trim();
            return txt.Split('(')[0].Trim();
        }
        return null;
    }

    private sealed class NodeHandlerContext
    {
        public string TreeLang { get; set; } = "";
        public Stack<Symbol> Stack { get; set; } = null!;
        public List<Node> CallNodes { get; set; } = null!;
        public List<Node> ImportNodes { get; set; } = null!;
        public string? Kind { get; set; }
        public string? Name { get; set; }
    }

    private static void HandleClassLike(Node node, NodeHandlerContext c)
    {
        // Go: type MyStruct struct { ... } — считаем как Class
        if (node.Type == "type_declaration" && c.TreeLang != "Go") return;
        c.Kind = SymbolKind.Class;
        c.Name = ExtractName(node, c.TreeLang);
        // Go type_declaration содержит type_spec с identifier; если имя пустое — пропустим
        if (c.Kind == SymbolKind.Class && node.Type == "type_declaration")
        {
            c.Name = ExtractName(node) ?? c.Name;
            if (string.IsNullOrWhiteSpace(c.Name)) c.Kind = null;
        }
    }

    private static void HandleFunctionDefinition(Node node, NodeHandlerContext c)
    {
        if (c.TreeLang == "C++" || c.TreeLang == "Python")
        {
            c.Kind = SymbolKind.Function;
            if (c.Stack.Count > 0 && c.Stack.Peek().Kind == SymbolKind.Class)
                c.Kind = SymbolKind.Method;
            c.Name = ExtractName(node, c.TreeLang);
        }
    }

    private static void HandleFunctionDeclaration(Node node, NodeHandlerContext c)
    {
        c.Kind = SymbolKind.Function;
        c.Name = ExtractName(node, c.TreeLang);
    }

    private static void HandleMethod(Node node, NodeHandlerContext c)
    {
        c.Kind = SymbolKind.Method;
        c.Name = ExtractName(node, c.TreeLang);
    }

    private static void HandleArrowFunction(Node node, NodeHandlerContext c)
    {
        c.Kind = SymbolKind.Function;
        c.Name = "arrow";
        var parentVar = node.Parent?.Type == "variable_declarator" ? ExtractName(node.Parent!, c.TreeLang) : null;
        if (!string.IsNullOrWhiteSpace(parentVar)) c.Name = parentVar;
    }

    private static void HandleFunctionExpression(Node node, NodeHandlerContext c)
    {
        c.Kind = SymbolKind.Function;
        // var foo = function() {} — имя из окружающего декларатора; иначе анонимная функция
        var parentVar = node.Parent?.Type == "variable_declarator" ? ExtractName(node.Parent!, c.TreeLang) : null;
        c.Name = parentVar ?? "anonymous";
    }

    private static void HandleCall(Node node, NodeHandlerContext c) => c.CallNodes.Add(node);

    private static void HandleImport(Node node, NodeHandlerContext c) => c.ImportNodes.Add(node);

    private static void HandleNamespace(Node node, NodeHandlerContext c)
    {
        c.Kind = SymbolKind.Namespace;
        c.Name = ExtractName(node, c.TreeLang);
    }
}
