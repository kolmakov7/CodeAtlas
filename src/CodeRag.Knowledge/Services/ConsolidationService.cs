using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Knowledge.Models;
using CodeRag.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Knowledge.Services;

public sealed class ConsolidationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConsolidationStore _jobs;
    private readonly IKnowledgeStore _knowledge;
    private readonly KnowledgeEmbeddingService _embedding;
    private readonly IChatCompletionClient _llm;
    private readonly IOptionsMonitor<KnowledgeOptions> _options;
    private readonly IOptionsMonitor<LlmOptions> _llmOptions;
    private readonly ILogger<ConsolidationService> _logger;

    public ConsolidationService(
        IConsolidationStore jobs,
        IKnowledgeStore knowledge,
        KnowledgeEmbeddingService embedding,
        IChatCompletionClient llm,
        IOptionsMonitor<KnowledgeOptions> options,
        IOptionsMonitor<LlmOptions> llmOptions,
        ILogger<ConsolidationService> logger)
    {
        _jobs = jobs;
        _knowledge = knowledge;
        _embedding = embedding;
        _llm = llm;
        _options = options;
        _llmOptions = llmOptions;
        _logger = logger;
    }

    public bool LlmConfigured => _llm.IsConfigured;

    public async Task<ConsolidationJobDetail> GetDetailAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        var items = await _jobs.GetItemsAsync(jobId, ct).ConfigureAwait(false);
        var entries = await _knowledge.GetByIdsAsync(items.Select(i => i.KnowledgeId).ToList(), ct).ConfigureAwait(false);
        var byId = entries.ToDictionary(e => e.Id);
        var entities = items
            .Where(i => byId.ContainsKey(i.KnowledgeId))
            .Select(i =>
            {
                var e = byId[i.KnowledgeId];
                return new ConsolidationEntity(e.Id, i.VersionAtCreation, e.Title, e.Content, e.Type, e.Status, e.ContentHash);
            })
            .ToList();
        var history = Array.Empty<EntityHistoryEntry>();
        return new ConsolidationJobDetail(job, entities, history);
    }

    public async Task<ConsolidationAnalysisResult> AnalyzeAsync(
        Guid jobId, string? actor = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job.Status is ConsolidationJobStatus.Accepted or ConsolidationJobStatus.Rejected or ConsolidationJobStatus.Cancelled)
            return new ConsolidationAnalysisResult(jobId, job.Status.ToString(), _llm.IsConfigured, 
                $"Cannot analyse a job in status {job.Status}.", job.Proposal);

        var items = await _jobs.GetItemsAsync(jobId, ct).ConfigureAwait(false);
        var entries = await LoadItemsAsync(items, ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            job.MarkError("Source knowledge entries no longer exist.");
            job.BumpVersion();
            await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
            return new ConsolidationAnalysisResult(jobId, job.Status.ToString(), _llm.IsConfigured,
                "Source knowledge entries no longer exist.", job.Proposal);
        }

        if (!_llm.IsConfigured)
        {
            job.MarkAwaitingLlm("LLM endpoint is not configured. Configure Llm:Endpoint and run analysis explicitly.");
            job.BumpVersion();
            await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
            return new ConsolidationAnalysisResult(jobId, job.Status.ToString(), false, job.LastError, job.Proposal);
        }

        var inputVersion = InputVersion(items);
        try
        {
            var prompt = BuildPrompt(job, entries);
            var result = await _llm.CompleteAsync(new ChatCompletionRequest(
                "You consolidate durable project knowledge. Preserve provenance and disagreements. " +
                "Return only strict JSON with fields: title, content, type, rationale, confidence, contradictions. " +
                "Allowed type values: fact, decision, constraint, research, hypothesis, issue, observation. " +
                "If entries genuinely conflict, keep both positions in content and list them in contradictions. " +
                "Do not invent facts that are absent from the sources.",
                prompt), ct).ConfigureAwait(false);

            var proposal = ParseProposal(result.Content);
            if (string.IsNullOrWhiteSpace(proposal.Title) || string.IsNullOrWhiteSpace(proposal.Content))
                throw new InvalidOperationException("LLM returned an empty title or content.");

            var analysis = JsonSerializer.Serialize(new
            {
                rationale = proposal.Rationale,
                confidence = proposal.Confidence,
                contradictions = proposal.Contradictions,
                model = result.Model,
                promptTokens = result.PromptTokens,
                completionTokens = result.CompletionTokens
            }, JsonOptions);

            var proposalJson = JsonSerializer.Serialize(new
            {
                title = proposal.Title.Trim(),
                content = proposal.Content.Trim(),
                type = NormalizeType(proposal.Type),
                rationale = proposal.Rationale,
                confidence = proposal.Confidence,
                contradictions = proposal.Contradictions,
                sourceIds = entries.Select(e => e.Id).ToArray(),
                model = result.Model,
                generatedAt = DateTimeOffset.UtcNow
            }, JsonOptions);

            job.MarkReadyForReview(analysis, proposalJson, job.ProposalVersion + 1, inputVersion);
            job.BumpVersion();
            var updated = await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
            if (!updated) throw new InvalidOperationException("Consolidation job changed concurrently.");
            return new ConsolidationAnalysisResult(jobId, job.Status.ToString(), true, null, proposalJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Consolidation analysis failed for job {JobId}", jobId);
            job.RecordAttempt(ex.Message);
            job.MarkError(ex.Message);
            job.BumpVersion();
            await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
            return new ConsolidationAnalysisResult(jobId, job.Status.ToString(), _llm.IsConfigured, ex.Message, job.Proposal);
        }
    }

    public async Task<ConsolidationApplyOutcome> AcceptAsync(
        Guid jobId, int expectedProposalVersion, string? actor = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job.Status != ConsolidationJobStatus.ReadyForReview)
            return Fail(job, $"Job must be ready_for_review (current: {job.Status}).");
        if (job.ProposalVersion != expectedProposalVersion)
            return Fail(job, $"Proposal version mismatch (expected {expectedProposalVersion}, current {job.ProposalVersion}).");
        if (string.IsNullOrWhiteSpace(job.Proposal))
            return Fail(job, "Job has no proposal.");

        var proposal = ParseProposal(job.Proposal);
        var items = await _jobs.GetItemsAsync(jobId, ct).ConfigureAwait(false);
        var entries = await LoadItemsAsync(items, ct).ConfigureAwait(false);
        if (entries.Count != items.Count)
            return Fail(job, "One or more source entries no longer exist.");

        foreach (var item in items)
        {
            var entry = entries.First(e => e.Id == item.KnowledgeId);
            if (entry.Version != item.VersionAtCreation || entry.ContentHash != item.ContentHashAtCreation)
                return Fail(job, $"Source knowledge {item.KnowledgeId} changed since the proposal was generated.");
        }

        var mergedId = MergedId(job.Id);
        var merged = KnowledgeEntry.Create(
            mergedId, proposal.Title, proposal.Content, ParseType(proposal.Type),
            job.ProjectId, job.RepositoryId, KnowledgeStatus.Active, "consolidation",
            $"consolidation:{job.Id}", $"consolidation:{job.Id}", actor,
            JsonSerializer.Serialize(new { jobId = job.Id, sourceIds = entries.Select(e => e.Id) }, JsonOptions));

        job.Accept(actor, resultKnowledgeId: mergedId);
        job.BumpVersion();

        var result = await _jobs.ApplyAsync(job, items, merged, job.Version - 1, ct).ConfigureAwait(false);
        if (!result.Success)
            return Fail(job, result.Conflict ?? "Apply failed.");

        await _embedding.TryEmbedAsync(mergedId, ct).ConfigureAwait(false);
        return new ConsolidationApplyOutcome(true, jobId, job.Status.ToString(), mergedId, null);
    }

    public async Task<ConsolidationApplyOutcome> RejectAsync(
        Guid jobId, string? reason = null, string? actor = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job.Status is ConsolidationJobStatus.Accepted or ConsolidationJobStatus.Undone)
            return Fail(job, "An accepted job cannot be rejected.");
        job.Reject(actor);
        job.BumpVersion();
        var updated = await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
        return updated
            ? new ConsolidationApplyOutcome(true, jobId, job.Status.ToString(), null, null)
            : Fail(job, "Consolidation job changed concurrently.");
    }

    public async Task<ConsolidationApplyOutcome> CancelAsync(
        Guid jobId, string? actor = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job.Status is ConsolidationJobStatus.Accepted or ConsolidationJobStatus.Undone)
            return Fail(job, "An accepted job cannot be cancelled.");
        job.Cancel(actor);
        job.BumpVersion();
        var updated = await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
        return updated
            ? new ConsolidationApplyOutcome(true, jobId, job.Status.ToString(), null, null)
            : Fail(job, "Consolidation job changed concurrently.");
    }

    public async Task<ConsolidationApplyOutcome> PostponeAsync(
        Guid jobId, string? actor = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        job.Postpone(actor);
        job.BumpVersion();
        var updated = await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
        return updated
            ? new ConsolidationApplyOutcome(true, jobId, job.Status.ToString(), null, null)
            : Fail(job, "Consolidation job changed concurrently.");
    }

    public async Task<ConsolidationApplyOutcome> RetryAsync(
        Guid jobId, string? actor = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job.Status is ConsolidationJobStatus.Accepted or ConsolidationJobStatus.Undone)
            return Fail(job, "An accepted job cannot be retried.");
        job.ReopenForAnalysis();
        job.BumpVersion();
        var updated = await _jobs.UpdateAsync(job, job.Version - 1, ct).ConfigureAwait(false);
        return updated
            ? new ConsolidationApplyOutcome(true, jobId, job.Status.ToString(), null, null)
            : Fail(job, "Consolidation job changed concurrently.");
    }

    public async Task<ConsolidationApplyOutcome> UndoAsync(
        Guid jobId, string? actor = null, CancellationToken ct = default)
    {
        var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
        if (job.Status != ConsolidationJobStatus.Accepted || job.ResultKnowledgeId is null)
            return Fail(job, "Only an accepted job can be undone.");
        var items = await _jobs.GetItemsAsync(jobId, ct).ConfigureAwait(false);
        var resultId = job.ResultKnowledgeId.Value;
        job.MarkUndone(actor);
        job.BumpVersion();
        var result = await _jobs.UndoAsync(job, items, resultId, job.Version - 1, ct).ConfigureAwait(false);
        if (!result.Success) return Fail(job, result.Conflict ?? "Undo failed.");
        await _embedding.DeleteAsync(resultId, ct).ConfigureAwait(false);
        return new ConsolidationApplyOutcome(true, jobId, job.Status.ToString(), resultId, null);
    }

    public Task<IReadOnlyList<ConsolidationJob>> ListAsync(ConsolidationJobQuery query, CancellationToken ct = default)
        => _jobs.ListAsync(query, ct);

    public Task<int> CountAsync(ConsolidationJobQuery query, CancellationToken ct = default)
        => _jobs.CountAsync(query, ct);

    private async Task<ConsolidationJob> GetJobAsync(Guid id, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(id, ct).ConfigureAwait(false);
        return job ?? throw new DomainValidationException($"Consolidation job {id} not found.");
    }

    private async Task<List<KnowledgeEntry>> LoadItemsAsync(
        IReadOnlyList<ConsolidationJobItem> items, CancellationToken ct)
        => (await _knowledge.GetByIdsAsync(items.Select(i => i.KnowledgeId).ToList(), ct).ConfigureAwait(false)).ToList();

    private static string InputVersion(IReadOnlyList<ConsolidationJobItem> items)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
            items.OrderBy(i => i.KnowledgeId)
                .Select(i => $"{i.KnowledgeId}:{i.VersionAtCreation}:{i.ContentHashAtCreation}"))))).ToLowerInvariant();

    private static Guid MergedId(Guid jobId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("codeatlas:consolidated:" + jobId));
        var guid = new byte[16];
        Array.Copy(bytes, guid, 16);
        return new Guid(guid);
    }

    private string BuildPrompt(ConsolidationJob job, IReadOnlyList<KnowledgeEntry> entries)
    {
        var max = Math.Clamp(_options.CurrentValue.Consolidation.MaxPromptChars, 1000, 500_000);
        var perItem = Math.Max(200, max / Math.Max(1, entries.Count) - 200);
        var sb = new StringBuilder();
        sb.AppendLine($"Consolidation reason: {job.Reason}");
        sb.AppendLine($"Job type: {job.Type}");
        sb.AppendLine();
        foreach (var entry in entries)
        {
            var content = entry.Content.Length <= perItem ? entry.Content : entry.Content[..perItem] + "\n[truncated]";
            sb.AppendLine($"--- ENTRY {entry.Id} ---");
            sb.AppendLine($"title: {entry.Title}");
            sb.AppendLine($"type: {entry.Type.ToString().ToLowerInvariant()}");
            sb.AppendLine($"version: {entry.Version}");
            sb.AppendLine($"source: {entry.SourceRef ?? entry.Source}");
            sb.AppendLine(content);
            sb.AppendLine();
            if (sb.Length > max)
            {
                sb.AppendLine("[prompt truncated]");
                break;
            }
        }
        return sb.ToString();
    }

    private static LlmProposal ParseProposal(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("LLM response did not contain a JSON object.");
        var json = raw[start..(end + 1)];
        var proposal = JsonSerializer.Deserialize<LlmProposal>(json, JsonOptions);
        return proposal ?? throw new InvalidOperationException("LLM response JSON was empty.");
    }

    private static string NormalizeType(string? raw)
    {
        var type = ParseType(raw);
        return type.ToString().ToLowerInvariant();
    }

    private static KnowledgeType ParseType(string? raw)
        => Enum.TryParse<KnowledgeType>(raw, true, out var type) ? type : KnowledgeType.Fact;

    private static ConsolidationApplyOutcome Fail(ConsolidationJob job, string error)
        => new(false, job.Id, job.Status.ToString(), job.ResultKnowledgeId, error);

    private sealed class LlmProposal
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("rationale")] public string? Rationale { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
        [JsonPropertyName("contradictions")] public List<string>? Contradictions { get; set; }
    }
}
