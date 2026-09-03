using CodeRag.Analyzers.Abstractions;

namespace CodeRag.Analyzers.TreeSitter.Relations;

public sealed class CppRelationExtractor : BaseTreeSitterRelationExtractor
{
    public CppRelationExtractor(ISymbolCatalog catalog) : base(catalog) { }

    public override string Language => SupportedLanguages.Cpp;

    protected override string PrimaryTreeSitterName => "C++";

    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh", ".hxx"
    };
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
