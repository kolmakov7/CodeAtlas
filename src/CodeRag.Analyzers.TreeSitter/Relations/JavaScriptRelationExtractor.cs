using CodeRag.Analyzers.Abstractions;

namespace CodeRag.Analyzers.TreeSitter.Relations;

public sealed class JavaScriptRelationExtractor : BaseTreeSitterRelationExtractor
{
    public JavaScriptRelationExtractor(ISymbolCatalog catalog) : base(catalog) { }

    public override string Language => SupportedLanguages.JavaScript;

    protected override string PrimaryTreeSitterName => "JavaScript";

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx"
    };
    protected override IReadOnlySet<string> AllowedExtensions => Exts;

    protected override string? GetTreeSitterLanguageForFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".ts" => "TypeScript",
            ".tsx" => "TSX",
            _ => "JavaScript"
        };
    }

    public override bool IsRelevantPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (!Exts.Contains(ext)) return false;
        if (TreeSitterRelationHelpers.IsExcludedPath(path)) return false;
        return true;
    }
}
