using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public sealed class ConsolidationJobItem
{
    public Guid JobId { get; private set; }
    public Guid KnowledgeId { get; private set; }
    public string Role { get; private set; } = "source";
    public int VersionAtCreation { get; private set; }
    public string ContentHashAtCreation { get; private set; } = "";
    public string PreviousStatus { get; private set; } = "active";

    private ConsolidationJobItem()
    {
    }

    public static ConsolidationJobItem Create(
        Guid jobId, Guid knowledgeId, string role, int versionAtCreation, string contentHashAtCreation,
        string previousStatus = "active")
    {
        if (jobId == Guid.Empty) throw new DomainValidationException("JobId must not be empty.");
        if (knowledgeId == Guid.Empty) throw new DomainValidationException("KnowledgeId must not be empty.");
        if (string.IsNullOrWhiteSpace(role)) throw new DomainValidationException("Role must not be empty.");
        return new ConsolidationJobItem
        {
            JobId = jobId,
            KnowledgeId = knowledgeId,
            Role = role.Trim().ToLowerInvariant(),
            VersionAtCreation = versionAtCreation,
            ContentHashAtCreation = contentHashAtCreation,
            PreviousStatus = string.IsNullOrWhiteSpace(previousStatus) ? "active" : previousStatus.Trim().ToLowerInvariant()
        };
    }

    public static ConsolidationJobItem Rehydrate(
        Guid jobId, Guid knowledgeId, string role, int versionAtCreation, string contentHashAtCreation,
        string previousStatus = "active")
        => new()
        {
            JobId = jobId,
            KnowledgeId = knowledgeId,
            Role = role,
            VersionAtCreation = versionAtCreation,
            ContentHashAtCreation = contentHashAtCreation,
            PreviousStatus = previousStatus
        };
}
