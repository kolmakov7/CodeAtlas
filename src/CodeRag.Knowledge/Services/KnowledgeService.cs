using System.Text.Json;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Knowledge.Models;

namespace CodeRag.Knowledge.Services;

public sealed class KnowledgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IKnowledgeStore _knowledge;
    private readonly ITaskStore _tasks;
    private readonly IProjectStore _projects;
    private readonly IRepositoryStore _repositories;
    private readonly IEntityLinkStore _links;
    private readonly IEntityHistoryStore _history;
    private readonly ProjectService _projectService;
    private readonly KnowledgeEmbeddingService _embedding;
    private readonly KnowledgeSearchService _search;

    public KnowledgeService(
        IKnowledgeStore knowledge,
        ITaskStore tasks,
        IProjectStore projects,
        IRepositoryStore repositories,
        IEntityLinkStore links,
        IEntityHistoryStore history,
        ProjectService projectService,
        KnowledgeEmbeddingService embedding,
        KnowledgeSearchService search)
    {
        _knowledge = knowledge;
        _tasks = tasks;
        _projects = projects;
        _repositories = repositories;
        _links = links;
        _history = history;
        _projectService = projectService;
        _embedding = embedding;
        _search = search;
    }

    public async Task<(KnowledgeEntry Entry, bool Created)> SaveAsync(
        SaveKnowledgeRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new DomainValidationException("Knowledge title must not be empty.");
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new DomainValidationException("Knowledge content must not be empty.");

        var project = await _projectService.ResolveAsync(request.ProjectId, request.RepositoryId, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.ExternalKey))
        {
            var existing = await _knowledge.GetByExternalKeyAsync(project?.Id, request.ExternalKey.Trim(), ct).ConfigureAwait(false);
            if (existing is not null) return (existing, false);
        }

        if (request.RepositoryId is not null)
        {
            var repository = await _repositories.GetByIdAsync(request.RepositoryId.Value, ct).ConfigureAwait(false);
            if (repository is null) throw new DomainValidationException($"Repository {request.RepositoryId} not found.");
        }

        var entry = KnowledgeEntry.Create(
            Guid.NewGuid(), request.Title, request.Content, request.Type, project?.Id, request.RepositoryId,
            KnowledgeStatus.Active, request.Source, request.SourceRef, request.ExternalKey, request.CreatedBy,
            request.Metadata);
        await _knowledge.CreateAsync(entry, ct).ConfigureAwait(false);
        await AddHistoryAsync(entry, "created", request.CreatedBy, null, entry.Version,
            new { entry.Title, type = entry.Type.ToString() }, ct).ConfigureAwait(false);

        foreach (var linkRequest in request.Links ?? Array.Empty<KnowledgeLinkRequest>())
            await LinkInternalAsync(entry, linkRequest, request.CreatedBy, ct).ConfigureAwait(false);

        await _embedding.TryEmbedAsync(entry.Id, ct).ConfigureAwait(false);
        var refreshed = await GetAsync(entry.Id, ct).ConfigureAwait(false);
        return (refreshed, true);
    }

    public async Task<KnowledgeEntry> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await _knowledge.GetByIdAsync(id, ct).ConfigureAwait(false);
        return entry ?? throw new DomainValidationException($"Knowledge {id} not found.");
    }

    public async Task<KnowledgeDetail> GetDetailAsync(
        Guid id, bool includeLinks = true, bool includeHistory = true, CancellationToken ct = default)
    {
        var entry = await GetAsync(id, ct).ConfigureAwait(false);
        var links = includeLinks
            ? (await _links.GetOutgoingAsync(EntityTypes.Knowledge, id, ct).ConfigureAwait(false))
                .Concat(await _links.GetIncomingAsync(EntityTypes.Knowledge, id, ct).ConfigureAwait(false))
                .DistinctBy(l => l.Id)
                .ToList()
            : new List<EntityLink>();
        var history = includeHistory
            ? await _history.ListAsync(EntityTypes.Knowledge, id, 200, ct).ConfigureAwait(false)
            : Array.Empty<EntityHistoryEntry>();
        return new KnowledgeDetail(entry, links, history, entry.EmbeddedAt is not null);
    }

    public Task<IReadOnlyList<KnowledgeEntry>> ListAsync(KnowledgeQuery query, CancellationToken ct = default)
        => _knowledge.ListAsync(query, ct);

    public Task<KnowledgeSearchResult> SearchAsync(KnowledgeSearchRequest request, CancellationToken ct = default)
        => _search.SearchAsync(request, ct);

    public async Task<KnowledgeEntry> UpdateAsync(
        Guid id, UpdateKnowledgeRequest request, CancellationToken ct = default)
    {
        var entry = await GetAsync(id, ct).ConfigureAwait(false);
        var contentChanged = false;
        var changes = new Dictionary<string, object?>();

        if (request.Title is not null || request.Content is not null || request.Type is not null || request.Metadata is not null)
        {
            entry.UpdateContent(
                request.Title ?? entry.Title,
                request.Content ?? entry.Content,
                request.Type ?? entry.Type,
                request.Metadata ?? entry.Metadata);
            contentChanged = true;
            changes["content"] = new { entry.Title, type = entry.Type.ToString(), entry.ContentHash };
        }

        if (request.Status is not null && request.Status.Value != entry.Status)
        {
            var from = entry.Status;
            entry.SetStatus(request.Status.Value);
            changes["status"] = new { from = from.ToString(), to = entry.Status.ToString() };
        }

        if (changes.Count == 0) return entry;

        entry.BumpVersion();
        var updated = await _knowledge.UpdateAsync(entry, request.ExpectedVersion, ct).ConfigureAwait(false);
        if (!updated)
        {
            var current = await GetAsync(id, ct).ConfigureAwait(false);
            throw new VersionConflictException("Knowledge", id, request.ExpectedVersion, current.Version);
        }

        await AddHistoryAsync(entry, "updated", request.Actor, request.ExpectedVersion, entry.Version, changes, ct)
            .ConfigureAwait(false);
        if (contentChanged)
        {
            entry = await GetAsync(id, ct).ConfigureAwait(false);
            await _embedding.TryEmbedAsync(id, ct).ConfigureAwait(false);
            entry = await GetAsync(id, ct).ConfigureAwait(false);
        }
        return entry;
    }

    public async Task<KnowledgeEntry> MarkStaleAsync(
        Guid id, int expectedVersion, string? reason = null, string? actor = null, CancellationToken ct = default)
    {
        var entry = await GetAsync(id, ct).ConfigureAwait(false);
        entry.SetStatus(KnowledgeStatus.Stale);
        entry.BumpVersion();
        var updated = await _knowledge.UpdateAsync(entry, expectedVersion, ct).ConfigureAwait(false);
        if (!updated)
        {
            var current = await GetAsync(id, ct).ConfigureAwait(false);
            throw new VersionConflictException("Knowledge", id, expectedVersion, current.Version);
        }
        await AddHistoryAsync(entry, "marked_stale", actor, expectedVersion, entry.Version,
            new { reason }, ct).ConfigureAwait(false);
        return entry;
    }

    public async Task<KnowledgeLinkResult> LinkAsync(
        Guid knowledgeId, KnowledgeLinkRequest request, string? actor = null, CancellationToken ct = default)
    {
        var entry = await GetAsync(knowledgeId, ct).ConfigureAwait(false);
        var result = await LinkInternalAsync(entry, request, actor, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> UnlinkAsync(
        Guid knowledgeId, string targetType, Guid targetId, string relation, string? actor = null,
        CancellationToken ct = default)
    {
        var entry = await GetAsync(knowledgeId, ct).ConfigureAwait(false);
        var removed = await _links.RemoveAsync(EntityTypes.Knowledge, knowledgeId, targetType, targetId, relation, ct)
            .ConfigureAwait(false);
        if (removed)
            await AddHistoryAsync(entry, "link_removed", actor, entry.Version, entry.Version,
                new { targetType, targetId, relation }, ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<KnowledgeEntry>> GetRelatedAsync(
        Guid knowledgeId, int depth = 1, string? relation = null, CancellationToken ct = default)
    {
        _ = await GetAsync(knowledgeId, ct).ConfigureAwait(false);
        var maxDepth = Math.Clamp(depth, 1, 3);
        var visited = new HashSet<Guid> { knowledgeId };
        var current = new List<Guid> { knowledgeId };
        var result = new List<KnowledgeEntry>();

        for (var level = 0; level < maxDepth; level++)
        {
            var next = new List<Guid>();
            foreach (var id in current)
            {
                var outgoing = await _links.ListAsync(EntityTypes.Knowledge, id, EntityTypes.Knowledge, null,
                    relation, 200, ct).ConfigureAwait(false);
                var incoming = await _links.ListAsync(EntityTypes.Knowledge, null, EntityTypes.Knowledge, id,
                    relation, 200, ct).ConfigureAwait(false);
                foreach (var link in outgoing.Concat(incoming))
                {
                    var other = link.FromId == id ? link.ToId : link.FromId;
                    if (visited.Add(other)) next.Add(other);
                }
            }
            if (next.Count == 0) break;
            var entries = await _knowledge.GetByIdsAsync(next, ct).ConfigureAwait(false);
            result.AddRange(entries);
            current = next;
        }
        return result;
    }

    public async Task<IReadOnlyList<EntityHistoryEntry>> GetHistoryAsync(
        Guid knowledgeId, int limit = 100, CancellationToken ct = default)
        => await _history.ListAsync(EntityTypes.Knowledge, knowledgeId, limit, ct).ConfigureAwait(false);

    public Task<int> ReembedAsync(int limit = 100, CancellationToken ct = default)
        => _embedding.EmbedPendingAsync(limit, ct);

    private async Task<KnowledgeLinkResult> LinkInternalAsync(
        KnowledgeEntry entry, KnowledgeLinkRequest request, string? actor, CancellationToken ct)
    {
        if (!EntityTypes.IsValid(request.TargetType))
            throw new DomainValidationException($"Unknown targetType '{request.TargetType}'.");
        var targetType = request.TargetType.Trim().ToLowerInvariant();
        switch (targetType)
        {
            case EntityTypes.Knowledge:
                _ = await GetAsync(request.TargetId, ct).ConfigureAwait(false);
                break;
            case EntityTypes.Task:
                _ = await _tasks.GetByIdAsync(request.TargetId, ct).ConfigureAwait(false)
                    ?? throw new DomainValidationException($"Task {request.TargetId} not found.");
                break;
            case EntityTypes.Repository:
                _ = await _repositories.GetByIdAsync(request.TargetId, ct).ConfigureAwait(false)
                    ?? throw new DomainValidationException($"Repository {request.TargetId} not found.");
                break;
            case EntityTypes.Project:
                _ = await _projects.GetByIdAsync(request.TargetId, ct).ConfigureAwait(false)
                    ?? throw new DomainValidationException($"Project {request.TargetId} not found.");
                break;
        }

        var link = EntityLink.Create(Guid.NewGuid(), EntityTypes.Knowledge, entry.Id, targetType,
            request.TargetId, request.Relation, request.Note, actor);
        await _links.AddAsync(link, ct).ConfigureAwait(false);
        await AddHistoryAsync(entry, "link_added", actor, entry.Version, entry.Version,
            new { targetType, targetId = request.TargetId, relation = request.Relation }, ct).ConfigureAwait(false);
        return new KnowledgeLinkResult(link, true);
    }

    private Task AddHistoryAsync(
        KnowledgeEntry entry, string action, string? actor, int? from, int? to, object changes,
        CancellationToken ct)
        => _history.AddAsync(EntityHistoryEntry.Create(
            EntityTypes.Knowledge, entry.Id, action, actor, from, to,
            JsonSerializer.Serialize(changes, JsonOptions)), ct);
}
