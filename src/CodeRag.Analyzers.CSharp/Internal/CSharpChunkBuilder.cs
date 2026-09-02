using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.Abstractions.Chunking;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using AbstractionsSK = CodeRag.Analyzers.Abstractions.SymbolKind;

namespace CodeRag.Analyzers.CSharp.Internal;

internal sealed class CSharpChunkBuilder
{
    private readonly IChunkFactory _factory;
    private readonly ISourceSlicer _slicer;

    public CSharpChunkBuilder(IChunkFactory factory, ISourceSlicer slicer)
    {
        _factory = factory;
        _slicer = slicer;
    }

    private static string Flat(SyntaxNode n) => n.ToString().ReplaceLineEndings(" ");

    public void AddChunkForSymbol(
        List<Chunk> chunks,
        FileContext ctx,
        Symbol symbol,
        SyntaxNode node,
        string? namespaceName,
        IReadOnlyList<CSharpTypeContext> typeChain,
        int budget,
        List<AnalyzerError> errors)
    {
        try
        {
            if (node is EnumDeclarationSyntax)
                AddWholeNodeChunk(chunks, ctx, symbol, node);
            else if (node is TypeDeclarationSyntax td)
                AddTypeSkeletonChunk(chunks, ctx, symbol, td, namespaceName, typeChain, budget);
            else
                AddMethodChunk(chunks, ctx, symbol, node, namespaceName, typeChain, budget);
        }
        catch (Exception ex)
        {
            errors.Add(new AnalyzerError(ctx.FilePath, $"Chunk failed for {symbol.Name}: {ex.Message}"));
        }
    }

    private void AddWholeNodeChunk(List<Chunk> chunks, FileContext ctx, Symbol symbol, SyntaxNode node)
    {
        var source = node.ToFullString().TrimStart();
        if (string.IsNullOrWhiteSpace(source)) return;
        var ls = node.SyntaxTree.GetLineSpan(node.Span);
        var startLine = Math.Max(1, ls.StartLinePosition.Line + 1);
        var endLine = Math.Max(startLine, ls.EndLinePosition.Line + 1);
        chunks.Add(_factory.Create(symbol.Id, SupportedLanguages.CSharp, startLine, endLine, source));
    }

    private static string TypeDeclarationLine(TypeDeclarationSyntax t)
    {
        var parts = new List<string>();
        foreach (var m in t.Modifiers) parts.Add(m.ToString());
        parts.Add(t.Keyword.ToString());
        parts.Add(t.Identifier.Text);
        if (t.TypeParameterList is { Parameters.Count: > 0 } tp) parts.Add(Flat(tp));
        if (t.ParameterList is { Parameters.Count: > 0 } pl) parts.Add(Flat(pl));
        if (t.BaseList is { Types.Count: > 0 } bl) parts.Add(Flat(bl));
        if (t.ConstraintClauses.Count > 0) parts.Add(t.ConstraintClauses.ToString().ReplaceLineEndings(" "));
        return string.Join(" ", parts).Trim();
    }

    private static string MemberSignature(SyntaxNode member)
    {
        var text = member.ToFullString().TrimStart();
        var idx = text.IndexOf('{');
        if (idx >= 0) text = text[..idx];
        return text.ReplaceLineEndings(" ").Trim();
    }

    private void AddTypeSkeletonChunk(List<Chunk> chunks, FileContext ctx, Symbol symbol, TypeDeclarationSyntax td, string? ns, IReadOnlyList<CSharpTypeContext> outerChain, int budget)
    {
        var ls = td.SyntaxTree.GetLineSpan(td.Span);
        var startLine = Math.Max(1, ls.StartLinePosition.Line + 1);
        var endLine = Math.Max(startLine, ls.EndLinePosition.Line + 1);

        var head = new List<string>();
        if (!string.IsNullOrWhiteSpace(ns)) head.Add($"namespace {ns};");
        foreach (var t in outerChain) head.Add(t.DeclarationLine);
        head.Add(TypeDeclarationLine(td));
        head.Add("{");

        var fields = new List<string>();
        var sigs = new List<string>();
        foreach (var m in td.Members)
        {
            switch (m)
            {
                case FieldDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax:
                    fields.Add(Flat(m).Trim());
                    break;
                case BaseMethodDeclarationSyntax bm: sigs.Add(MemberSignature(bm)); break;
                case DelegateDeclarationSyntax d: sigs.Add(Flat(d).Trim()); break;
                case TypeDeclarationSyntax nested: sigs.Add(TypeDeclarationLine(nested) + ";"); break;
            }
        }

        var headText = string.Join("\n", head);
        var cap = budget - headText.Length - 3;
        var body = new List<string>();
        int used = 0, skipped = 0;
        foreach (var t in fields.Concat(sigs))
        {
            if (used + t.Length + 1 <= cap) { body.Add(t); used += t.Length + 1; }
            else skipped++;
        }
        if (skipped > 0) body.Add($"// ... ({skipped} more members)");
        var source = headText + "\n" + string.Join("\n", body) + "\n}";
        if (string.IsNullOrWhiteSpace(source)) return;
        chunks.Add(_factory.Create(symbol.Id, SupportedLanguages.CSharp, startLine, endLine, source));
    }

