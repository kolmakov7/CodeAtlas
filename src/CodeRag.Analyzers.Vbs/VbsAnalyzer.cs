using System.Text.RegularExpressions;
using CodeRag.Analyzers.Abstractions;

namespace CodeRag.Analyzers.Vbs;

/// <summary>
/// VBS/VBScript анализатор — Regex fallback (?im)^(Sub|Function|Class|Property)\s+(\w+), syntax/structure.
/// </summary>
public sealed class VbsAnalyzer : ICodeAnalyzer
{
    public string Language => SupportedLanguages.Vbs;

    // Требуемый ТЗ паттерн (?im)^(Sub|Function|Class|Property)\s+(\w+)
    // Расширяем для Public/Private и отступов, сохраняя совместимость
    private static readonly Regex VbsRegex = new(
        @"(?im)^\s*(?:Public\s+|Private\s+)?(Sub|Function|Class|Property)\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex EndClassRegex = new(@"(?im)^\s*End\s+Class\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex EndSubRegex = new(@"(?im)^\s*End\s+(Sub|Function|Property)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public bool CanAnalyze(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".vbs" or ".vb" or ".vba" or ".bas";
    }

    public Task<AnalyzeResult> AnalyzeAsync(FileContext ctx, CancellationToken ct)
    {
        var symbols = new List<Symbol>();
        var relations = new List<Relation>();
        var chunks = new List<Chunk>();
        var errors = new List<AnalyzerError>();

        if (string.IsNullOrWhiteSpace(ctx.Content))
            return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));

