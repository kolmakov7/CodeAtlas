using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Knowledge.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Knowledge.Services;

public sealed partial class RevisionService
{
    private readonly IKnowledgeStore _knowledge;
    private readonly IConsolidationStore _jobs;
    private readonly IEntityLinkStore _links;
    private readonly IRevisionRunStore _runs;
    private readonly IOptionsMonitor<KnowledgeOptions> _options;
    private readonly ILogger<RevisionService> _logger;

    public RevisionService(
        IKnowledgeStore knowledge,
        IConsolidationStore jobs,
        IEntityLinkStore links,
        IRevisionRunStore runs,
        IOptionsMonitor<KnowledgeOptions> options,
        ILogger<RevisionService> logger)
    {
        _knowledge = knowledge;
        _jobs = jobs;
        _links = links;
        _runs = runs;
        _options = options;
        _logger = logger;
    }

    public async Task<RevisionRunResult> RunAsync(
        Guid? projectId = null,
        Guid? repositoryId = null,
        string trigger = "manual",
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var opts = _options.CurrentValue.Revision;
        var run = RevisionRun.Start(Guid.NewGuid(), trigger);
        if (!dryRun) await _runs.AddAsync(run, ct).ConfigureAwait(false);

        var warnings = new List<string>();
        var created = new List<Guid>();
        var skipped = 0;

        try
        {
            var entries = (await _knowledge.ListAsync(new KnowledgeQuery(
                ProjectId: projectId,
                RepositoryId: repositoryId,
                Statuses: new[] { KnowledgeStatus.Active, KnowledgeStatus.Stale },
                Limit: Math.Clamp(opts.MaxEntriesPerRun, 1, 5000)), ct).ConfigureAwait(false)).ToList();

            var candidates = await BuildCandidatesAsync(entries, opts, warnings, ct).ConfigureAwait(false);
            foreach (var candidate in candidates.Take(Math.Clamp(opts.MaxJobsPerRun, 1, 2000)))
            {
                if (candidate.Members.Count < 1) continue;
                var ids = candidate.Members.Select(m => m.Entry.Id).OrderBy(x => x).ToList();
                var inputVersion = Hash(string.Join("|", candidate.Members
                    .OrderBy(m => m.Entry.Id)
                    .Select(m => $"{m.Entry.Id}:{m.Entry.Version}:{m.Entry.ContentHash}")));
                var dedupeKey = Hash($"{candidate.Type}|{candidate.ScopeKey}|{candidate.ReasonKey}|{inputVersion}");
                var job = ConsolidationJob.Create(
                    Guid.NewGuid(), candidate.Type, candidate.Reason, dedupeKey, inputVersion,
                    candidate.ProjectId ?? projectId, candidate.RepositoryId ?? repositoryId,
                    ConsolidationJobStatus.ReadyForAnalysis);
                var items = candidate.Members.Select(m => ConsolidationJobItem.Create(
                    job.Id, m.Entry.Id, "source", m.Entry.Version, m.Entry.ContentHash,
                    m.Entry.Status.ToString().ToLowerInvariant())).ToList();

                if (dryRun)
                {
                    created.Add(job.Id);
                    continue;
                }

                var inserted = await _jobs.CreateIfAbsentAsync(job, items, ct).ConfigureAwait(false);
                if (inserted) created.Add(job.Id);
                else skipped++;
            }

            var result = new RevisionRunResult(run.Id, entries.Count, created.Count, skipped, created, warnings);
            if (!dryRun)
            {
                run.Complete(JsonSerializer.Serialize(new
                {
                    scanned = entries.Count,
                    jobsCreated = created.Count,
                    jobsSkipped = skipped,
                    warnings
                }));
                await _runs.UpdateAsync(run, ct).ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Knowledge revision failed");
            if (!dryRun)
            {
                run.Fail(ex.Message);
                await _runs.UpdateAsync(run, ct).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<bool> IsDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue.Revision;
        if (!opts.Enabled) return false;
        var latest = await _runs.GetLatestAsync(ct).ConfigureAwait(false);
        if (latest is null) return true;
        return latest.StartedAt.AddDays(Math.Clamp(opts.IntervalDays, 1, 365)) <= now;
    }

    public Task<RevisionRun?> GetLatestRunAsync(CancellationToken ct = default)
        => _runs.GetLatestAsync(ct);

    public Task<IReadOnlyList<RevisionRun>> ListRunsAsync(int limit = 20, CancellationToken ct = default)
        => _runs.ListAsync(limit, ct);

    private async Task<List<Candidate>> BuildCandidatesAsync(
        List<KnowledgeEntry> entries, RevisionOptions opts, List<string> warnings, CancellationToken ct)
    {
        var candidates = new List<Candidate>();
        if (entries.Count == 0) return candidates;

        var seen = new HashSet<string>();

        void Add(Candidate candidate)
        {
            var ids = string.Join(",", candidate.Members.Select(m => m.Entry.Id).OrderBy(x => x));
            if (!seen.Add(candidate.Type + "|" + ids)) return;
            candidates.Add(candidate);
        }

        foreach (var group in entries.GroupBy(e => e.ContentHash).Where(g => g.Count() > 1))
        {
            var members = group.OrderBy(e => e.CreatedAt).Select(e => new CandidateMember(e)).ToList();
            if (members.Count > opts.GroupMaxSize) members = members.Take(opts.GroupMaxSize).ToList();
            Add(new Candidate(ConsolidationJobType.Duplicates,
                $"Identical content hash across {members.Count} entries",
                members, members[0].Entry.ProjectId, members[0].Entry.RepositoryId,
                "content_hash"));
        }

        foreach (var group in entries.GroupBy(e => Normalize(e.Title)).Where(g => g.Count() > 1))
        {
            var members = group.OrderBy(e => e.CreatedAt).Select(e => new CandidateMember(e)).ToList();
            if (members.Count > opts.GroupMaxSize) members = members.Take(opts.GroupMaxSize).ToList();
            Add(new Candidate(ConsolidationJobType.Duplicates,
                $"Same normalized title across {members.Count} entries",
                members, members[0].Entry.ProjectId, members[0].Entry.RepositoryId,
                "same_title"));
        }

        var tokenSets = entries.ToDictionary(e => e.Id, e => Tokens(e.Title + " " + e.Content));
        var threshold = Math.Clamp(opts.SimilarityThreshold, 0.5, 1.0);
        var union = new UnionFind(entries.Select(e => e.Id));
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                var a = entries[i];
                var b = entries[j];
                if (a.Type != b.Type || a.ProjectId != b.ProjectId || a.RepositoryId != b.RepositoryId) continue;
                if (a.ContentHash == b.ContentHash) { union.Union(a.Id, b.Id); continue; }
                var similarity = Jaccard(tokenSets[a.Id], tokenSets[b.Id]);
                if (similarity >= threshold) union.Union(a.Id, b.Id);
            }
        }

        foreach (var cluster in entries.GroupBy(e => union.Find(e.Id)).Where(g => g.Count() > 1))
        {
            var members = cluster.OrderBy(e => e.CreatedAt).Take(opts.GroupMaxSize)
                .Select(e => new CandidateMember(e)).ToList();
            Add(new Candidate(ConsolidationJobType.Duplicates,
                $"Token similarity >= {threshold:0.00} across {members.Count} entries",
                members, members[0].Entry.ProjectId, members[0].Entry.RepositoryId,
                "token_similarity"));
        }

        var contradictionLinks = new List<EntityLink>();
        foreach (var entry in entries)
        {
            var outgoing = await _links.ListAsync(EntityTypes.Knowledge, entry.Id, EntityTypes.Knowledge, null,
                LinkRelations.Contradicts, 100, ct).ConfigureAwait(false);
            contradictionLinks.AddRange(outgoing.Where(l => l.Relation == LinkRelations.Contradicts));
        }
        foreach (var link in contradictionLinks.DistinctBy(l => l.Id))
        {
            var from = entries.FirstOrDefault(e => e.Id == link.FromId);
            var to = entries.FirstOrDefault(e => e.Id == link.ToId);
            if (from is null || to is null) continue;
            Add(new Candidate(ConsolidationJobType.Contradiction,
                "Explicit contradiction link between knowledge entries",
                new List<CandidateMember> { new(from), new(to) },
                from.ProjectId, from.RepositoryId, "contradiction_link"));
        }

        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.AddDays(-Math.Clamp(opts.StaleDays, 1, 3650));
        foreach (var entry in entries.Where(e =>
                     e.Status == KnowledgeStatus.Active &&
                     (e.UpdatedAt < staleBefore || (e.ValidUntil is not null && e.ValidUntil < now))))
        {
            Add(new Candidate(ConsolidationJobType.Stale,
                $"Not updated since {entry.UpdatedAt:yyyy-MM-dd}" + (entry.ValidUntil is not null ? $" (valid until {entry.ValidUntil:yyyy-MM-dd})" : ""),
                new List<CandidateMember> { new(entry) }, entry.ProjectId, entry.RepositoryId, "stale"));
        }

        foreach (var entry in entries.Where(e =>
                     string.IsNullOrWhiteSpace(e.SourceRef) &&
                     e.Source is not "import" and not "consolidation" &&
                     e.Type is KnowledgeType.Decision or KnowledgeType.Constraint or KnowledgeType.Fact))
        {
            Add(new Candidate(ConsolidationJobType.MissingSource,
                "No source reference recorded for a durable knowledge type",
                new List<CandidateMember> { new(entry) }, entry.ProjectId, entry.RepositoryId, "missing_source"));
        }

        if (candidates.Count >= opts.MaxJobsPerRun)
            warnings.Add($"Candidate list truncated at {opts.MaxJobsPerRun} jobs.");
        return candidates;
    }

    private static string Normalize(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        var space = false;
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static HashSet<string> Tokens(string text)
        => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CandidateMember(KnowledgeEntry Entry);

    private sealed record Candidate(
        ConsolidationJobType Type,
        string Reason,
        List<CandidateMember> Members,
        Guid? ProjectId,
        Guid? RepositoryId,
        string ReasonKey)
    {
        public string ScopeKey => $"{ProjectId?.ToString() ?? "global"}|{RepositoryId?.ToString() ?? "all"}";
    }

    private sealed class UnionFind
    {
        private readonly Dictionary<Guid, Guid> _parent;

        public UnionFind(IEnumerable<Guid> ids)
        {
            _parent = ids.ToDictionary(id => id, id => id);
        }

        public Guid Find(Guid id)
        {
            if (!_parent.TryGetValue(id, out var parent)) return id;
            if (parent == id) return id;
            var root = Find(parent);
            _parent[id] = root;
            return root;
        }

        public void Union(Guid a, Guid b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }
    }
}
