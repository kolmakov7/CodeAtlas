namespace CodeRag.Search;

public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>w_lex default 0.4</summary>
    public double LexicalWeight { get; set; } = 0.4;

    /// <summary>w_sem default 0.4</summary>
    public double SemanticWeight { get; set; } = 0.4;

    /// <summary>w_struct default 0.2</summary>
    public double StructuralWeight { get; set; } = 0.2;

    public int DefaultResultCount { get; set; } = 20;

    public int MaxResultCount { get; set; } = 100;

    public int PreviewMaxLines { get; set; } = 20;

    /// <summary>k for vector search (spec k=50)</summary>
    public int VectorTopK { get; set; } = 50;

    /// <summary>
    /// Minimum cosine similarity for a vector match to count as relevant. Vector candidates below this
    /// are dropped so a weak/ambiguous query returns no results instead of "least-irrelevant" random
    /// code. Calibrated to this codebase: genuinely relevant matches score ~0.31+, random noise ~0.21-;
    /// 0.25 sits in the gap. Set to 0 to disable filtering.
    /// </summary>
    public double MinSemanticScore { get; set; } = 0.25;

    /// <summary>exactBonus +1.0</summary>
    public double ExactBonus { get; set; } = 1.0;
}