        try
        {
            var content = ctx.Content;
            var lines = content.Split('\n');
            var matches = VbsRegex.Matches(content);

            if (matches.Count == 0)
            {
                // syntax/structure — если ничего не нашли, fallback sliding window и AnalyzerError как info
                if (!string.IsNullOrWhiteSpace(content.Trim()))
                    errors.Add(new AnalyzerError(ctx.FilePath, "VBS: no Sub/Function/Class/Property found (syntax/structure fallback)"));
                var fb = ChunkingHelper.SlidingWindow(content, SupportedLanguages.Vbs);
                chunks.AddRange(fb);
                return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
            }

            // Собираем классы для определения parent
            var classSymbols = new List<(Match m, Symbol s, int endLine)>();

            // Первый проход: создаём символы с линиями
            foreach (Match m in matches)
            {
                ct.ThrowIfCancellationRequested();
                var kindRaw = m.Groups[1].Value;
                var name = m.Groups[2].Value;

                var kind = kindRaw.ToLowerInvariant() switch
                {
                    "sub" => SymbolKind.Sub,
                    "function" => SymbolKind.Function,
                    "class" => SymbolKind.Class,
                    "property" => SymbolKind.Property,
                    _ => SymbolKind.Function
                };

                int startLine = ChunkingHelper.GetLineFromOffset(content, m.Index);
                int endLine = FindEndLine(content, m, startLine, kind);
                if (endLine < startLine) endLine = startLine;

                int startCol = 1;
                // Попытка вычислить колонку: offset - last newline
                var lastNl = content.LastIndexOf('\n', m.Index);
                startCol = m.Index - lastNl;

                string? signature = m.Value.Trim();
                if (signature.Length > 200) signature = signature[..200];

                // FQN via stack — пока flat, уточним во втором проходе для методов внутри классов
                var symbol = new Symbol(Guid.NewGuid(), SupportedLanguages.Vbs, kind, name, name, null, null, startLine, endLine, startCol, startCol + name.Length, signature);
                symbols.Add(symbol);

                if (kind == SymbolKind.Class)
                    classSymbols.Add((m, symbol, endLine));
            }

            // Второй проход: назначаем ParentSymbolId и FQN для вложенных Sub/Function внутри Class
            // Для каждого не-Class символа проверяем попадает ли его StartLine внутрь класса
            foreach (var s in symbols.Where(s => s.Kind != SymbolKind.Class).ToList())
            {
                var parent = classSymbols.FirstOrDefault(c =>
                {
                    var classStart = c.s.StartLine;
                    var classEnd = c.endLine;
                    return s.StartLine > classStart && s.EndLine <= classEnd;
                });
                if (parent.s != null)
                {
                    // Обновляем символ с parentId и FQN
                    var idx = symbols.IndexOf(s);
                    var newFqn = $"{parent.s.Name}.{s.Name}";
                    var newNs = parent.s.Name;
                    var updated = new Symbol(s.Id, s.Language, s.Kind, s.Name, newFqn, newNs, parent.s.Id, s.StartLine, s.EndLine, s.StartColumn, s.EndColumn, s.Signature);
                    symbols[idx] = updated;

                    // Contains relation
                    relations.Add(new Relation(parent.s.Id, s.Id, RelationType.Contains));
                }
                else
                {
                    // Корректируем FQN на уровне модуля (без namespace)
                    // Уже корректно
                }
            }

            // Чанки: семантические единицы sub/function/class — по одному объекту
            foreach (var s in symbols)
            {
                var source = ChunkingHelper.ExtractLines(content, s.StartLine, s.EndLine);
                if (string.IsNullOrWhiteSpace(source))
                    source = s.Signature ?? s.Name;
                source = source.Trim();
                if (string.IsNullOrWhiteSpace(source)) continue;
                try
                {
                    var chunk = ChunkingHelper.CreateChunk(s.Id, SupportedLanguages.Vbs, s.StartLine, s.EndLine, source);
                    chunks.Add(chunk);
                }
                catch (Exception ex)
                {
                    errors.Add(new AnalyzerError(ctx.FilePath, $"Chunk failed for {s.Name}: {ex.Message}", s.StartLine, s.StartColumn));
                }
            }

            // Если чанков всё ещё 0 — fallback
            if (chunks.Count == 0)
            {
                var fb = ChunkingHelper.SlidingWindow(content, SupportedLanguages.Vbs);
                chunks.AddRange(fb);
            }

            // Contains уже добавлен выше; дополнительно проверим дубли
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"VBS analyzer failed: {ex.Message}"));
            if (chunks.Count == 0)
            {
                var fb = ChunkingHelper.SlidingWindow(ctx.Content, SupportedLanguages.Vbs);
                chunks.AddRange(fb);
            }
        }

        return Task.FromResult(new AnalyzeResult(symbols, relations, chunks, errors));
    }

    private static int FindEndLine(string content, Match m, int startLine, string kind)
    {
        // Для Class ищем End Class, для Sub/Function/Property — End Sub/Function/Property
        string searchPattern;
        if (kind == SymbolKind.Class)
            searchPattern = @"(?im)^\s*End\s+Class\b";
        else if (kind == SymbolKind.Sub)
            searchPattern = @"(?im)^\s*End\s+Sub\b";
        else if (kind == SymbolKind.Function)
            searchPattern = @"(?im)^\s*End\s+Function\b";
        else if (kind == SymbolKind.Property)
            searchPattern = @"(?im)^\s*End\s+Property\b";
        else
            searchPattern = @"(?im)^\s*End\s+(Sub|Function|Class|Property)\b";

        var regex = new Regex(searchPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var subContent = content[m.Index..];
        var endMatch = regex.Match(subContent);
        if (endMatch.Success)
        {
            int absoluteIdx = m.Index + endMatch.Index + endMatch.Length;
            // Конец — строка где End найден
            return ChunkingHelper.GetLineFromOffset(content, absoluteIdx);
        }

        // Fallback: до следующего символа того же уровня или конца файла
        // Ищем следующий match после текущего
        var next = VbsRegex.Match(content, m.Index + m.Length);
        if (next.Success)
        {
            return ChunkingHelper.GetLineFromOffset(content, next.Index) - 1;
        }

        // До конца файла
        return content.Split('\n').Length;
    }
}
