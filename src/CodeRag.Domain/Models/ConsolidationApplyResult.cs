namespace CodeRag.Domain.Models;

public sealed record ConsolidationApplyResult(
    bool Success,
    string? Conflict = null,
    Guid? ResultKnowledgeId = null,
    int RestoredCount = 0);
