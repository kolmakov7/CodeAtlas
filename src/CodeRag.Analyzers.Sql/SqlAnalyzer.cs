using System.Text.RegularExpressions;
using CodeRag.Analyzers.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace CodeRag.Analyzers.Sql;

/// <summary>
/// SQL анализатор — ScriptDom TSql170Parser + TSqlFragmentVisitor (§15).
/// Процедуры/функции/views/tables, Defines/Contains, требует GO разделитель.
/// </summary>
public sealed class SqlAnalyzer : ICodeAnalyzer
{
    public string Language => SupportedLanguages.Sql;

    public bool CanAnalyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".sql" or ".tsql";
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
        var createCount = Regex.Matches(ctx.Content, @"(?im)^\s*CREATE\s+(PROCEDURE|FUNCTION|VIEW|TABLE|TRIGGER|PROC)").Count;
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
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
                chunks.AddRange(fb);
                return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"SQL parse failed: {ex.Message}"));
            var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
            chunks.AddRange(fb);
            return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
        }

        try
        {
            var visitor = new SqlVisitor(ctx, symbols, chunks, relations, errors);
            fragment.Accept(visitor);

            // Relations Contains уже добавлены внутри visitor для batch? Дополним Contains на основе ParentSymbolId
            foreach (var s in symbols)
            {
                if (s.ParentSymbolId != null && !relations.Any(r => r.FromSymbolId == s.ParentSymbolId && r.ToSymbolId == s.Id && r.RelationType == RelationType.Contains))
                    relations.Add(new Relation(s.ParentSymbolId.Value, s.Id, RelationType.Contains));
            }

            if (chunks.Count == 0 && symbols.Count > 0)
            {
                // Если по какой-то причине чанки не создались — создадим по символам
                foreach (var s in symbols)
                {
                    var src = ChunkingHelper.ExtractLines(ctx.Content, s.StartLine, s.EndLine);
                    if (!string.IsNullOrWhiteSpace(src))
                        chunks.Add(ChunkingHelper.CreateChunk(s.Id, SupportedLanguages.Sql, s.StartLine, s.EndLine, src));
                }
            }

            if (chunks.Count == 0)
            {
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
                chunks.AddRange(fb);
            }
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"SQL visitor failed: {ex.Message}"));
            if (chunks.Count == 0)
            {
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.Sql);
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

        public SqlVisitor(FileContext ctx, List<Symbol> symbols, List<Chunk> chunks, List<Relation> relations, List<AnalyzerError> errors)
        {
            _ctx = ctx;
            _symbols = symbols;
            _chunks = chunks;
            _relations = relations;
            _errors = errors;
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
                int endCol = startCol + 1;

                // Source slice via offsets
                string source = ExtractSource(node);
                int endLine = startLine + source.Split('\n').Length - 1;
                if (endLine < startLine) endLine = startLine;

                var symbol = new Symbol(Guid.NewGuid(), language, kind, name!, fqn, ns, null, startLine, endLine, startCol, endCol, $"{kind} {fqn}");
                _symbols.Add(symbol);

                if (!string.IsNullOrWhiteSpace(source))
                {
                    try
                    {
                        var chunk = ChunkingHelper.CreateChunk(symbol.Id, language, startLine, endLine, source);
                        _chunks.Add(chunk);
                    }
                    catch (Exception ex)
                    {
                        _errors.Add(new AnalyzerError(_ctx.FilePath, $"Chunk failed for {fqn}: {ex.Message}"));
                    }
                }

                // Для SQL Contains не нужен parent, но если есть схема — можно добавить? Пропускаем
            }
            catch (Exception ex)
            {
                _errors.Add(new AnalyzerError(_ctx.FilePath, $"HandleProcedure failed: {ex.Message}"));
            }
        }

        private string ExtractSource(TSqlFragment node)
        {
            try
            {
                var start = node.StartOffset;
                var len = node.FragmentLength;
                if (start < 0 || len <= 0 || start + len > _ctx.Content.Length)
                {
                    // Fallback via lines
                    int sl = GetStartLine(node);
                    int el = GetEndLine(node, sl);
                    return ChunkingHelper.ExtractLines(_ctx.Content, sl, el);
                }
                return _ctx.Content.Substring(start, len).Trim();
            }
            catch
            {
                int sl = GetStartLine(node);
                int el = GetEndLine(node, sl);
                return ChunkingHelper.ExtractLines(_ctx.Content, sl, el);
            }
        }

        private static int GetStartLine(TSqlFragment node)
        {
            try { return node.StartLine; } catch { return 1; }
        }
        private static int GetEndLine(TSqlFragment node, int fallbackStart)
        {
            // ScriptDom не всегда даёт EndLine — аппроксимируем
            try
            {
                // Пытаемся через reflection найти EndLine или используем StartOffset+FragmentLength
                var prop = node.GetType().GetProperty("StartLine");
                // Если есть FragmentLength, считаем строки внутри фрагмента
                // Но проще: если есть свойство через ToString count
                return fallbackStart; // пока fallback
            }
            catch { return fallbackStart; }
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
