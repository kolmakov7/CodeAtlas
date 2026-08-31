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
    /// code. After switching to raw cosine (no per-domain normalization), weak noise scores 0.25-0.28
    /// are correctly filtered at 0.30 while relevant eShop code still scores 0.4+ (SemanticCode) / 0.6+ (SemanticText).
    /// </summary>
    public double MinSemanticScore { get; set; } = 0.30;

    /// <summary>
    /// Absolute cosine floor for the TEXT domain, applied on top of MinSemanticScore. jina-text assigns
    /// 0.33-0.46 even to wholly irrelevant prose (e.g. "best pizza recipes in nyc" → 0.36), so the
    /// generic 0.30 floor lets junk docs through; legitimate doc queries score 0.55-0.85.
    /// </summary>
    public double MinTextSemanticScore { get; set; } = 0.55;

    /// <summary>
    /// Multiplier applied to the (per-domain normalized) text-domain vector contribution in merged
    /// Hybrid/Semantic ranking. Code questions must not be out-ranked by the doc model, which scores
    /// prose queries higher against prose docs; 0.6 keeps docs competitive for genuinely doc-oriented
    /// queries (where they also win on FTS) while letting code win code questions.
    /// </summary>
    public double TextDomainWeight { get; set; } = 0.6;

    /// <summary>exactBonus +1.0</summary>
    public double ExactBonus { get; set; } = 1.0;
}