    private void AddMethodChunk(List<Chunk> chunks, FileContext ctx, Symbol symbol, SyntaxNode node, string? ns, IReadOnlyList<CSharpTypeContext> chain, int budget)
    {
        var fullSource = node.ToFullString().TrimStart();
        var spanSource = SpanToText(ctx.Content, node.Span);
        var source = string.IsNullOrWhiteSpace(spanSource) ? fullSource : spanSource;
        if (string.IsNullOrWhiteSpace(source)) return;
        var ls = node.SyntaxTree.GetLineSpan(node.Span);
        var startLine = Math.Max(1, ls.StartLinePosition.Line + 1);
        var endLine = Math.Max(startLine, ls.EndLinePosition.Line + 1);

        var contextParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ns)) contextParts.Add($"namespace {ns};");
        foreach (var t in chain) contextParts.Add(t.DeclarationLine);

        var contextText = string.Join("\n", contextParts);
        if (contextText.Length > budget - 512 && contextParts.Count > 2)
        {
            contextParts = [contextParts[0], contextParts[^1]];
            contextText = string.Join("\n", contextParts);
        }

        int window = budget - contextText.Length - 4;
        if (window < 256) window = 256;

        if (fullSource.Length <= window)
        {
            var text = contextText.Length > 0 ? $"{contextText}\n{{\n{fullSource}\n}}" : fullSource;
            chunks.Add(_factory.Create(symbol.Id, SupportedLanguages.CSharp, startLine, endLine, text));
            return;
        }

        var header = BuildContextHeader(ctx, ns, chain, symbol);
        var subWindow = Math.Max(256, budget - header.Length - 2);
        var overlap = Math.Max(64, (int)(subWindow * 0.15));
        var slices = _slicer.SplitByCharBudget(source, startLine, endLine, subWindow, overlap, header).ToList();
        if (slices.Count == 0) return;
        var first = slices[0];
        var firstText = contextText.Length > 0
            ? $"{contextText}\n{{\n{TruncateToBudget(first.Text, budget - contextText.Length - 4)}\n}}"
            : TruncateToBudget(first.Text, budget - 1);
        chunks.Add(_factory.Create(symbol.Id, SupportedLanguages.CSharp, first.StartLine, first.EndLine, firstText));
        for (var i = 1; i < slices.Count; i++)
            chunks.Add(_factory.Create(symbol.Id, SupportedLanguages.CSharp, slices[i].StartLine, slices[i].EndLine, slices[i].Text));
    }

    private static string SpanToText(string content, TextSpan span)
    {
        if (span.Start < 0 || span.Start >= content.Length) return string.Empty;
        return content.Substring(span.Start, Math.Min(span.Length, content.Length - span.Start));
    }

    private static string TruncateToBudget(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, max - 1));
        if (cut <= 0) cut = max;
        return text[..cut];
    }

    private static string BuildContextHeader(FileContext ctx, string? ns, IReadOnlyList<CSharpTypeContext> chain, Symbol symbol)
    {
        var typeName = chain.Count > 0 ? string.Join(".", chain.Select(t => t.Name)) : null;
        var qual = ns != null ? (typeName != null ? $"{ns}.{typeName}" : ns) : (typeName ?? symbol.Fqn);
        return $"// [continued] {ctx.FilePath} :: {qual}.{symbol.Name}";
    }


}
