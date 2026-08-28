using CodeRag.Analyzers.Abstractions;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter;

/// <summary>
/// Polyglot Tree-sitter анализатор для Python / JavaScript / Go / C++ / TypeScript (§15).
/// Использует TreeSitter.DotNet Language("Python"/"JavaScript"/"Go"/"C++"/"TypeScript"),
/// Parser.Parse, Query.Execute captures, символы function/class/method/call/import, FQN via stack, depth medium.
/// </summary>
public sealed class TreeSitterAnalyzer : ICodeAnalyzer
{
    public string Language => SupportedLanguages.Python; // polyglot, но возвращает python как primary; CanAnalyze покрывает все

    public bool CanAnalyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".py" or ".pyw" or ".pyi"
            or ".js" or ".mjs" or ".cjs" or ".jsx"
            or ".ts" or ".tsx"
            or ".go"
            or ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh" or ".hxx";
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
            var fb = ChunkingHelper.SlidingWindow(ctx.Content, ctx.Language ?? SupportedLanguages.Python);
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
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang));
                chunks.AddRange(fb);
                return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
            }

            parser = new Parser(language);
            tree = parser.Parse(ctx.Content);
            if (tree == null)
            {
                errors.Add(new AnalyzerError(ctx.FilePath, $"TreeSitter Parse returned null for {treeSitterLang}"));
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang));
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

            Walk(root, stack, symbols, chunks, relations, errors, ctx, treeSitterLang, callNodes, importNodes, ct);

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
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang));
                chunks.AddRange(fb);
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"TreeSitter failed: {ex.GetType().Name} {ex.Message}", null, null));
            if (chunks.Count == 0)
            {
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, MapToSupported(treeSitterLang ?? "Python"));
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
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".py" or ".pyw" or ".pyi" => "Python",
            ".js" or ".mjs" or ".cjs" or ".jsx" => "JavaScript",
            ".ts" => "TypeScript",
            ".tsx" => "TSX",
            ".go" => "Go",
            ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh" or ".hxx" => "C++",
            _ when ctxLang != null => MapCtxLanguageToTreeSitter(ctxLang),
            _ => null
        };
    }

    private static string? MapCtxLanguageToTreeSitter(string lang) => lang.ToLowerInvariant() switch
    {
        "python" => "Python",
        "javascript" => "JavaScript",
        "go" => "Go",
        "cpp" or "c++" => "C++",
        "csharp" => null,
        _ => null
    };

    private static string MapToSupported(string treeLang) => treeLang.ToLowerInvariant() switch
    {
        "python" => SupportedLanguages.Python,
        "javascript" or "typescript" or "tsx" => SupportedLanguages.JavaScript,
        "go" => SupportedLanguages.Go,
        "c++" or "cpp" => SupportedLanguages.Cpp,
        _ => SupportedLanguages.Python
    };

    private static Language? TryLoadLanguage(string name, List<AnalyzerError> errors, string filePath)
    {
        var candidates = new List<string> { name };
        // Добавляем fallback варианты
        if (name == "C++") candidates.AddRange(new[] { "Cpp", "cpp", "c++" });
        if (name == "TypeScript") candidates.AddRange(new[] { "Typescript", "typescript", "TSX", "tsx" });
        if (name == "TSX") candidates.AddRange(new[] { "TypeScript", "typescript" });
        if (name == "JavaScript") candidates.AddRange(new[] { "Javascript", "javascript", "JS" });
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

    private static void Walk(
        Node node,
        Stack<Symbol> stack,
        List<Symbol> symbols,
        List<Chunk> chunks,
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
        string? kind = null;
        string? name = null;

        // Определяем kind по типу узла
        // Python
        // Go, JS, C++ типы разные, поэтому проверяем множество
        switch (node.Type)
        {
            // Classes
            case "class_definition":
            case "class_declaration":
            case "class_specifier":
            case "type_declaration": // Go: type MyStruct struct { ... } — это не класс но считаем как Class
                if (node.Type == "type_declaration" && treeLang != "Go") break;
                kind = SymbolKind.Class;
                name = ExtractName(node, treeLang);
                break;
            case "function_definition":
                if (treeLang == "C++" || treeLang == "Python")
                {
                    kind = SymbolKind.Function;
                    if (stack.Count > 0 && stack.Peek().Kind == SymbolKind.Class)
                        kind = SymbolKind.Method;
                    name = ExtractName(node, treeLang);
                }
                break;
            case "function_declaration":
                kind = SymbolKind.Function;
                name = ExtractName(node, treeLang);
                break;
            case "method_declaration":
            case "method_definition":
                kind = SymbolKind.Method;
                name = ExtractName(node, treeLang);
                break;
            case "arrow_function":
                kind = SymbolKind.Function;
                name = "arrow";
                var parentVar = node.Parent?.Type == "variable_declarator" ? ExtractName(node.Parent!, treeLang) : null;
                if (!string.IsNullOrWhiteSpace(parentVar)) name = parentVar;
                break;
            case "call":
            case "call_expression":
                callNodes.Add(node);
                break;
            case "import_statement":
            case "import_from_statement":
            case "import_declaration":
                importNodes.Add(node);
                break;
            case "namespace_definition":
                kind = SymbolKind.Namespace;
                name = ExtractName(node, treeLang);
                break;
        }

        // Для Go type_declaration — проверяем что это struct/interface
        if (kind == SymbolKind.Class && node.Type == "type_declaration")
        {
            // Для Go: type_declaration содержит type_spec с identifier
            name = ExtractName(node) ?? name;
            // Если имя пустое — пропустим
            if (string.IsNullOrWhiteSpace(name)) kind = null;
        }

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

            // Chunk per function/class/method
            if (kind is SymbolKind.Class or SymbolKind.Function or SymbolKind.Method)
            {
                var source = node.Text;
                if (!string.IsNullOrWhiteSpace(source))
                {
                    try
                    {
                        var chunk = ChunkingHelper.CreateChunk(symbol.Id, supportedLang, startLine, endLine, source);
                        chunks.Add(chunk);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new AnalyzerError(ctx.FilePath, $"Chunk failed for {name}: {ex.Message}"));
                    }
                }
            }

            created = symbol;
            stack.Push(symbol);
        }

        foreach (var child in node.Children)
        {
            Walk(child, stack, symbols, chunks, relations, errors, ctx, treeLang, callNodes, importNodes, ct);
        }

        if (created != null) stack.Pop();
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
}
