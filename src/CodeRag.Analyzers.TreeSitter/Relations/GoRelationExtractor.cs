using CodeRag.Analyzers.Abstractions;

namespace CodeRag.Analyzers.TreeSitter.Relations;

public sealed class GoRelationExtractor : BaseTreeSitterRelationExtractor
{
    public GoRelationExtractor(ISymbolCatalog catalog) : base(catalog) { }

    public override string Language => SupportedLanguages.Go;

    protected override string PrimaryTreeSitterName => "Go";

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase) { ".go" };
    protected override IReadOnlySet<string> AllowedExtensions => Exts;

    public override bool IsRelevantPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (!Exts.Contains(ext)) return false;
        if (TreeSitterRelationHelpers.IsExcludedPath(path)) return false;
        return true;
    }
}
