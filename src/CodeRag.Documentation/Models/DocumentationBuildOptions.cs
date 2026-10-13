namespace CodeRag.Documentation.Models;

/// <summary>Per-request knobs for how much context to assemble for a documentation target.</summary>
public sealed record DocumentationBuildOptions(
    bool IncludeTests = true,
    int MaxMembers = 60,
    int MaxDependencies = 40,
    int MaxCallers = 40,
    int MaxRelatedSnippets = 6,
    int MaxSnippetLines = 40,
    int MaxSourceLines = 400)
{
    public static DocumentationBuildOptions Default { get; } = new();
}
