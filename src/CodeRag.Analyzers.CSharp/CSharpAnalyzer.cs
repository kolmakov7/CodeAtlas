using System.Collections.Concurrent;
using CodeRag.Analyzers.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using AbstractionsSK = CodeRag.Analyzers.Abstractions.SymbolKind;

namespace CodeRag.Analyzers.CSharp;

/// <summary>
/// C# анализатор — Roslyn AdhocWorkspace/CSharpCompilation (§14).
/// AdhocWorkspace как база, GetBclReferences via TRUSTED_PLATFORM_ASSEMBLIES,
/// CSharpCompilation.Create, SemanticModel, ISymbol для overloads/generics/NsA vs NsB,
/// SymbolFinder.FindReferencesAsync/FindImplementationsAsync fallback syntax-only.
/// </summary>
public sealed class CSharpAnalyzer : ICodeAnalyzer
{
    public string Language => SupportedLanguages.CSharp;

    private static readonly string[] Extensions = [".cs", ".csx"];

    public bool CanAnalyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    // Кеш BCL references (thread-safe lazy)
    private static readonly Lazy<IReadOnlyList<MetadataReference>> BclRefsLazy = new(GetBclReferencesInternal, true);

    private static IReadOnlyList<MetadataReference> GetBclReferences() => BclRefsLazy.Value;

