using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class TaskStore : ITaskStore
{
    private readonly IDbConnectionFactory _factory;

    public TaskStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task CreateAsync(TaskItem task, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            @"INSERT INTO tasks(id, title, description, kind, status, previous_status, priority,
                project_id, repository_id, parent_task_id, source, external_key, created_by, metadata,
                created_at, updated_at, archived_at, version)
              VALUES(@id, @title, @description, @kind, @status, @previous, @priority,
                @project, @repo, @parent, @source, @externalKey, @createdBy, @metadata,
                @createdAt, @updatedAt, @archivedAt, @version);",
            Params(task)).ConfigureAwait(false);
    }

    public async Task<TaskItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(
            "SELECT * FROM tasks WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<TaskItem?> GetByExternalKeyAsync(Guid? projectId, string externalKey, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(
            projectId is null
                ? "SELECT * FROM tasks WHERE project_id IS NULL AND external_key=@key LIMIT 1;"
                : "SELECT * FROM tasks WHERE project_id=@project AND external_key=@key LIMIT 1;",
            new { project = projectId?.ToString(), key = externalKey }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<TaskItem>> ListAsync(TaskQuery query, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ps = new DynamicParameters();
        var where = new List<string>();

        if (!query.IncludeArchived)
        {
            where.Add("status <> @archived");
            ps.Add("archived", "archived");
        }
        if (query.Status is not null)
        {
            where.Add("status = @status");
            ps.Add("status", Text(query.Status.Value));
        }
        if (query.Kind is not null)
        {
            where.Add("kind = @kind");
            ps.Add("kind", Text(query.Kind.Value));
        }
        if (query.ProjectId is not null)
        {
            where.Add("project_id = @project");
            ps.Add("project", query.ProjectId.Value.ToString());
        }
        if (query.RepositoryId is not null)
        {
            where.Add("repository_id = @repo");
            ps.Add("repo", query.RepositoryId.Value.ToString());
        }
        if (query.ParentTaskId is not null)
        {
            where.Add("parent_task_id = @parent");
            ps.Add("parent", query.ParentTaskId.Value.ToString());
        }
        if (query.RootOnly)
        {
            where.Add("parent_task_id IS NULL");
        }
        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            where.Add("(title LIKE @q OR description LIKE @q)");
            ps.Add("q", $"%{query.Query.Trim()}%");
        }

        var limit = Math.Clamp(query.Limit, 1, 500);
        var offset = Math.Max(0, query.Offset);
        ps.Add("limit", limit);
        ps.Add("offset", offset);
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var sql = $@"SELECT * FROM tasks {whereSql}
                     ORDER BY priority ASC, updated_at DESC
                     LIMIT @limit OFFSET @offset;";
        var rows = await conn.QueryAsync<TaskRow>(sql, ps).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<TaskItem>> GetChildrenAsync(
        Guid parentTaskId, bool includeArchived, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var sql = includeArchived
            ? "SELECT * FROM tasks WHERE parent_task_id=@parent ORDER BY priority ASC, updated_at DESC;"
            : "SELECT * FROM tasks WHERE parent_task_id=@parent AND status <> 'archived' ORDER BY priority ASC, updated_at DESC;";
        var rows = await conn.QueryAsync<TaskRow>(sql, new { parent = parentTaskId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<bool> UpdateAsync(TaskItem task, int expectedVersion, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var affected = await conn.ExecuteAsync(
            @"UPDATE tasks SET
                title=@title, description=@description, kind=@kind, status=@status,
                previous_status=@previous, priority=@priority, project_id=@project,
                repository_id=@repo, parent_task_id=@parent, source=@source,
                external_key=@externalKey, created_by=@createdBy, metadata=@metadata,
                updated_at=@updatedAt, archived_at=@archivedAt, version=@version
              WHERE id=@id AND version=@expectedVersion;",
            new
            {
                id = task.Id.ToString(),
                title = task.Title,
                description = task.Description,
                kind = Text(task.Kind),
                status = Text(task.Status),
                previous = task.PreviousStatus is null ? null : Text(task.PreviousStatus.Value),
                priority = (int)task.Priority,
                project = task.ProjectId?.ToString(),
                repo = task.RepositoryId?.ToString(),
                parent = task.ParentTaskId?.ToString(),
                source = task.Source,
                externalKey = task.ExternalKey,
                createdBy = task.CreatedBy,
                metadata = task.Metadata,
                updatedAt = task.UpdatedAt.ToString("o"),
                archivedAt = task.ArchivedAt?.ToString("o"),
                version = task.Version,
                expectedVersion
            }).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task AddCommentAsync(TaskComment comment, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT INTO task_comments(id, task_id, author, body, created_at) VALUES(@id, @task, @author, @body, @at);",
            new
            {
                id = comment.Id.ToString(),
                task = comment.TaskId.ToString(),
                author = comment.Author,
                body = comment.Body,
                at = comment.CreatedAt.ToString("o")
            }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskComment>> GetCommentsAsync(
        Guid taskId, int limit = 100, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<CommentRow>(
            "SELECT * FROM task_comments WHERE task_id=@task ORDER BY created_at ASC LIMIT @limit;",
            new { task = taskId.ToString(), limit = Math.Clamp(limit, 1, 500) }).ConfigureAwait(false);
        return rows.Select(r => TaskComment.Rehydrate(
            Guid.Parse(r.id), Guid.Parse(r.task_id), r.author ?? "agent", r.body, DateTimeOffset.Parse(r.created_at))).ToList();
    }

    public async Task<int> CountByProjectAsync(Guid? projectId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(
            projectId is null
                ? "SELECT COUNT(*) FROM tasks WHERE project_id IS NULL;"
                : "SELECT COUNT(*) FROM tasks WHERE project_id=@project;",
            new { project = projectId?.ToString() }).ConfigureAwait(false);
    }

    private static object Params(TaskItem t) => new
    {
        id = t.Id.ToString(),
        title = t.Title,
        description = t.Description,
        kind = Text(t.Kind),
        status = Text(t.Status),
        previous = t.PreviousStatus is null ? null : Text(t.PreviousStatus.Value),
        priority = (int)t.Priority,
        project = t.ProjectId?.ToString(),
        repo = t.RepositoryId?.ToString(),
        parent = t.ParentTaskId?.ToString(),
        source = t.Source,
        externalKey = t.ExternalKey,
        createdBy = t.CreatedBy,
        metadata = t.Metadata,
        createdAt = t.CreatedAt.ToString("o"),
        updatedAt = t.UpdatedAt.ToString("o"),
        archivedAt = t.ArchivedAt?.ToString("o"),
        version = t.Version
    };

    private static TaskItem Map(TaskRow r) => TaskItem.Rehydrate(
        Guid.Parse(r.id),
        r.title,
        r.description,
        Parse<TaskKind>(r.kind, TaskKind.Work),
        Parse<TaskState>(r.status, TaskState.Open),
        string.IsNullOrWhiteSpace(r.previous_status) ? null : Parse<TaskState>(r.previous_status, TaskState.Open),
        Parse<TaskPriority>(r.priority.ToString(), TaskPriority.Normal),
        GuidOrNull(r.project_id),
        GuidOrNull(r.repository_id),
        GuidOrNull(r.parent_task_id),
        r.source,
        r.external_key,
        r.created_by,
        r.metadata,
        DateTimeOffset.Parse(r.created_at),
        DateTimeOffset.Parse(r.updated_at),
        string.IsNullOrWhiteSpace(r.archived_at) ? null : DateTimeOffset.Parse(r.archived_at),
        r.version);

    internal static string Text<TEnum>(TEnum value) where TEnum : struct, Enum
        => value.ToString().ToLowerInvariant();

    internal static TEnum Parse<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum
        => !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<TEnum>(raw, true, out var parsed) ? parsed : fallback;

    internal static Guid? GuidOrNull(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : Guid.Parse(raw);

    private sealed class TaskRow
    {
        public string id { get; set; } = "";
        public string title { get; set; } = "";
        public string? description { get; set; }
        public string kind { get; set; } = "";
        public string status { get; set; } = "";
        public string? previous_status { get; set; }
        public int priority { get; set; }
        public string? project_id { get; set; }
        public string? repository_id { get; set; }
        public string? parent_task_id { get; set; }
        public string source { get; set; } = "agent";
        public string? external_key { get; set; }
        public string? created_by { get; set; }
        public string? metadata { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string? archived_at { get; set; }
        public int version { get; set; }
    }

    private sealed class CommentRow
    {
        public string id { get; set; } = "";
        public string task_id { get; set; } = "";
        public string? author { get; set; }
        public string body { get; set; } = "";
        public string created_at { get; set; } = "";
    }
}
