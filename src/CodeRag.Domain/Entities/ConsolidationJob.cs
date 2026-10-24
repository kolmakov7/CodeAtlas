using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public sealed class ConsolidationJob
{
    public Guid Id { get; private set; }
    public ConsolidationJobType Type { get; private set; }
    public ConsolidationJobStatus Status { get; private set; }
    public string Reason { get; private set; } = "";
    public Guid? ProjectId { get; private set; }
    public Guid? RepositoryId { get; private set; }
    public string DedupeKey { get; private set; } = "";
    public string InputVersion { get; private set; } = "";
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public string? Analysis { get; private set; }
    public string? Proposal { get; private set; }
    public int ProposalVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolvedBy { get; private set; }
    public Guid? ResultKnowledgeId { get; private set; }
    public int Version { get; private set; } = 1;

    private ConsolidationJob(Guid id)
    {
        Id = id;
    }

    public static ConsolidationJob Create(
        Guid id,
        ConsolidationJobType type,
        string reason,
        string dedupeKey,
        string inputVersion,
        Guid? projectId = null,
        Guid? repositoryId = null,
        ConsolidationJobStatus status = ConsolidationJobStatus.ReadyForAnalysis,
        DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Consolidation job Id must not be empty.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainValidationException("Consolidation reason must not be empty.");
        if (string.IsNullOrWhiteSpace(dedupeKey)) throw new DomainValidationException("DedupeKey must not be empty.");
        if (string.IsNullOrWhiteSpace(inputVersion)) throw new DomainValidationException("InputVersion must not be empty.");

        var now = createdAt ?? DateTimeOffset.UtcNow;
        return new ConsolidationJob(id)
        {
            Type = type,
            Status = status,
            Reason = reason.Trim(),
            ProjectId = projectId,
            RepositoryId = repositoryId,
            DedupeKey = dedupeKey.Trim(),
            InputVersion = inputVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void MarkAnalysisRequested()
    {
        if (Status is ConsolidationJobStatus.Accepted or ConsolidationJobStatus.Rejected or ConsolidationJobStatus.Cancelled)
            throw new DomainValidationException($"Cannot analyse a job in status {Status}.");
        Status = ConsolidationJobStatus.AwaitingLlm;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordAttempt(string? error, DateTimeOffset? at = null)
    {
        Attempts++;
        if (!string.IsNullOrWhiteSpace(error)) LastError = error;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void MarkAwaitingLlm(string reason)
    {
        Status = ConsolidationJobStatus.AwaitingLlm;
        LastError = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkReadyForReview(string analysis, string proposal, int proposalVersion, string inputVersion)
    {
        Analysis = analysis;
        Proposal = proposal;
        ProposalVersion = proposalVersion;
        InputVersion = inputVersion;
        Status = ConsolidationJobStatus.ReadyForReview;
        LastError = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkError(string error)
    {
        Status = ConsolidationJobStatus.Error;
        LastError = error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Accept(string? actor = null, DateTimeOffset? at = null, Guid? resultKnowledgeId = null)
    {
        Status = ConsolidationJobStatus.Accepted;
        ResolvedAt = at ?? DateTimeOffset.UtcNow;
        ResolvedBy = actor;
        ResultKnowledgeId = resultKnowledgeId;
        UpdatedAt = ResolvedAt.Value;
    }

    public void Postpone(string? actor = null, DateTimeOffset? at = null)
    {
        Status = ConsolidationJobStatus.Postponed;
        ResolvedBy = actor;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void MarkUndone(string? actor = null, DateTimeOffset? at = null)
    {
        Status = ConsolidationJobStatus.Undone;
        ResolvedBy = actor;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void Reject(string? actor = null, DateTimeOffset? at = null)
    {
        Status = ConsolidationJobStatus.Rejected;
        ResolvedAt = at ?? DateTimeOffset.UtcNow;
        ResolvedBy = actor;
        UpdatedAt = ResolvedAt.Value;
    }

    public void Cancel(string? actor = null, DateTimeOffset? at = null)
    {
        Status = ConsolidationJobStatus.Cancelled;
        ResolvedAt = at ?? DateTimeOffset.UtcNow;
        ResolvedBy = actor;
        UpdatedAt = ResolvedAt.Value;
    }

    public void ReopenForAnalysis()
    {
        Status = ConsolidationJobStatus.ReadyForAnalysis;
        Proposal = null;
        ProposalVersion = 0;
        Analysis = null;
        LastError = null;
        ResolvedAt = null;
        ResolvedBy = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void BumpVersion(DateTimeOffset? at = null)
    {
        Version++;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public static ConsolidationJob Rehydrate(
        Guid id, ConsolidationJobType type, ConsolidationJobStatus status, string reason,
        Guid? projectId, Guid? repositoryId, string dedupeKey, string inputVersion,
        int attempts, string? lastError, string? analysis, string? proposal, int proposalVersion,
        DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset? resolvedAt, string? resolvedBy,
        int version, Guid? resultKnowledgeId = null)
        => new(id)
        {
            Type = type,
            Status = status,
            Reason = reason,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            DedupeKey = dedupeKey,
            InputVersion = inputVersion,
            Attempts = attempts,
            LastError = lastError,
            Analysis = analysis,
            Proposal = proposal,
            ProposalVersion = proposalVersion,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ResolvedAt = resolvedAt,
            ResolvedBy = resolvedBy,
            ResultKnowledgeId = resultKnowledgeId,
            Version = version
        };
}
