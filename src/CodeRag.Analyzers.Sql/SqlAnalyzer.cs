using System.Text.RegularExpressions;
using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.Abstractions.Chunking;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace CodeRag.Analyzers.Sql;

/// <summary>
/// SQL анализатор — ScriptDom TSql170Parser + TSqlFragmentVisitor (§15).
/// Процедуры/функции/views/tables, Defines/Contains, требует GO разделитель.
/// </summary>
public sealed class SqlAnalyzer : ICodeAnalyzer
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql",
        ".tsql"
    };

    private readonly IChunkFactory _factory;
    private readonly ISourceSlicer _slicer;

    public SqlAnalyzer() : this(new DefaultChunkFactory(), null!) { }

    public SqlAnalyzer(IChunkFactory factory, ISourceSlicer slicer)
    {
        _factory = factory ?? new DefaultChunkFactory();
        _slicer = slicer ?? new DefaultSourceSlicer(_factory);
    }

    public string Language => SupportedLanguages.Sql;

    public bool CanAnalyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return SupportedExtensions.Contains(Path.GetExtension(filePath));
    }

    public Task<AnalyzeResult> AnalyzeAsync(FileContext ctx, CancellationToken ct)
    {
        var symbols = new List<Symbol>();
        var relations = new List<Relation>();
        var chunks = new List<Chunk>();
        var errors = new List<AnalyzerError>();

        if (string.IsNullOrWhiteSpace(ctx.Content))
            return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));

        // Проверка GO разделителя — по ТЗ требует GO; логируем если отсутствует при множественных CREATE
        var hasGo = Regex.IsMatch(ctx.Content, @"(?im)^\s*GO\s*$");
        var createCount = Regex.Matches(ctx.Content, @"(?im)^\s*CREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|FUNCTION|VIEW|TABLE|TRIGGER|PROC)").Count;
        if (createCount > 1 && !hasGo)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, "SQL: множественные CREATE без GO разделителя — парсинг может быть неточным (требуется GO между батчами)", 1, 1));
        }

        TSqlScript? fragment = null;
        IList<ParseError> parseErrors = Array.Empty<ParseError>();
        try
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(ctx.Content);
            fragment = parser.Parse(reader, out parseErrors) as TSqlScript;
            if (parseErrors.Count > 0)
            {
                foreach (var e in parseErrors.Take(10))
                    errors.Add(new AnalyzerError(ctx.FilePath, e.Message, e.Line, e.Column));
                // Не фейлим — продолжаем с частичными результатами
            }
            if (fragment == null)
            {
                errors.Add(new AnalyzerError(ctx.FilePath, "ScriptDom Parse вернул null"));
                var fb = _slicer.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
                chunks.AddRange(fb);
                return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"SQL parse failed: {ex.Message}"));
            var fb = _slicer.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
            chunks.AddRange(fb);
            return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
        }

        try
        {
            var visitor = new SqlVisitor(ctx, symbols, chunks, relations, errors, _factory, _slicer);
            fragment.Accept(visitor);
            visitor.EmitChunks();

            // Relations Contains уже добавлены visitor'ом для вложенных определений; дополним по ParentSymbolId
            foreach (var s in symbols)
            {
                if (s.ParentSymbolId != null && !relations.Any(r => r.FromSymbolId == s.ParentSymbolId && r.ToSymbolId == s.Id && r.RelationType == RelationType.Contains))
                    relations.Add(new Relation(s.ParentSymbolId.Value, s.Id, RelationType.Contains));
            }

            if (chunks.Count == 0 && symbols.Count > 0)
            {
                foreach (var s in symbols)
                {
                    var src = _slicer.ExtractLines(ctx.Content, s.StartLine, s.EndLine);
                    if (!string.IsNullOrWhiteSpace(src))
                        chunks.Add(_factory.Create(s.Id, SupportedLanguages.Sql, s.StartLine, s.EndLine, src));
                }
            }

            if (chunks.Count == 0)
            {
                var fb = _slicer.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
                chunks.AddRange(fb);
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"SQL visitor failed: {ex.Message}"));
            if (chunks.Count == 0)
            {
                var fb = _slicer.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
                chunks.AddRange(fb);
            }
        }

        return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
    }

    private sealed class SqlVisitor : TSqlFragmentVisitor
    {
        private readonly FileContext _ctx;
        private readonly List<Symbol> _symbols;
        private readonly List<Chunk> _chunks;
        private readonly List<Relation> _relations;
        private readonly List<AnalyzerError> _errors;
        private readonly IChunkFactory _factory;
        private readonly ISourceSlicer _slicer;
        private readonly List<StatementCandidate> _candidates = new();

        public SqlVisitor(FileContext ctx, List<Symbol> symbols, List<Chunk> chunks, List<Relation> relations, List<AnalyzerError> errors, IChunkFactory factory, ISourceSlicer slicer)
        {
            _ctx = ctx;
            _symbols = symbols;
            _chunks = chunks;
            _relations = relations;
            _errors = errors;
            _factory = factory;
            _slicer = slicer;
        }

        /// <summary>Кандидат на чанк: объект DDL/создания с диапазоном в байтах-символах.</summary>
        private sealed class StatementCandidate
        {
            public Symbol? Symbol;
            public int StartOffset = -1;
            public int EndOffset = -1;
        }

        /// <summary>
        /// Второй проход: источник чанка = фрагмент объекта минус диапазоны вложенных кандидатов
        /// (тот же центральный принцип исключения вложенных чанков, что и в TreeSitter-анализаторе).
        /// </summary>
        public void EmitChunks()
        {
            foreach (var cand in _candidates)
            {
                if (cand.Symbol == null) continue;
                var source = BuildCandidateSource(cand);
                if (!string.IsNullOrWhiteSpace(source))
                {
                    try
                    {
                        var chunk = _factory.Create(cand.Symbol.Id, SupportedLanguages.Sql, cand.Symbol.StartLine, cand.Symbol.EndLine, source);
                        _chunks.Add(chunk);
                    }
                    catch (Exception ex)
                    {
                        _errors.Add(new AnalyzerError(_ctx.FilePath, $"Chunk failed: {ex.Message}"));
                    }
                }

                // Вложенные определения (напр. CREATE TABLE #temp внутри процедуры) — relation Contains
                foreach (var child in GetChildren(cand))
                    _relations.Add(new Relation(cand.Symbol.Id, child.Symbol!.Id, RelationType.Contains));
            }
        }

        private List<StatementCandidate> GetChildren(StatementCandidate cand)
        {
            if (cand.StartOffset < 0) return new List<StatementCandidate>();
            return _candidates
                .Where(c => !ReferenceEquals(c, cand) && c.StartOffset >= cand.StartOffset && c.EndOffset <= cand.EndOffset)
                .OrderBy(c => c.StartOffset).ThenBy(c => c.EndOffset)
                .ToList();
        }

        private string BuildCandidateSource(StatementCandidate cand)
        {
            var content = _ctx.Content;
            if (cand.StartOffset < 0 || cand.EndOffset < 0 || cand.EndOffset > content.Length)
                return _slicer.ExtractLines(content, cand.Symbol!.StartLine, cand.Symbol.EndLine);

            var sb = new System.Text.StringBuilder(cand.EndOffset - cand.StartOffset);
            var cursor = cand.StartOffset;
            foreach (var child in GetChildren(cand))
            {
                if (child.StartOffset > cursor)
                    sb.Append(content.Substring(cursor, child.StartOffset - cursor));
                if (child.EndOffset > cursor)
                    cursor = child.EndOffset;
            }
            if (cand.EndOffset > cursor)
                sb.Append(content.Substring(cursor, cand.EndOffset - cursor));

            // Degenerate: всё исключено — оставляем полный фрагмент
            return sb.Length > 0 ? sb.ToString().Trim() : content.Substring(cand.StartOffset, cand.EndOffset - cand.StartOffset).Trim();
        }

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            HandleProcedure(node, node.ProcedureReference, SupportedLanguages.Sql, SymbolKind.Procedure);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.Name), SupportedLanguages.Sql, SymbolKind.Function);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.SchemaObjectName), SupportedLanguages.Sql, SymbolKind.View);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.SchemaObjectName), SupportedLanguages.Sql, SymbolKind.Table);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            // Trigger: имя в node.Name
            HandleProcedure(node, new SchemaObjectNameWrapper(node.Name), SupportedLanguages.Sql, SymbolKind.Trigger);
            base.ExplicitVisit(node);
        }

        // CREATE OR ALTER (SQL Server 2016+) — ScriptDom выдаёт отдельные типы узлов
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
        {
            HandleProcedure(node, node.ProcedureReference, SupportedLanguages.Sql, SymbolKind.Procedure);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.Name), SupportedLanguages.Sql, SymbolKind.Function);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterViewStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.SchemaObjectName), SupportedLanguages.Sql, SymbolKind.View);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.Name), SupportedLanguages.Sql, SymbolKind.Trigger);
            base.ExplicitVisit(node);
        }

        // Также поддержим ALTER варианты — они тоже определяют объекты
        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            HandleProcedure(node, node.ProcedureReference, SupportedLanguages.Sql, SymbolKind.Procedure);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.Name), SupportedLanguages.Sql, SymbolKind.Function);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterViewStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.SchemaObjectName), SupportedLanguages.Sql, SymbolKind.View);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            HandleProcedure(node, new SchemaObjectNameWrapper(node.Name), SupportedLanguages.Sql, SymbolKind.Trigger);
            base.ExplicitVisit(node);
        }

        private void HandleProcedure(TSqlFragment node, object? nameObj, string language, string kind)
        {
            try
            {
                string? name = null;
                string? schema = null;
                string? fqn = null;

                if (nameObj is SchemaObjectName son)
                {
                    name = son.BaseIdentifier?.Value;
                    if (son.SchemaIdentifier != null) schema = son.SchemaIdentifier.Value;
                    else if (son.Identifiers.Count == 2) schema = son.Identifiers[0].Value;
                    // Многочастное имя: [db].[schema].[name]
                    if (string.IsNullOrWhiteSpace(name) && son.Identifiers.Count > 0)
                        name = son.Identifiers[^1].Value;
                    if (string.IsNullOrWhiteSpace(schema) && son.Identifiers.Count >= 2)
                        schema = son.Identifiers[^2].Value;
                }
                else if (nameObj is ProcedureReference pr)
                {
                    var sname = pr.Name;
                    name = sname?.BaseIdentifier?.Value;
                    if (sname?.SchemaIdentifier != null) schema = sname.SchemaIdentifier.Value;
                    else if (sname != null && sname.Identifiers.Count == 2) schema = sname.Identifiers[0].Value;
                    if (string.IsNullOrWhiteSpace(name) && sname != null && sname.Identifiers.Count > 0)
                        name = sname.Identifiers[^1].Value;
                }
                else if (nameObj is SchemaObjectNameWrapper w && w.Inner != null)
                {
                    var inner = w.Inner;
                    name = inner.BaseIdentifier?.Value;
                    if (inner.SchemaIdentifier != null) schema = inner.SchemaIdentifier.Value;
                    else if (inner.Identifiers.Count == 2) schema = inner.Identifiers[0].Value;
                    if (string.IsNullOrWhiteSpace(name) && inner.Identifiers.Count > 0)
                        name = inner.Identifiers[^1].Value;
                }

                if (string.IsNullOrWhiteSpace(name)) name = "unknown";

                fqn = string.IsNullOrWhiteSpace(schema) ? name : $"{schema}.{name}";
                var ns = schema;

                // Позиции — ScriptDom хранит StartLine/StartColumn/StartOffset
                int startLine = GetStartLine(node);
                int startCol = GetStartColumn(node);
                int endLine = startLine;
                int endCol = startCol + 1;

                // Точные end line/col выводим из StartOffset+FragmentLength; диапазон кандидата для 2-го прохода
                var startOffset = node.StartOffset;
                var fragLen = node.FragmentLength;
                var validOffset = startOffset >= 0 && fragLen > 0 && startOffset + fragLen <= _ctx.Content.Length;
                if (validOffset)
                {
                    endLine = 1;
                    endCol = 1;
                    for (var i = 0; i < startOffset + fragLen; i++)
                    {
                        if (_ctx.Content[i] == '\n')
                        {
                            endLine++;
                            endCol = 1;
                        }
                        else
                            endCol++;
                    }
                }

                var symbol = new Symbol(Guid.NewGuid(), language, kind, name!, fqn, ns, null, startLine, endLine, startCol, endCol, $"{kind} {fqn}");
                _symbols.Add(symbol);
                _candidates.Add(new StatementCandidate
                {
                    Symbol = symbol,
                    StartOffset = validOffset ? startOffset : -1,
                    EndOffset = validOffset ? startOffset + fragLen : -1,
                });
            }
            catch (Exception ex)
            {
                _errors.Add(new AnalyzerError(_ctx.FilePath, $"HandleProcedure failed: {ex.Message}"));
            }
        }

        private static int GetStartLine(TSqlFragment node)
        {
            try { return node.StartLine; } catch { return 1; }
        }
        private static int GetStartColumn(TSqlFragment node)
        {
            try { return node.StartColumn; } catch { return 1; }
        }

        // Wrapper для унификации SchemaObjectName vs ProcedureReference
        private sealed class SchemaObjectNameWrapper
        {
            public SchemaObjectName? Inner { get; }
            public SchemaObjectNameWrapper(SchemaObjectName? inner) => Inner = inner;
        }
    }
}
