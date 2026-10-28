using System.ComponentModel;
using System.Text;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public sealed class KnowledgeTools
{
    private readonly KnowledgeService _knowledge;
    private readonly IMcpUsageRecorder? _usage;
    private readonly ILogger<KnowledgeTools> _logger;

    public KnowledgeTools(KnowledgeService knowledge, ILogger<KnowledgeTools> logger, IMcpUsageRecorder? usage = null)
    {
        _knowledge = knowledge;
        _logger = logger;
        _usage = usage;
    }

    private async Task<string> TrackAsync(string tool, Guid? repositoryId, Func<Task<string>> execute)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result;
        try { result = await execute().ConfigureAwait(false); }
        catch (Exception ex) { result = $"Error: {ex.Message}"; }
        sw.Stop();
        _usage?.Record(tool, !result.StartsWith("Error:", StringComparison.Ordinal), (int)sw.ElapsedMilliseconds, repositoryId);
        return result;
    }

    [McpServerTool, Description("Save durable project knowledge (fact, decision, constraint, research result, hypothesis, issue, observation). Use externalKey for idempotent saves: a repeated call returns the existing entry. projectId scopes knowledge to a project; omit it for global memory. repositoryId optionally narrows the entry to one repository. Embeddings are generated when available; failure does not lose the entry.")]
    public Task<string> SaveKnowledgeAsync(
        [Description("Short title")] string title,
        [Description("Knowledge body (markdown)")] string content,
        [Description("fact|decision|constraint|research|hypothesis|issue|observation")] string type = "fact",
        [Description("Project Id (Guid); omit for global memory")] Guid? projectId = null,
        [Description("Repository Id (Guid)")] Guid? repositoryId = null,
        [Description("Source link/commit/file reference")] string? sourceRef = null,
        [Description("Idempotency key")] string? externalKey = null,
        [Description("Optional task Id to link this knowledge to")] Guid? taskId = null,
        [Description("Author label")] string? createdBy = null,
        [Description("Save to global memory (cross-project). Explicit opt-in only.")] bool @global = false,
        CancellationToken ct = default)
        => TrackAsync("save_knowledge", repositoryId, async () =>
        {
            if (projectId is null && repositoryId is null && !@global)
                return "Error: scope required. Pass projectId (call list_projects or create_project first) or repositoryId. Use global=true only for genuinely cross-project knowledge.";
            if (@global && (projectId is not null || repositoryId is not null))
                return "Error: pass either projectId/repositoryId or global=true, not both.";
            var links = taskId is null
                ? null
                : new[] { new KnowledgeLinkRequest(EntityTypes.Task, taskId.Value, LinkRelations.Relates) };
            var (entry, created) = await _knowledge.SaveAsync(new SaveKnowledgeRequest(
                title, content, ParseType(type), projectId, repositoryId, sourceRef, externalKey,
                createdBy, Links: links), ct).ConfigureAwait(false);
            return $"Knowledge {entry.Id} ({(created ? "created" : "existing")}) v{entry.Version} " +
                   $"embedded={entry.EmbeddedAt is not null}\n{FormatEntry(entry)}";
        });

    [McpServerTool, Description("Get a knowledge entry with its links and change history.")]
    public Task<string> GetKnowledgeAsync(
        [Description("Knowledge Id (Guid)")] Guid knowledgeId, CancellationToken ct = default)
        => TrackAsync("get_knowledge", null, async () =>
        {
            var detail = await _knowledge.GetDetailAsync(knowledgeId, true, true, ct).ConfigureAwait(false);
            return FormatEntry(detail.Entry) +
                   $"\nEmbedded: {detail.Embedded}" +
                   $"\nLinks ({detail.Links.Count}):\n" +
                   string.Join("\n", detail.Links.Select(l => $"- {l.Relation} {l.FromType}:{l.FromId} -> {l.ToType}:{l.ToId}")) +
                   $"\nHistory ({detail.History.Count}):\n" +
                   string.Join("\n", detail.History.Select(h => $"- {h.At:u} {h.Actor ?? "-"} {h.Action}"));
        });

    [McpServerTool, Description("Search the knowledge base. mode: hybrid (default), semantic, lexical. Filters: projectId, repositoryId, types, statuses, globalOnly. Semantic mode degrades gracefully when embeddings are unavailable; hybrid then uses lexical results.")]
    public Task<string> SearchKnowledgeAsync(
        [Description("Search query")] string query,
        [Description("hybrid|semantic|lexical")] string mode = "hybrid",
        [Description("Project Id (Guid)")] Guid? projectId = null,
        [Description("Repository Id (Guid)")] Guid? repositoryId = null,
        [Description("Comma-separated types: fact,decision,constraint,research,hypothesis,issue,observation")] string? types = null,
        [Description("Comma-separated statuses: active,stale,superseded,archived")] string? statuses = null,
        [Description("Search only global memory")] bool globalOnly = false,
        [Description("Max results")] int limit = 20,
        [Description("Offset")] int offset = 0,
        CancellationToken ct = default)
        => TrackAsync("search_knowledge", repositoryId, async () =>
        {
            var result = await _knowledge.SearchAsync(new KnowledgeSearchRequest(
                query, mode, projectId, repositoryId, ParseTypes(types), ParseStatuses(statuses),
                globalOnly, false, limit, offset), ct).ConfigureAwait(false);
            var sb = new StringBuilder($"Mode: {result.Mode}; hits={result.Hits.Count}");
            if (result.Warnings.Count > 0) sb.AppendLine("Warnings: " + string.Join("; ", result.Warnings));
            sb.AppendLine();
            foreach (var hit in result.Hits)
                sb.AppendLine($"{hit.Score:0.000} [{hit.MatchedBy}] {FormatEntry(hit.Entry)}");
            return sb.ToString();
        });

    [McpServerTool, Description("Update a knowledge entry using optimistic concurrency (expectedVersion required). Changing title/content resets its embedding, which is regenerated when possible.")]
    public Task<string> UpdateKnowledgeAsync(
        [Description("Knowledge Id (Guid)")] Guid knowledgeId,
        [Description("Current version")] int expectedVersion,
        [Description("New title")] string? title = null,
        [Description("New content")] string? content = null,
        [Description("fact|decision|constraint|research|hypothesis|issue|observation")] string? type = null,
        [Description("active|stale|superseded|archived")] string? status = null,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("update_knowledge", null, async () =>
        {
            var entry = await _knowledge.UpdateAsync(knowledgeId, new UpdateKnowledgeRequest(
                expectedVersion, title, content, ParseTypeOrNull(type), ParseStatusOrNull(status),
                Actor: actor), ct).ConfigureAwait(false);
            return FormatEntry(entry);
        });

    [McpServerTool, Description("Mark a knowledge entry as stale (requires expectedVersion). The entry remains searchable and is not deleted.")]
    public Task<string> MarkKnowledgeStaleAsync(
        [Description("Knowledge Id (Guid)")] Guid knowledgeId,
        [Description("Current version")] int expectedVersion,
        [Description("Why the entry is stale")] string? reason = null,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("mark_knowledge_stale", null, async () =>
        {
            var entry = await _knowledge.MarkStaleAsync(knowledgeId, expectedVersion, reason, actor, ct)
                .ConfigureAwait(false);
            return FormatEntry(entry);
        });

    [McpServerTool, Description("Link a knowledge entry to a task, another knowledge entry, a symbol, file, repository or project. relation: relates|derived_from|supersedes|contradicts|implements|source|mentions.")]
    public Task<string> LinkKnowledgeAsync(
        [Description("Knowledge Id (Guid)")] Guid knowledgeId,
        [Description("task|knowledge|symbol|file|repository|project")] string targetType,
        [Description("Target Id (Guid)")] Guid targetId,
        [Description("Relation")] string relation = "relates",
        [Description("Optional note")] string? note = null,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("link_knowledge", null, async () =>
        {
            var result = await _knowledge.LinkAsync(knowledgeId,
                new KnowledgeLinkRequest(targetType, targetId, relation, note), actor, ct).ConfigureAwait(false);
            return $"Linked knowledge {knowledgeId} -> {result.Link.ToType}:{result.Link.ToId} ({result.Link.Relation})";
        });

    [McpServerTool, Description("Remove a link from a knowledge entry.")]
    public Task<string> UnlinkKnowledgeAsync(
        [Description("Knowledge Id (Guid)")] Guid knowledgeId,
        [Description("task|knowledge|symbol|file|repository|project")] string targetType,
        [Description("Target Id (Guid)")] Guid targetId,
        [Description("Relation to remove")] string relation = "relates",
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("unlink_knowledge", null, async () =>
        {
            var removed = await _knowledge.UnlinkAsync(knowledgeId, targetType, targetId, relation, actor, ct)
                .ConfigureAwait(false);
            return removed ? "Link removed" : "Link not found";
        });

    [McpServerTool, Description("Get knowledge entries related through links, up to depth 1..3.")]
    public Task<string> GetRelatedKnowledgeAsync(
        [Description("Knowledge Id (Guid)")] Guid knowledgeId,
        [Description("Traversal depth (1..3)")] int depth = 1,
        [Description("Optional relation filter")] string? relation = null,
        CancellationToken ct = default)
        => TrackAsync("get_related_knowledge", null, async () =>
        {
            var entries = await _knowledge.GetRelatedAsync(knowledgeId, depth, relation, ct).ConfigureAwait(false);
            if (entries.Count == 0) return "No related knowledge found";
            return string.Join("\n", entries.Select(FormatEntry));
        });

    [McpServerTool, Description("Show the change history of a knowledge entry.")]
    public Task<string> GetKnowledgeHistoryAsync(
        [Description("Knowledge Id (Guid)")] Guid knowledgeId,
        [Description("Max entries")] int limit = 100,
        CancellationToken ct = default)
        => TrackAsync("get_knowledge_history", null, async () =>
        {
            var history = await _knowledge.GetHistoryAsync(knowledgeId, limit, ct).ConfigureAwait(false);
            return string.Join("\n", history.Select(h =>
                $"- {h.At:u} {h.Actor ?? "-"} {h.Action} v{h.VersionFrom}->{h.VersionTo} {Truncate(h.Changes, 300)}"));
        });

    [McpServerTool, Description("Regenerate embeddings for knowledge entries whose embedding is missing or built with a different model/config. Safe to repeat; no LLM is required.")]
    public Task<string> ReembedKnowledgeAsync(
        [Description("Max entries to process")] int limit = 100, CancellationToken ct = default)
        => TrackAsync("reembed_knowledge", null, async () =>
        {
            var embedded = await _knowledge.ReembedAsync(limit, ct).ConfigureAwait(false);
            return $"Embedded {embedded} knowledge entries";
        });

    private static string FormatEntry(KnowledgeEntry entry)
        => $"Knowledge {entry.Id} v{entry.Version} [{entry.Status.ToString().ToLowerInvariant()}] " +
           $"{entry.Type.ToString().ToLowerInvariant()} — {entry.Title}" +
           (entry.ProjectId is null ? " (global)" : $" project={entry.ProjectId}") +
           (entry.RepositoryId is null ? "" : $" repo={entry.RepositoryId}") +
           (string.IsNullOrWhiteSpace(entry.SourceRef) ? "" : $" source={entry.SourceRef}") +
           $"\n  {Truncate(entry.Content, 800)}";

    private static KnowledgeType ParseType(string? value)
        => Enum.TryParse<KnowledgeType>(value, true, out var parsed) ? parsed : KnowledgeType.Fact;

    private static KnowledgeType? ParseTypeOrNull(string? value)
        => Enum.TryParse<KnowledgeType>(value, true, out var parsed) ? parsed : null;

    private static KnowledgeStatus? ParseStatusOrNull(string? value)
        => Enum.TryParse<KnowledgeStatus>(value, true, out var parsed) ? parsed : null;

    private static IReadOnlyList<KnowledgeType>? ParseTypes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var result = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Enum.TryParse<KnowledgeType>(v, true, out var t) ? t : (KnowledgeType?)null)
            .Where(t => t is not null).Select(t => t!.Value).ToList();
        return result.Count == 0 ? null : result;
    }

    private static IReadOnlyList<KnowledgeStatus>? ParseStatuses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var result = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Enum.TryParse<KnowledgeStatus>(v, true, out var t) ? t : (KnowledgeStatus?)null)
            .Where(t => t is not null).Select(t => t!.Value).ToList();
        return result.Count == 0 ? null : result;
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";
}
