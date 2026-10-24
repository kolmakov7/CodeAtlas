namespace CodeRag.Knowledge;

public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    public RevisionOptions Revision { get; set; } = new();
    public KnowledgeEmbeddingOptions Embeddings { get; set; } = new();
    public ConsolidationOptions Consolidation { get; set; } = new();
}

public sealed class RevisionOptions
{
    public bool Enabled { get; set; }
    public int IntervalDays { get; set; } = 7;
    public int CheckMinutes { get; set; } = 15;
    public int MaxEntriesPerRun { get; set; } = 500;
    public double SimilarityThreshold { get; set; } = 0.8;
    public int GroupMaxSize { get; set; } = 10;
    public int StaleDays { get; set; } = 180;
    public int MaxJobsPerRun { get; set; } = 200;
}

public sealed class KnowledgeEmbeddingOptions
{
    public int MaxChars { get; set; } = 6000;
    public int BatchSize { get; set; } = 32;
}

public sealed class ConsolidationOptions
{
    public int MaxPromptChars { get; set; } = 60000;
    public int MaxItems { get; set; } = 20;
}
