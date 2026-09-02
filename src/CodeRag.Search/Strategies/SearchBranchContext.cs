using CodeRag.Domain.Enums;
using CodeRag.Search.Models;

namespace CodeRag.Search.Strategies;

public sealed class SearchBranchContext
{
    public SearchRequest Request { get; }
    public string Query { get; }
    public Guid[]? RepoFilter { get; }
    public string[]? LangFilter { get; }
    public string? SymbolKindFilter { get; }
    public int Limit { get; }
    public int Offset { get; }
    public SearchMode Mode { get; }
    public List<string> Warnings { get; }
    public List<Guid> ExactSymbolIds { get; set; } = new();

    public SearchBranchContext(SearchRequest request, string query, Guid[]? repoFilter, string[]? langFilter, string? symbolKindFilter, List<string> warnings)
    {
        Request = request;
        Query = query;
        RepoFilter = repoFilter;
        LangFilter = langFilter;
        SymbolKindFilter = symbolKindFilter;
        Limit = request.Limit <= 0 ? 20 : request.Limit;
        Warnings = warnings;
        Mode = request.Mode;
        Offset = request.Offset;
    }
}

public sealed class CandidateAccumulator
{
    public Guid ChunkId { get; }
    public HashSet<SearchOrigin> Origins { get; } = new();
    public double? FtsRank { get; set; }
    public double? VectorScore { get; set; }
    public double? VectorDistance { get; set; }
    public EmbeddingDomain VectorDomain { get; set; }
    public bool HasExact { get; set; }
    public double ExactBonus { get; set; }
    public bool HasStructural { get; set; }
    public CandidateAccumulator(Guid chunkId) => ChunkId = chunkId;
}

public sealed record BranchResult(int Count, List<Guid> ChunkIds);