    private static IReadOnlyList<MetadataReference> GetBclReferencesInternal()
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MetadataReference>();
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) return;
            if (!refs.Add(path)) return;
            try { result.Add(MetadataReference.CreateFromFile(path)); } catch { /* ignore */ }
        }

        Add(typeof(object).Assembly.Location); // System.Private.CoreLib
        Add(typeof(Console).Assembly.Location); // System.Console
        Add(typeof(Enumerable).Assembly.Location); // System.Linq / System.Core
        Add(typeof(List<>).Assembly.Location);
        Add(typeof(System.Runtime.GCSettings).Assembly.Location); // System.Runtime

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

        // Fallback: ensure at least one ref
        if (result.Count == 0) Add(typeof(object).Assembly.Location);
        return result;
    }

    public async Task<AnalyzeResult> AnalyzeAsync(FileContext ctx, CancellationToken ct)
    {
        var symbols = new List<Symbol>();
        var relations = new List<Relation>();
        var chunks = new List<Chunk>();
        var errors = new List<AnalyzerError>();

        if (string.IsNullOrWhiteSpace(ctx.Content))
            return new AnalyzeResult(symbols, relations, chunks, errors);

        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(ctx.Content, new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular), path: ctx.FilePath);
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"Parse failed: {ex.Message}"));
            // fallback sliding window
            var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.CSharp);
            chunks.AddRange(fb);
            return new AnalyzeResult(symbols, relations, chunks, errors);
        }

        // Диагностика парсера (syntax errors) — не фейлит, пишем в AnalyzerError
        var parseDiags = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(10).ToList();
        foreach (var d in parseDiags)
        {
            var span = d.Location.GetLineSpan();
            errors.Add(new AnalyzerError(ctx.FilePath, d.GetMessage(), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
        }

        // Попытка семантического анализа
        CSharpCompilation? compilation = null;
        SemanticModel? semanticModel = null;
        IReadOnlyList<MetadataReference> refs = Array.Empty<MetadataReference>();
        try
        {
            refs = GetBclReferences();
            compilation = CSharpCompilation.Create(
                assemblyName: "CodeRag_Adhoc_" + Guid.NewGuid().ToString("N"),
                syntaxTrees: new[] { tree },
                references: refs,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithAllowUnsafe(true)
                    .WithNullableContextOptions(NullableContextOptions.Enable)
                    .WithOverflowChecks(false));

            semanticModel = compilation.GetSemanticModel(tree);

            var compDiags = compilation.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error).Take(10).ToList();
            foreach (var d in compDiags)
            {
                // Не дублировать parse ошибки; логируем как AnalyzerError но не фейлим
                var span = d.Location.GetLineSpan();
                // Игнорируем hidden/info; только Error
                if (!errors.Any(e => e.Message == d.GetMessage() && e.Line == span.StartLinePosition.Line + 1))
                    errors.Add(new AnalyzerError(ctx.FilePath, d.GetMessage(), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"Compilation failed (fallback syntax-only): {ex.Message}"));
            // semanticModel stays null -> fallback syntax-only
        }

        // Извлечение символов (семантика где есть, иначе syntax)
        var symbolMap = new Dictionary<SyntaxNode, Symbol>(); // syntax node -> DTO
        var symbolByRoslyn = new Dictionary<ISymbol, Symbol>(SymbolEqualityComparer.Default);
        // Parent stack для Relations Contains
        var parentStack = new Stack<Symbol>();

        SyntaxNode root;
        try { root = await tree.GetRootAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"GetRoot failed: {ex.Message}"));
            var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.CSharp);
            chunks.AddRange(fb);
            return new AnalyzeResult(symbols, relations, chunks, errors);
        }

        // Соберём все declarations
        // Используем рекурсивный обход чтобы сохранить иерархию для Contains и ParentSymbolId
        try
        {
            VisitNode(root, parentStack, symbols, chunks, relations, symbolMap, symbolByRoslyn, semanticModel, ctx, errors, ct);
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"Visit failed: {ex.Message}"));
        }

        // Top-level statements (Program.cs) → один чанк «Program», чтобы поиск и get_code работали
        try
        {
            if (root is CompilationUnitSyntax unit)
            {
                var globals = unit.Members.OfType<GlobalStatementSyntax>().ToList();
                if (globals.Count > 0)
                {
                    var first = globals.First();
                    var last = globals.Last();
                    var ls = unit.SyntaxTree.GetLineSpan(TextSpan.FromBounds(first.SpanStart, last.Span.End));
                    var sl = Math.Max(1, ls.StartLinePosition.Line + 1);
                    var el = Math.Max(sl, ls.EndLinePosition.Line + 1);
                    var source = SpanToText(ctx.Content, TextSpan.FromBounds(first.SpanStart, last.Span.End));
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        var sym = new Symbol(
                            Guid.NewGuid(),
                            SupportedLanguages.CSharp,
                            AbstractionsSK.Method,
                            "Program",
                            "global::Program.Main",
                            null,
                            null,
                            sl,
                            el,
                            1,
                            1,
                            "(top-level statements)");
                        symbols.Add(sym);
                        chunks.Add(ChunkingHelper.CreateChunk(sym.Id, SupportedLanguages.CSharp, sl, el, source));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"Top-level chunk failed: {ex.Message}"));
        }

        // Дополнительные реляции: Inherits / Implements / Calls / Creates / Returns / ParameterType etc.
        if (semanticModel != null && compilation != null)
        {
            try
            {
                ExtractSemanticRelations(root, semanticModel, symbolMap, symbols, relations, errors, ctx.FilePath, ct);
            }
            catch (Exception ex)
            {
                errors.Add(new AnalyzerError(ctx.FilePath, $"Semantic relations failed (syntax fallback): {ex.Message}"));
            }

            // Попытка SymbolFinder для FindReferences / FindImplementations (внутри одного файла)
            // Работает на AdhocWorkspace; fallback syntax-only при ошибках
            try
            {
                await TrySymbolFinderAsync(ctx, refs, symbolByRoslyn, relations, errors, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(new AnalyzerError(ctx.FilePath, $"SymbolFinder fallback: {ex.Message}"));
            }
        }
        else
        {
            // Syntax-only fallback для Calls: поиск InvocationExpressionSyntax по имени
            try { ExtractSyntaxCalls(root, symbols, relations, ctx.FilePath, errors); } catch { }
        }

        // Fallback чанкинг: если семантических чанков 0, но файл содержит код (типы/top-level) — sliding window 800/80.
        // Файлы только с using/attribute-директивами (GlobalUsings и т.п.) не индексируем.
        if (chunks.Count == 0)
        {
            var hasCode = root.DescendantNodes().Any(n => n is TypeDeclarationSyntax or GlobalStatementSyntax);
            if (hasCode)
            {
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.CSharp);
                chunks.AddRange(fb);
            }
        }

        return new AnalyzeResult(symbols, relations, chunks, errors);
    }

    /// <summary>Enclosing type declaration context (name + declaration line without body) for chunk building.</summary>
    private sealed record TypeContext(string Name, string DeclarationLine);

    private static readonly IReadOnlyList<TypeContext> EmptyTypeChain = Array.Empty<TypeContext>();

    private static void VisitNode(
        SyntaxNode node,
        Stack<Symbol> parentStack,
        List<Symbol> symbols,
        List<Chunk> chunks,
        List<Relation> relations,
        Dictionary<SyntaxNode, Symbol> symbolMap,
        Dictionary<ISymbol, Symbol> symbolByRoslyn,
        SemanticModel? semanticModel,
        FileContext ctx,
        List<AnalyzerError> errors,
        CancellationToken ct,
        string? namespaceName = null,
        IReadOnlyList<TypeContext> typeChain = null!)
    {
        typeChain ??= EmptyTypeChain;
        // Определяем kind для узлов, которые соответствуют символам
        Symbol? currentSymbol = null;
        string? kind = null;
        SyntaxToken identifierToken = default;
        string? name = null;

        switch (node)
        {
            case FileScopedNamespaceDeclarationSyntax fns:
                kind = AbstractionsSK.Namespace;
                name = fns.Name.ToString();
                identifierToken = default;
                break;
            case NamespaceDeclarationSyntax ns:
                kind = AbstractionsSK.Namespace;
                name = ns.Name.ToString();
                break;
            case ClassDeclarationSyntax cds:
                kind = AbstractionsSK.Class;
                name = cds.Identifier.Text;
                identifierToken = cds.Identifier;
                break;
            case InterfaceDeclarationSyntax ids:
                kind = AbstractionsSK.Interface;
                name = ids.Identifier.Text;
                identifierToken = ids.Identifier;
                break;
            case RecordDeclarationSyntax rds:
                // RecordDeclarationSyntax covers record class / record struct
                kind = rds.ClassOrStructKeyword.Text == "struct" ? AbstractionsSK.Struct : AbstractionsSK.Record;
                // Для record с Kind RecordDeclaration, fallback to Record
                if (kind == AbstractionsSK.Struct && rds.Kind() == SyntaxKind.RecordDeclaration) kind = AbstractionsSK.Record;
                name = rds.Identifier.Text;
                identifierToken = rds.Identifier;
                break;
            case StructDeclarationSyntax sds:
                kind = AbstractionsSK.Struct;
                name = sds.Identifier.Text;
                identifierToken = sds.Identifier;
                break;
            case EnumDeclarationSyntax eds:
                kind = AbstractionsSK.Enum;
                name = eds.Identifier.Text;
                identifierToken = eds.Identifier;
                break;
            case EnumMemberDeclarationSyntax emds:
                kind = AbstractionsSK.EnumMember;
                name = emds.Identifier.Text;
                identifierToken = emds.Identifier;
                break;
            case MethodDeclarationSyntax mds:
                kind = AbstractionsSK.Method;
                name = mds.Identifier.Text;
                identifierToken = mds.Identifier;
                break;
            case ConstructorDeclarationSyntax cds2:
                kind = AbstractionsSK.Constructor;
                name = cds2.Identifier.Text;
                identifierToken = cds2.Identifier;
                break;
            case PropertyDeclarationSyntax pds:
                kind = AbstractionsSK.Property;
                name = pds.Identifier.Text;
                identifierToken = pds.Identifier;
                break;
            case FieldDeclarationSyntax fds:
                foreach (var v in fds.Declaration.Variables)
                {
                    var fieldKind = AbstractionsSK.Field;
                    var fieldName = v.Identifier.Text;
                    var fieldSymbolDto = CreateSymbolForNode(fds, v.Identifier, fieldKind, fieldName, parentStack, symbols, symbolMap, symbolByRoslyn, semanticModel, ctx, errors);
                    if (parentStack.Count > 0)
                    {
                        var parentId = parentStack.Peek().Id;
                        if (!relations.Any(r => r.FromSymbolId == parentId && r.ToSymbolId == fieldSymbolDto.Id && r.RelationType == RelationType.Contains))
                            relations.Add(new Relation(parentId, fieldSymbolDto.Id, RelationType.Contains));
                    }
                }
                kind = null;
                break;
            case EventDeclarationSyntax evds:
                kind = AbstractionsSK.Event;
                name = evds.Identifier.Text;
                identifierToken = evds.Identifier;
                break;
            case EventFieldDeclarationSyntax efds:
                foreach (var v in efds.Declaration.Variables)
                {
                    var evKind = AbstractionsSK.Event;
                    var evName = v.Identifier.Text;
                    var evSymbol = CreateSymbolForNode(efds, v.Identifier, evKind, evName, parentStack, symbols, symbolMap, symbolByRoslyn, semanticModel, ctx, errors);
                    if (parentStack.Count > 0)
                    {
                        var parentId = parentStack.Peek().Id;
                        if (!relations.Any(r => r.FromSymbolId == parentId && r.ToSymbolId == evSymbol.Id && r.RelationType == RelationType.Contains))
                            relations.Add(new Relation(parentId, evSymbol.Id, RelationType.Contains));
                    }
                }
                kind = null;
                break;
            case DelegateDeclarationSyntax dds:
                kind = AbstractionsSK.Method; // delegate как method-like
                name = dds.Identifier.Text;
                identifierToken = dds.Identifier;
                break;
            case OperatorDeclarationSyntax ods:
                kind = AbstractionsSK.Method;
                name = ods.OperatorToken.Text;
                break;
            case ConversionOperatorDeclarationSyntax cods:
                kind = AbstractionsSK.Method;
                name = cods.Type.ToString();
                break;
        }

        if (kind != null && name != null)
        {
            var syntaxNodeForMap = node; // для полей мы уже создали отдельно
            currentSymbol = CreateSymbolForNode(node, identifierToken, kind, name, parentStack, symbols, symbolMap, symbolByRoslyn, semanticModel, ctx, errors);
            // Relation Contains от родителя
            if (parentStack.Count > 0)
            {
                var parentId = parentStack.Peek().Id;
                if (!relations.Any(r => r.FromSymbolId == parentId && r.ToSymbolId == currentSymbol.Id && r.RelationType == RelationType.Contains))
                    relations.Add(new Relation(parentId, currentSymbol.Id, RelationType.Contains));
            }
            // Чанки для семантических единиц:
            //  - метод/конструктор → один чанк: контекст (namespace + цепочка типов) + полный исходник метода;
            //  - class/interface/record/struct → скелет (declaration + поля + сигнатуры) без тел методов;
            //  - enum → весь enum как одна единица.
            if (kind is AbstractionsSK.Class or AbstractionsSK.Interface or AbstractionsSK.Record or AbstractionsSK.Struct or AbstractionsSK.Enum or AbstractionsSK.Method or AbstractionsSK.Constructor)
            {
                try
                {
                    var budget = Math.Max(256, ctx.MaxChunkChars);
                    if (node is EnumDeclarationSyntax)
                    {
                        AddWholeNodeChunk(chunks, ctx, currentSymbol, node);
                    }
                    else if (node is TypeDeclarationSyntax typeDecl)
                    {
                        AddTypeSkeletonChunk(chunks, ctx, currentSymbol, typeDecl, namespaceName, typeChain, budget);
                    }
                    else
                    {
                        AddMethodChunk(chunks, ctx, currentSymbol, node, namespaceName, typeChain, budget);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new AnalyzerError(ctx.FilePath, $"Chunk failed for {name}: {ex.Message}"));
                }
            }
        }

        // Рекурсия по детям с учётом стека + передачи контекста (namespace, цепочка типов)
        if (currentSymbol != null) parentStack.Push(currentSymbol);
        string? childNs = namespaceName;
        IReadOnlyList<TypeContext> childChain = typeChain;
        if (node is FileScopedNamespaceDeclarationSyntax fnsDecl)
        {
            var nsName = fnsDecl.Name.ToString();
            childNs = string.IsNullOrEmpty(namespaceName) ? nsName : $"{namespaceName}.{nsName}";
        }
        else if (node is NamespaceDeclarationSyntax nsDecl)
        {
            var nsName = nsDecl.Name.ToString();
            childNs = string.IsNullOrEmpty(namespaceName) ? nsName : $"{namespaceName}.{nsName}";
        }
        else if (currentSymbol != null && node is TypeDeclarationSyntax td)
        {
            childChain =
            [
                .. typeChain,
                new TypeContext(td.Identifier.Text, TypeDeclarationLine(td))
            ];
        }
        foreach (var child in node.ChildNodes())
        {
            ct.ThrowIfCancellationRequested();
            VisitNode(child, parentStack, symbols, chunks, relations, symbolMap, symbolByRoslyn, semanticModel, ctx, errors, ct, childNs, childChain);
        }
        if (currentSymbol != null) parentStack.Pop();
    }

    // ─────────────────────────── Chunk building ───────────────────────────

    private static string SpanToText(string content, TextSpan span)
    {
        if (span.Start < 0 || span.Start >= content.Length) return string.Empty;
        return content.Substring(span.Start, Math.Min(span.Length, content.Length - span.Start));
    }

    /// <summary>Чанк из полного исходника узла (для enum и других небольших единиц).</summary>
    private static void AddWholeNodeChunk(List<Chunk> chunks, FileContext ctx, Symbol symbol, SyntaxNode node)
    {
        var source = node.ToFullString().TrimStart();
        if (string.IsNullOrWhiteSpace(source)) return;
        var ls = node.SyntaxTree.GetLineSpan(node.Span);
        var startLine = Math.Max(1, ls.StartLinePosition.Line + 1);
        var endLine = Math.Max(startLine, ls.EndLinePosition.Line + 1);
        chunks.Add(ChunkingHelper.CreateChunk(symbol.Id, SupportedLanguages.CSharp, startLine, endLine, source));
    }

    /// <summary>Declaration-«шапка» типа: modifiers + type + name + generic args + primary ctor + base list + constraints (без тела).</summary>
    private static string TypeDeclarationLine(TypeDeclarationSyntax t)
    {
        var parts = new List<string>();
        foreach (var m in t.Modifiers)
            parts.Add(m.ToString());
        parts.Add(t.Keyword.ToString());
        parts.Add(t.Identifier.Text);
        if (t.TypeParameterList is { Parameters.Count: > 0 } typeParams)
            parts.Add(typeParams.ToString().ReplaceLineEndings(" "));
        if (t.ParameterList is { Parameters.Count: > 0 } paramList)
            parts.Add(paramList.ToString().ReplaceLineEndings(" "));
        if (t.BaseList is { Types.Count: > 0 } baseList)
            parts.Add(baseList.ToString().ReplaceLineEndings(" "));
        if (t.ConstraintClauses.Count > 0)
            parts.Add(t.ConstraintClauses.ToString().ReplaceLineEndings(" "));
        return string.Join(" ", parts).Trim();
    }

    /// <summary>Сигнатура члена: исходник до первой «{» (для expression-bodied — весь текст).</summary>
    private static string MemberSignature(SyntaxNode member)
    {
        var text = member.ToFullString().TrimStart();
        var idx = text.IndexOf('{');
        if (idx >= 0)
            text = text[..idx];
        return text.ReplaceLineEndings(" ").Trim();
    }

    /// <summary>
    /// Скелет типа: namespace + цепочка вложенных деклараций + declaration типа + поля/проперти/события + сигнатуры членов.
    /// Тела методов не включаются (у каждого метода свой чанк). Линии — реальный диапазон типа для get_code.
    /// </summary>
    private static void AddTypeSkeletonChunk(
        List<Chunk> chunks,
        FileContext ctx,
        Symbol symbol,
        TypeDeclarationSyntax td,
        string? ns,
        IReadOnlyList<TypeContext> outerChain,
        int budget)
    {
        var ls = td.SyntaxTree.GetLineSpan(td.Span);
        var startLine = Math.Max(1, ls.StartLinePosition.Line + 1);
        var endLine = Math.Max(startLine, ls.EndLinePosition.Line + 1);

        var head = new List<string>();
        if (!string.IsNullOrWhiteSpace(ns))
            head.Add($"namespace {ns};");
        foreach (var t in outerChain)
            head.Add(t.DeclarationLine);
        head.Add(TypeDeclarationLine(td));
        head.Add("{");

        var fields = new List<string>();
        var signatures = new List<string>();
        foreach (var m in td.Members)
        {
            switch (m)
            {
                case FieldDeclarationSyntax f:
                    fields.Add(f.ToString().ReplaceLineEndings(" ").Trim());
                    break;
                case PropertyDeclarationSyntax p:
                    fields.Add(p.ToString().ReplaceLineEndings(" ").Trim());
                    break;
                case EventDeclarationSyntax e:
                    fields.Add(e.ToString().ReplaceLineEndings(" ").Trim());
                    break;
                case EventFieldDeclarationSyntax ef:
                    fields.Add(ef.ToString().ReplaceLineEndings(" ").Trim());
                    break;
                case BaseMethodDeclarationSyntax bm:
                    signatures.Add(MemberSignature(bm));
                    break;
                case DelegateDeclarationSyntax d:
                    signatures.Add(d.ToString().ReplaceLineEndings(" ").Trim());
                    break;
                case TypeDeclarationSyntax nested:
                    signatures.Add(TypeDeclarationLine(nested) + ";");
                    break;
            }
        }

        var headText = string.Join("\n", head);
        var cap = budget - headText.Length - 3; // "}" + newlines
        var bodyParts = new List<string>();
        int used = 0, skipped = 0;
        foreach (var t in fields.Concat(signatures))
        {
            if (used + t.Length + 1 <= cap)
            {
                bodyParts.Add(t);
                used += t.Length + 1;
            }
            else
            {
                skipped++;
            }
        }
        if (skipped > 0)
            bodyParts.Add($"// ... ({skipped} more members)");

        var source = headText + "\n" + string.Join("\n", bodyParts) + "\n}";
        if (string.IsNullOrWhiteSpace(source)) return;
        chunks.Add(ChunkingHelper.CreateChunk(symbol.Id, SupportedLanguages.CSharp, startLine, endLine, source));
    }

    /// <summary>
    /// Чанк метода/конструктора: контекст (namespace + declaration-цепочка типов) + полный исходник метода
    /// (включая doc-комментарии и атрибуты). Если не влезает в бюджет — первый кусок с реальным контекстом,
    /// остальные — с [continued] заголовком. Линии — реальный диапазон метода для get_code.
    /// </summary>
    private static void AddMethodChunk(
        List<Chunk> chunks,
        FileContext ctx,
        Symbol symbol,
        SyntaxNode node,
        string? ns,
        IReadOnlyList<TypeContext> chain,
        int budget)
    {
        // Полный текст с doc-комментариями/атрибутами — для одиночного чанка
        var fullSource = node.ToFullString().TrimStart();
        // Текст от node.Span (начинается точно со startLine) — для окон при сплите: точное соответствие строк
        var spanSource = SpanToText(ctx.Content, node.Span);
        var source = string.IsNullOrWhiteSpace(spanSource) ? fullSource : spanSource;
        if (string.IsNullOrWhiteSpace(source)) return;
        var ls = node.SyntaxTree.GetLineSpan(node.Span);
        var startLine = Math.Max(1, ls.StartLinePosition.Line + 1);
        var endLine = Math.Max(startLine, ls.EndLinePosition.Line + 1);

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ns))
            contextParts.Add($"namespace {ns};");
        foreach (var t in chain)
            contextParts.Add(t.DeclarationLine);

        var contextText = string.Join("\n", contextParts);
        if (contextText.Length > budget - 512 && contextParts.Count > 2)
        {
            // Контекст сам по себе слишком большой: оставляем namespace + ближайший тип
            contextParts = [contextParts[0], contextParts[^1]];
            contextText = string.Join("\n", contextParts);
        }

        int window = budget - contextText.Length - 4; // "{", "}", newlines
        if (window < 256)
            window = 256;

        if (fullSource.Length <= window)
        {
            var text = contextText.Length > 0 ? $"{contextText}\n{{\n{fullSource}\n}}" : fullSource;
            chunks.Add(ChunkingHelper.CreateChunk(symbol.Id, SupportedLanguages.CSharp, startLine, endLine, text));
            return;
        }

        // Крупный метод: тело по окнам бюджета. Первое окно — с реальным контекстом, остальные — [continued].
        var header = BuildContextHeader(ctx, ns, chain, symbol);
        var subWindow = Math.Max(256, budget - header.Length - 2);
        var overlap = Math.Max(64, (int)(subWindow * 0.15));
        var slices = ChunkingHelper.SplitByCharBudget(source, startLine, endLine, subWindow, overlap, header).ToList();
        if (slices.Count == 0) return;

        var first = slices[0];
        var firstText = contextText.Length > 0
            ? $"{contextText}\n{{\n{TruncateToBudget(first.Text, budget - contextText.Length - 4)}\n}}"
            : TruncateToBudget(first.Text, budget - 1);
        chunks.Add(ChunkingHelper.CreateChunk(symbol.Id, SupportedLanguages.CSharp, first.StartLine, first.EndLine, firstText));

        for (var i = 1; i < slices.Count; i++)
        {
            var s = slices[i];
            chunks.Add(ChunkingHelper.CreateChunk(symbol.Id, SupportedLanguages.CSharp, s.StartLine, s.EndLine, s.Text));
        }
    }

    private static string TruncateToBudget(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, max - 1));
        if (cut <= 0) cut = max;
        return text[..cut];
    }

    /// <summary>Заголовок продолжения для окон крупного метода: путь + namespace.Типы.Метод.</summary>
    private static string BuildContextHeader(FileContext ctx, string? ns, IReadOnlyList<TypeContext> chain, Symbol symbol)
    {
        var typeName = chain.Count > 0 ? string.Join(".", chain.Select(t => t.Name)) : null;
        var qual = ns != null
            ? (typeName != null ? $"{ns}.{typeName}" : ns)
            : (typeName ?? symbol.Fqn);
        return $"// [continued] {ctx.FilePath} :: {qual}.{symbol.Name}";
    }

    /// <summary>FQN for member symbols: `ContainingTypeFQN.MemberName` (member symbols render as bare names under FullyQualifiedFormat).</summary>
    private static string BuildMemberFqn(ISymbol member, string name)
    {
        if (member.ContainingType is INamedTypeSymbol ct)
        {
            var ctFqn = ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!string.IsNullOrWhiteSpace(ctFqn) && ctFqn != "global::<global namespace>")
                return $"{ctFqn}.{name}";
        }
        return member.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static Symbol CreateSymbolForNode(
        SyntaxNode node,
        SyntaxToken identifier,
        string kind,
        string name,
        Stack<Symbol> parentStack,
        List<Symbol> symbols,
        Dictionary<SyntaxNode, Symbol> symbolMap,
        Dictionary<ISymbol, Symbol> symbolByRoslyn,
        SemanticModel? semanticModel,
        FileContext ctx,
        List<AnalyzerError> errors)
    {
        ISymbol? roslynSymbol = null;
        string? fqn = null;
        string? ns = null;
        string? signature = null;
        Guid? parentId = parentStack.Count > 0 ? parentStack.Peek().Id : null;

        if (semanticModel != null)
        {
            try
            {
                // Для Field/Event переменных — GetDeclaredSymbol на VariableDeclarator не работает, берём на FieldDeclaration
                if (node is FieldDeclarationSyntax || node is EventFieldDeclarationSyntax)
                {
                    // Попытка получить символ переменной
                    // identifier — это variable identifier
                    roslynSymbol = semanticModel.GetDeclaredSymbol(node) as ISymbol;
                    // Fallback попробовать найти по переменным
                    if (roslynSymbol == null)
                    {
                        // Найти VariableDeclarator по имени
                        var decl = node.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == name);
                        if (decl != null)
                            roslynSymbol = semanticModel.GetDeclaredSymbol(decl) as ISymbol;
                    }
                }
                else
                {
                    roslynSymbol = semanticModel.GetDeclaredSymbol(node) as ISymbol;
                }

                if (roslynSymbol != null)
                {
                    try
                    {
                        if (roslynSymbol is IMethodSymbol ms)
                        {
                            // Для overloads/generics формируем FQN вручную чтобы включить тип и параметры
                            var containingTypeFqn = ms.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "";
                            var paramList = string.Join(", ", ms.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
                            var methodFqnCore = string.IsNullOrWhiteSpace(containingTypeFqn) || containingTypeFqn == "global::<global namespace>"
                                ? $"{ms.Name}({paramList})"
                                : $"{containingTypeFqn}.{ms.Name}({paramList})";
                            fqn = methodFqnCore;
                            ns = ms.ContainingNamespace?.ToDisplayString();
                            if (string.IsNullOrWhiteSpace(ns) || ns == "<global namespace>") ns = ms.ContainingType?.ContainingNamespace?.ToDisplayString();
                            signature = ms.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                        }
                        else if (roslynSymbol is INamedTypeSymbol nts)
                        {
                            fqn = nts.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            ns = nts.ContainingNamespace?.ToDisplayString();
                            signature = nts.ToDisplayString();
                        }
                        else if (roslynSymbol is IPropertySymbol or IFieldSymbol or IEventSymbol)
                        {
                            // Member symbols: FullyQualifiedFormat renders them as BARE names
                            // (e.g. "Created"), so build the FQN from the containing type.
                            fqn = BuildMemberFqn(roslynSymbol, name);
                            ns = roslynSymbol.ContainingNamespace?.ToDisplayString();
                            signature = roslynSymbol.ToDisplayString();
                        }
                        else
                        {
                            fqn = roslynSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            ns = roslynSymbol.ContainingNamespace?.ToDisplayString();
                            signature = roslynSymbol.ToDisplayString();
                        }
                        if (ns == "<global namespace>") ns = null;
                    }
                    catch { /* ignore display errors */ }
                }
            }
            catch (Exception ex)
            {
                errors.Add(new AnalyzerError(ctx.FilePath, $"GetDeclaredSymbol failed for {name}: {ex.Message}"));
            }
        }

        // Fallback FQN via stack + syntax
        if (string.IsNullOrWhiteSpace(fqn))
        {
            // Namespace из parentStack или из синтаксиса
            var containingNs = parentStack.FirstOrDefault(s => s.Kind == AbstractionsSK.Namespace)?.Name;
            if (!string.IsNullOrWhiteSpace(containingNs))
                ns ??= containingNs;

            if (node is MethodDeclarationSyntax mds)
            {
                var paramTypes = string.Join(", ", mds.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "object"));
                var typeName = parentStack.FirstOrDefault(s => s.Kind is AbstractionsSK.Class or AbstractionsSK.Interface or AbstractionsSK.Record or AbstractionsSK.Struct)?.Name;
                if (!string.IsNullOrWhiteSpace(typeName))
                    fqn = $"{ns}.{typeName}.{name}({paramTypes})".TrimStart('.');
                else
                    fqn = $"{ns}.{name}({paramTypes})".TrimStart('.');
            }
            else if (parentStack.Count > 0)
            {
                var stackFqn = string.Join(".", parentStack.Reverse().Select(s => s.Name));
                fqn = $"{stackFqn}.{name}";
                ns ??= stackFqn.Split('.').FirstOrDefault();
            }
            else
            {
                fqn = string.IsNullOrWhiteSpace(ns) ? name : $"{ns}.{name}";
            }
        }

        if (string.IsNullOrWhiteSpace(signature))
            signature = node.ToString().Split('\n').FirstOrDefault()?.Trim();

        // Позиции
        var lineSpan = node.SyntaxTree.GetLineSpan(node.Span);
        int startLine = lineSpan.StartLinePosition.Line + 1;
        int endLine = lineSpan.EndLinePosition.Line + 1;
        int startCol = lineSpan.StartLinePosition.Character + 1;
        int endCol = lineSpan.EndLinePosition.Character + 1;
        if (startLine < 1) startLine = 1;
        if (endLine < 1) endLine = startLine;
        if (startCol < 1) startCol = 1;

        var dto = new Symbol(Guid.NewGuid(), SupportedLanguages.CSharp, kind, name, fqn, ns, parentId, startLine, endLine, startCol, endCol, signature);
        symbols.Add(dto);
        symbolMap[node] = dto;
        // Также маппим по identifier для полей? Но основная мапа уже
        if (roslynSymbol != null && !symbolByRoslyn.ContainsKey(roslynSymbol))
            symbolByRoslyn[roslynSymbol] = dto;

        // Relation Contains от parent к текущему
        // Создаётся после push/pop, но здесь parent уже в стеке
        // Мы добавим relation отдельно в VisitNode через parentStack.Peek, но здесь уже знаем parentId
        // Чтобы избежать дублирования, добавим Contains в вызывающем коде через VisitNode stack
        // Но здесь удобнее: если parentId != null, добавим Contains (нужен доступ к relations list) — передаём через parentStack, но не имеем relations.
        // Поэтому Contains будет добавлен в VisitNode после создания символа (там есть доступ к relations и parentStack)
        // Костыль: найдём parent символ и создадим relation через closure — но у нас нет relations здесь напрямую?
        // Мы передадим relations через другой параметр, но CreateSymbolForNode не имеет relations. Добавим после возврата в VisitNode.
        return dto;
    }

    // Переопределение VisitNode содержит добавление Contains после создания текущего символа
    // Патчим через расширение: после создания символа проверяем parentStack.Peek

    private static void ExtractSemanticRelations(
        SyntaxNode root,
        SemanticModel semanticModel,
        Dictionary<SyntaxNode, Symbol> symbolMap,
        List<Symbol> symbols,
        List<Relation> relations,
        List<AnalyzerError> errors,
        string filePath,
        CancellationToken ct)
    {
        // Contains уже частично добавлен через VisitNode stack — дополним если пропустили
        // Но теперь семантические Inherits/Implements/Calls/Creates/ReturnTypes etc.

        // Добавим Contains для всех parent-child где parentStack использовался (дополнительно проверим)
        // Уже сделано в VisitNode? Мы не добавляли Contains там полностью — добавим сейчас на основе родительских связей ParentSymbolId
        foreach (var s in symbols)
        {
            if (s.ParentSymbolId != null)
            {
                // Избежать дубликатов
                if (!relations.Any(r => r.FromSymbolId == s.ParentSymbolId && r.ToSymbolId == s.Id && r.RelationType == RelationType.Contains))
                    relations.Add(new Relation(s.ParentSymbolId.Value, s.Id, RelationType.Contains));
                // Также Defines (опционально) — Defines как file defines symbol? Но можем пропустить, Contains достаточно
            }
        }

        // Inheritance / Implements via BaseListSyntax
        var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
        foreach (var td in typeDeclarations)
        {
            if (td.BaseList == null) continue;
            if (!symbolMap.TryGetValue(td, out var fromSym)) continue;
            foreach (var baseType in td.BaseList.Types)
            {
                var typeInfo = semanticModel.GetTypeInfo(baseType.Type, ct);
                var baseSym = typeInfo.Type as INamedTypeSymbol;
                if (baseSym == null) continue;
                // Определить Implements vs Inherits: если baseSym.TypeKind == Interface => Implements иначе Inherits
                string relType = baseSym.TypeKind == TypeKind.Interface ? RelationType.Implements : RelationType.Inherits;
                // Найти target symbol среди текущего файла или внешних — если в этом файле есть symbol с таким FQN, используем его
                var targetFqn = baseSym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var targetDto = symbols.FirstOrDefault(s => s.Fqn == targetFqn);
                if (targetDto != null)
                {
                    relations.Add(new Relation(fromSym.Id, targetDto.Id, relType));
                }
                else
                {
                    // Внешний тип — можно создать external? Но пропускаем чтобы не плодить fake symbols
                    // Однако для теста generics/interfaces достаточно внутренних
                }
            }
        }

        // Calls / Creates / References via InvocationExpression, ObjectCreation
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var symInfo = semanticModel.GetSymbolInfo(inv, ct);
                var methodSym = symInfo.Symbol as IMethodSymbol;
                if (methodSym == null && symInfo.CandidateSymbols.Length > 0)
                    methodSym = symInfo.CandidateSymbols.FirstOrDefault() as IMethodSymbol;
                if (methodSym == null) continue;

                // Найти enclosing symbol (метод/класс) для From
                var enclosing = inv.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or ClassDeclarationSyntax);
                if (enclosing == null || !symbolMap.TryGetValue(enclosing, out var fromDto))
                {
                    // fallback: ближайший symbol по линии
                    var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    fromDto = symbols.Where(s => s.StartLine <= line && s.EndLine >= line).OrderByDescending(s => s.StartLine).FirstOrDefault();
                    if (fromDto == null) continue;
                }

                // Найти target DTO если такой метод в нашем файле
                var targetFqn = methodSym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var targetDto = symbols.FirstOrDefault(s => s.Fqn == targetFqn) ?? symbols.FirstOrDefault(s => s.Name == methodSym.Name);
                if (targetDto != null)
                {
                    relations.Add(new Relation(fromDto.Id, targetDto.Id, RelationType.Calls));
                }
                else
                {
                    // Внешний вызов — можем still создать relation к external? пропускаем
                }
            }
            catch { }
        }

        // ObjectCreation (new)
        var creations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        foreach (var oc in creations)
        {
            try
            {
                var typeInfo = semanticModel.GetTypeInfo(oc.Type, ct);
                var typeSym = typeInfo.Type as INamedTypeSymbol;
                if (typeSym == null) continue;
                var enclosing = oc.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or ConstructorDeclarationSyntax);
                if (enclosing == null || !symbolMap.TryGetValue(enclosing, out var fromDto)) continue;
                var targetFqn = typeSym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var targetDto = symbols.FirstOrDefault(s => s.Fqn == targetFqn);
                if (targetDto != null)
                    relations.Add(new Relation(fromDto.Id, targetDto.Id, RelationType.Creates));
            }
            catch { }
        }

        // Returns / ParameterType / FieldType / PropertyType
        // Для методов — ReturnType
        foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!symbolMap.TryGetValue(m, out var fromDto)) continue;
            try
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(m, ct) as IMethodSymbol;
                if (methodSymbol == null) continue;
                var retType = methodSymbol.ReturnType as INamedTypeSymbol;
                if (retType != null)
                {
                    var targetFqn = retType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var targetDto = symbols.FirstOrDefault(s => s.Fqn == targetFqn);
                    if (targetDto != null) relations.Add(new Relation(fromDto.Id, targetDto.Id, RelationType.Returns));
                }
                foreach (var p in methodSymbol.Parameters)
                {
                    var pType = p.Type as INamedTypeSymbol;
                    if (pType == null) continue;
                    var targetFqn = pType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var targetDto = symbols.FirstOrDefault(s => s.Fqn == targetFqn);
                    if (targetDto != null) relations.Add(new Relation(fromDto.Id, targetDto.Id, RelationType.ParameterType));
                }
            }
            catch { }
        }
    }

    private static void ExtractSyntaxCalls(SyntaxNode root, List<Symbol> symbols, List<Relation> relations, string filePath, List<AnalyzerError> errors)
    {
        // Fallback без SemanticModel: по имени
        var methods = symbols.Where(s => s.Kind == AbstractionsSK.Method || s.Kind == AbstractionsSK.Constructor).ToList();
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var inv in invocations)
        {
            var name = inv.Expression is MemberAccessExpressionSyntax maes ? maes.Name.Identifier.Text : (inv.Expression as IdentifierNameSyntax)?.Identifier.Text;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var target = methods.FirstOrDefault(m => m.Name == name);
            if (target == null) continue;
            // найти caller
            var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var caller = methods.FirstOrDefault(m => m.StartLine <= line && m.EndLine >= line);
            if (caller == null || caller.Id == target.Id) continue;
            relations.Add(new Relation(caller.Id, target.Id, RelationType.Calls));
        }
    }

    private static async Task TrySymbolFinderAsync(FileContext ctx, IReadOnlyList<MetadataReference> refs, Dictionary<ISymbol, Symbol> symbolByRoslyn, List<Relation> relations, List<AnalyzerError> errors, CancellationToken ct)
    {
        // Создаём AdhocWorkspace с одним документом для SymbolFinder
        try
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var docId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "Adhoc", "Adhoc", LanguageNames.CSharp)
                .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithAllowUnsafe(true).WithNullableContextOptions(NullableContextOptions.Enable))
                .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Latest))
                .AddMetadataReferences(projectId, refs)
                .AddDocument(docId, ctx.FilePath, SourceText.From(ctx.Content));
            if (!workspace.TryApplyChanges(solution)) return;

            var doc = workspace.CurrentSolution.GetDocument(docId);
            if (doc == null) return;
            var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (semanticModel == null) return;
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) return;

            // Для каждого интерфейса — FindImplementationsAsync
            var interfaceSymbols = symbolByRoslyn.Keys.OfType<INamedTypeSymbol>().Where(s => s.TypeKind == TypeKind.Interface).ToList();
            foreach (var iface in interfaceSymbols)
            {
                try
                {
                    var impls = await SymbolFinder.FindImplementationsAsync(iface, workspace.CurrentSolution, cancellationToken: ct).ConfigureAwait(false);
                    foreach (var impl in impls)
                    {
                        if (symbolByRoslyn.TryGetValue(impl, out var implDto) && symbolByRoslyn.TryGetValue(iface, out var ifaceDto))
                        {
                            // Implements уже добавлен via inheritance, но дополним если нет
                            if (!relations.Any(r => r.FromSymbolId == implDto.Id && r.ToSymbolId == ifaceDto.Id && r.RelationType == RelationType.Implements))
                                relations.Add(new Relation(implDto.Id, ifaceDto.Id, RelationType.Implements));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new AnalyzerError(ctx.FilePath, $"FindImplementations failed for {iface.Name}: {ex.Message}"));
                }
            }

            // Для методов — FindReferencesAsync (Calls/References)
            var methodSymbols = symbolByRoslyn.Keys.OfType<IMethodSymbol>().Take(5).ToList(); // ограничим чтобы не тяжело
            foreach (var m in methodSymbols)
            {
                try
                {
                    var refsFound = await SymbolFinder.FindReferencesAsync(m, workspace.CurrentSolution, cancellationToken: ct).ConfigureAwait(false);
                    int locCount = refsFound.Sum(r => r.Locations.Count());
                    // Можно логировать, но не создавать новые реляции — уже есть Calls
                    if (locCount > 0)
                    {
                        // optional debug
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new AnalyzerError(ctx.FilePath, $"FindReferences failed for {m.Name}: {ex.Message}"));
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"SymbolFinder workspace failed: {ex.Message}"));
        }
    }

    // Переопределение VisitNode чтобы добавить Contains после создания символа (т.к. CreateSymbolForNode не имела доступа к relations)
    // Мы патчим исходный VisitNode: после создания currentSymbol добавляем Contains relation если parent существует
    // Для этого нужно изменить VisitNode выше — уже вставлена логика, но Contains добавляется в ExtractSemanticRelations как fallback
    // Дополнительно добавим Contains непосредственно в VisitNode рекурсии ниже
    // Чтобы избежать дублирования, ExtractSemanticRelations уже добавляет Contains на основе ParentSymbolId
}
