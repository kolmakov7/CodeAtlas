using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Knowledge.Models;

namespace CodeRag.Knowledge.Services;

public sealed partial class TaskService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITaskStore _tasks;
    private readonly IRepositoryStore _repositories;
    private readonly IEntityLinkStore _links;
    private readonly IEntityHistoryStore _history;
    private readonly ProjectService _projectService;

    public TaskService(
        ITaskStore tasks,
        IProjectStore projects,
        IRepositoryStore repositories,
        IEntityLinkStore links,
        IEntityHistoryStore history,
        ProjectService projectService)
    {
        _tasks = tasks;
        _repositories = repositories;
        _links = links;
        _history = history;
        _projectService = projectService;
    }

    public async Task<TaskResult> CreateAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new DomainValidationException("Task title must not be empty.");

        var project = await _projectService.ResolveAsync(request.ProjectId, request.RepositoryId, ct).ConfigureAwait(false);
        var repositoryId = request.RepositoryId;
        if (repositoryId is not null)
        {
            var repository = await _repositories.GetByIdAsync(repositoryId.Value, ct).ConfigureAwait(false);
            if (repository is null) throw new DomainValidationException($"Repository {repositoryId} not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExternalKey))
        {
            var existing = await _tasks.GetByExternalKeyAsync(project?.Id, request.ExternalKey.Trim(), ct).ConfigureAwait(false);
            if (existing is not null)
                return new TaskResult(existing, false, await _links.GetOutgoingAsync(EntityTypes.Task, existing.Id, ct).ConfigureAwait(false));
        }

        if (request.ParentTaskId is not null)
        {
            var parent = await GetAsync(request.ParentTaskId.Value, ct).ConfigureAwait(false);
            if (parent.ProjectId != project?.Id)
                throw new DomainValidationException("Parent task belongs to a different project.");
        }

        var task = TaskItem.Create(
            Guid.NewGuid(), request.Title, request.Description, request.Kind, request.Status, request.Priority,
            project?.Id, repositoryId, request.ParentTaskId, request.Source, request.ExternalKey,
            request.CreatedBy, request.Metadata);
        await _tasks.CreateAsync(task, ct).ConfigureAwait(false);
        await AddHistoryAsync(task, "created", request.CreatedBy, null, task.Version, new { title = task.Title }, ct).ConfigureAwait(false);
        return new TaskResult(task, true, Array.Empty<EntityLink>());
    }

    public async Task<TaskItem> GetAsync(Guid id, CancellationToken ct = default)
    {
        var task = await _tasks.GetByIdAsync(id, ct).ConfigureAwait(false);
        return task ?? throw new DomainValidationException($"Task {id} not found.");
    }

    public async Task<TaskDetail> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        var task = await GetAsync(id, ct).ConfigureAwait(false);
        var comments = await _tasks.GetCommentsAsync(id, 200, ct).ConfigureAwait(false);
        var links = await _links.GetOutgoingAsync(EntityTypes.Task, id, ct).ConfigureAwait(false);
        var history = await _history.ListAsync(EntityTypes.Task, id, 200, ct).ConfigureAwait(false);
        return new TaskDetail(task, comments, links, history);
    }

    public Task<IReadOnlyList<TaskItem>> ListAsync(TaskQuery query, CancellationToken ct = default)
        => _tasks.ListAsync(query, ct);

    public async Task<TaskResult> UpdateAsync(Guid id, UpdateTaskRequest request, CancellationToken ct = default)
    {
        var task = await GetAsync(id, ct).ConfigureAwait(false);
        var changes = new Dictionary<string, object?>();

        if (request.ProjectId is not null || request.RepositoryId is not null)
        {
            var project = await _projectService.ResolveAsync(request.ProjectId, request.RepositoryId, ct).ConfigureAwait(false);
            task.SetScope(project?.Id, request.RepositoryId);
            changes["scope"] = new { projectId = project?.Id, repositoryId = request.RepositoryId };
        }

        if (request.Title is not null || request.Description is not null || request.Kind is not null
            || request.Priority is not null || request.Metadata is not null)
        {
            task.UpdateContent(
                request.Title ?? task.Title,
                request.Description ?? task.Description,
                request.Kind ?? task.Kind,
                request.Priority ?? task.Priority,
                request.Metadata ?? task.Metadata);
            changes["content"] = new { task.Title, task.Description, kind = task.Kind.ToString() };
        }

        if (request.Status is not null && request.Status.Value != task.Status)
        {
            var from = task.Status;
            task.SetStatus(request.Status.Value);
            changes["status"] = new { from = from.ToString(), to = task.Status.ToString() };
        }

        if (request.ClearParent)
        {
            task.SetParent(null);
            changes["parentTaskId"] = null;
        }
        else if (request.ParentTaskId is not null && request.ParentTaskId != task.ParentTaskId)
        {
            var parent = await GetAsync(request.ParentTaskId.Value, ct).ConfigureAwait(false);
            if (parent.ProjectId != task.ProjectId)
                throw new DomainValidationException("Parent task belongs to a different project.");
            await EnsureNoParentCycleAsync(task.Id, request.ParentTaskId.Value, ct).ConfigureAwait(false);
            task.SetParent(request.ParentTaskId);
            changes["parentTaskId"] = request.ParentTaskId;
        }

        if (changes.Count == 0)
            return new TaskResult(task, false, await _links.GetOutgoingAsync(EntityTypes.Task, task.Id, ct).ConfigureAwait(false));

        task.BumpVersion();
        var updated = await _tasks.UpdateAsync(task, request.ExpectedVersion, ct).ConfigureAwait(false);
        if (!updated)
        {
            var current = await GetAsync(id, ct).ConfigureAwait(false);
            throw new VersionConflictException("Task", id, request.ExpectedVersion, current.Version);
        }

        await AddHistoryAsync(task, "updated", request.Actor, request.ExpectedVersion, task.Version, changes, ct).ConfigureAwait(false);
        return new TaskResult(task, false, await _links.GetOutgoingAsync(EntityTypes.Task, task.Id, ct).ConfigureAwait(false));
    }

    public async Task<TaskItem> ArchiveAsync(Guid id, int expectedVersion, string? actor = null, CancellationToken ct = default)
    {
        var task = await GetAsync(id, ct).ConfigureAwait(false);
        task.SetStatus(TaskState.Archived);
        return await PersistAsync(task, expectedVersion, "archived", actor, ct).ConfigureAwait(false);
    }

    public async Task<TaskItem> RestoreAsync(Guid id, int expectedVersion, string? actor = null, CancellationToken ct = default)
    {
        var task = await GetAsync(id, ct).ConfigureAwait(false);
        var status = task.PreviousStatus ?? TaskState.Open;
        task.SetStatus(status);
        return await PersistAsync(task, expectedVersion, "restored", actor, ct).ConfigureAwait(false);
    }    public async Task<TaskTreeResult> GetTreeAsync(
        Guid? rootTaskId, Guid? projectId, Guid? repositoryId, int maxDepth = 10, bool includeArchived = false,
        CancellationToken ct = default)
    {
        var depth = Math.Clamp(maxDepth, 1, 20);
        var visited = new HashSet<Guid>();
        async Task<TaskTreeNode> BuildAsync(TaskItem task, int level)
        {
            visited.Add(task.Id);
            if (level >= depth) return new TaskTreeNode(task, Array.Empty<TaskTreeNode>());
            var children = await _tasks.GetChildrenAsync(task.Id, includeArchived, ct).ConfigureAwait(false);
            var nodes = new List<TaskTreeNode>();
            foreach (var child in children.Where(c => !visited.Contains(c.Id)))
                nodes.Add(await BuildAsync(child, level + 1).ConfigureAwait(false));
            return new TaskTreeNode(task, nodes);
        }

        if (rootTaskId is not null)
        {
            var root = await GetAsync(rootTaskId.Value, ct).ConfigureAwait(false);
            return new TaskTreeResult(new[] { await BuildAsync(root, 0).ConfigureAwait(false) });
        }

        var roots = await _tasks.ListAsync(new TaskQuery(
            ProjectId: projectId, RepositoryId: repositoryId, IncludeArchived: includeArchived,
            RootOnly: true, Limit: 200), ct).ConfigureAwait(false);
        var built = new List<TaskTreeNode>();
        foreach (var root in roots.Where(r => !visited.Contains(r.Id)))
            built.Add(await BuildAsync(root, 0).ConfigureAwait(false));
        return new TaskTreeResult(built);
    }

    public async Task<TaskDependencyResult> AddDependencyAsync(
        Guid taskId, Guid dependsOnTaskId, string? actor = null, CancellationToken ct = default)
    {
        var task = await GetAsync(taskId, ct).ConfigureAwait(false);
        var dependsOn = await GetAsync(dependsOnTaskId, ct).ConfigureAwait(false);
        if (task.ProjectId != dependsOn.ProjectId)
            throw new DomainValidationException("Dependencies must belong to the same project.");
        if (await WouldCycleAsync(taskId, dependsOnTaskId, ct).ConfigureAwait(false))
            throw new DomainValidationException("Adding this dependency would create a cycle.");

        var existing = await _links.ListAsync(EntityTypes.Task, taskId, EntityTypes.Task, dependsOnTaskId,
            LinkRelations.DependsOn, 1, ct).ConfigureAwait(false);
        if (existing.Count > 0) return new TaskDependencyResult(task, dependsOn, false);

        var link = EntityLink.Create(Guid.NewGuid(), EntityTypes.Task, taskId, EntityTypes.Task, dependsOnTaskId,
            LinkRelations.DependsOn, createdBy: actor);
        await _links.AddAsync(link, ct).ConfigureAwait(false);
        await AddHistoryAsync(task, "dependency_added", actor, task.Version, task.Version,
            new { dependsOnTaskId }, ct).ConfigureAwait(false);
        return new TaskDependencyResult(task, dependsOn, true);
    }

    public async Task<bool> RemoveDependencyAsync(
        Guid taskId, Guid dependsOnTaskId, string? actor = null, CancellationToken ct = default)
    {
        var task = await GetAsync(taskId, ct).ConfigureAwait(false);
        var removed = await _links.RemoveAsync(EntityTypes.Task, taskId, EntityTypes.Task, dependsOnTaskId,
            LinkRelations.DependsOn, ct).ConfigureAwait(false);
        if (removed)
            await AddHistoryAsync(task, "dependency_removed", actor, task.Version, task.Version,
                new { dependsOnTaskId }, ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<EntityLink>> GetDependenciesAsync(Guid taskId, CancellationToken ct = default)
        => await _links.ListAsync(EntityTypes.Task, taskId, EntityTypes.Task, null, LinkRelations.DependsOn, 500, ct)
            .ConfigureAwait(false);

    public async Task<TaskCommentResult> AddCommentAsync(
        Guid taskId, string body, string? author = null, CancellationToken ct = default)
    {
        var task = await GetAsync(taskId, ct).ConfigureAwait(false);
        var comment = TaskComment.Create(Guid.NewGuid(), taskId, body, author);
        await _tasks.AddCommentAsync(comment, ct).ConfigureAwait(false);
        await AddHistoryAsync(task, "comment_added", author, task.Version, task.Version,
            new { commentId = comment.Id }, ct).ConfigureAwait(false);
        return new TaskCommentResult(task, comment);
    }

    public Task<IReadOnlyList<TaskComment>> GetCommentsAsync(Guid taskId, int limit = 100, CancellationToken ct = default)
        => _tasks.GetCommentsAsync(taskId, limit, ct);

    public async Task<TaskHistoryResult> GetHistoryAsync(Guid taskId, int limit = 100, CancellationToken ct = default)
    {
        var task = await GetAsync(taskId, ct).ConfigureAwait(false);
        var history = await _history.ListAsync(EntityTypes.Task, taskId, limit, ct).ConfigureAwait(false);
        return new TaskHistoryResult(task, history);
    }

    public async Task<KnowledgeLinkResult> LinkAsync(
        Guid taskId, KnowledgeLinkRequest request, string? actor = null, CancellationToken ct = default)
    {
        var task = await GetAsync(taskId, ct).ConfigureAwait(false);
        if (!EntityTypes.IsValid(request.TargetType))
            throw new DomainValidationException($"Unknown targetType '{request.TargetType}'.");
        var link = EntityLink.Create(Guid.NewGuid(), EntityTypes.Task, taskId,
            request.TargetType.Trim().ToLowerInvariant(), request.TargetId, request.Relation,
            request.Note, actor);
        await _links.AddAsync(link, ct).ConfigureAwait(false);
        await AddHistoryAsync(task, "link_added", actor, task.Version, task.Version,
            new { targetType = link.ToType, targetId = link.ToId, relation = link.Relation }, ct).ConfigureAwait(false);
        return new KnowledgeLinkResult(link, true);
    }

    public async Task<bool> UnlinkAsync(
        Guid taskId, string targetType, Guid targetId, string relation, string? actor = null,
        CancellationToken ct = default)
    {
        var task = await GetAsync(taskId, ct).ConfigureAwait(false);
        var removed = await _links.RemoveAsync(EntityTypes.Task, taskId, targetType, targetId, relation, ct)
            .ConfigureAwait(false);
        if (removed)
            await AddHistoryAsync(task, "link_removed", actor, task.Version, task.Version,
                new { targetType, targetId, relation }, ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<TodoImportResult> ImportTodosAsync(
        Guid repositoryId, string relativePath, Guid? projectId = null, bool dryRun = false,
        string? actor = null, CancellationToken ct = default)
    {
        var repository = await _repositories.GetByIdAsync(repositoryId, ct).ConfigureAwait(false);
        if (repository is null) throw new DomainValidationException($"Repository {repositoryId} not found.");

        var root = Path.GetFullPath(repository.LocalPath.Value);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, root, StringComparison.Ordinal))
            throw new DomainValidationException("Path traversal is not allowed.");
        if (!File.Exists(fullPath)) throw new DomainValidationException($"File '{relativePath}' not found.");

        var project = await _projectService.ResolveAsync(projectId, repositoryId, ct).ConfigureAwait(false);
        var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        var parsed = ParseTodoMarkdown(content);
        var found = parsed.Count;
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var items = new List<TodoImportItem>();

        foreach (var item in parsed.Take(500))
        {
            var key = HashKey(repositoryId, relativePath, item.HeadingPath, item.Title);
            var existing = await _tasks.GetByExternalKeyAsync(project?.Id, key, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Status != item.Status)
                {
                    existing.SetStatus(item.Status);
                    existing.BumpVersion();
                    if (dryRun)
                    {
                        updated++;
                        items.Add(new TodoImportItem(key, item.Title, item.Status, "update"));
                        continue;
                    }
                    var ok = await _tasks.UpdateAsync(existing, existing.Version - 1, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        updated++;
                        items.Add(new TodoImportItem(key, item.Title, item.Status, "update"));
                        await AddHistoryAsync(existing, "import_status_update", actor,
                            existing.Version - 1, existing.Version, new { source = relativePath }, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        skipped++;
                        items.Add(new TodoImportItem(key, item.Title, item.Status, "skip"));
                    }
                }
                else
                {
                    skipped++;
                    items.Add(new TodoImportItem(key, item.Title, item.Status, "skip"));
                }
                continue;
            }

            if (dryRun)
            {
                created++;
                items.Add(new TodoImportItem(key, item.Title, item.Status, "create"));
                continue;
            }

            var task = TaskItem.Create(Guid.NewGuid(), item.Title,
                string.IsNullOrWhiteSpace(item.HeadingPath) ? null : item.HeadingPath,
                TaskKind.Work, item.Status, TaskPriority.Normal, project?.Id, repositoryId,
                null, "import", key, actor, $"{{\"importPath\":{JsonSerializer.Serialize(relativePath)}}}");
            await _tasks.CreateAsync(task, ct).ConfigureAwait(false);
            await AddHistoryAsync(task, "imported", actor, null, task.Version,
                new { source = relativePath }, ct).ConfigureAwait(false);
            created++;
            items.Add(new TodoImportItem(key, item.Title, item.Status, "create"));
        }

        return new TodoImportResult(dryRun, found, created, updated, skipped, items);
    }

    public async Task<TodoExportResult> ExportAsync(
        Guid? projectId, Guid? repositoryId, bool includeArchived = false, CancellationToken ct = default)
    {
        var tasks = await _tasks.ListAsync(new TaskQuery(
            ProjectId: projectId, RepositoryId: repositoryId, IncludeArchived: includeArchived, Limit: 500), ct)
            .ConfigureAwait(false);

        var byParent = tasks.GroupBy(t => t.ParentTaskId ?? Guid.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt).ToList());
        var sb = new StringBuilder();
        var title = projectId is null && repositoryId is null ? "CodeAtlas tasks" : "TODO";
        sb.AppendLine("# " + title);
        sb.AppendLine();
        void Append(Guid? parentId, int level)
        {
            if (!byParent.TryGetValue(parentId ?? Guid.Empty, out var children)) return;
            foreach (var task in children)
            {
                var indent = new string(' ', level * 2);
                sb.AppendLine($"{indent}- [{Marker(task.Status)}] {task.Title}");
                if (!string.IsNullOrWhiteSpace(task.Description))
                {
                    foreach (var line in task.Description.Split('\n'))
                        sb.AppendLine($"{indent}  > {line.TrimEnd()}");
                }
                Append(task.Id, level + 1);
            }
        }
        Append(null, 0);
        var fileName = $"TODO_{title.Replace(' ', '_').ToLowerInvariant()}.md";
        return new TodoExportResult(fileName, sb.ToString(), tasks.Count);
    }

    private async Task<TaskItem> PersistAsync(
        TaskItem task, int expectedVersion, string action, string? actor, CancellationToken ct)
    {
        task.BumpVersion();
        var updated = await _tasks.UpdateAsync(task, expectedVersion, ct).ConfigureAwait(false);
        if (!updated)
        {
            var current = await GetAsync(task.Id, ct).ConfigureAwait(false);
            throw new VersionConflictException("Task", task.Id, expectedVersion, current.Version);
        }
        await AddHistoryAsync(task, action, actor, expectedVersion, task.Version,
            new { status = task.Status.ToString() }, ct).ConfigureAwait(false);
        return task;
    }

    private async Task EnsureNoParentCycleAsync(Guid taskId, Guid parentId, CancellationToken ct = default)
    {
        var current = parentId;
        for (var i = 0; i < 100; i++)
        {
            if (current == taskId)
                throw new DomainValidationException("Setting this parent would create a cycle.");
            var parent = await _tasks.GetByIdAsync(current, ct).ConfigureAwait(false);
            if (parent?.ParentTaskId is null) return;
            current = parent.ParentTaskId.Value;
        }
        throw new DomainValidationException("Task hierarchy is too deep.");
    }

    private async Task<bool> WouldCycleAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken ct)
    {
        var queue = new Queue<Guid>();
        var visited = new HashSet<Guid>();
        queue.Enqueue(dependsOnTaskId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == taskId) return true;
            if (!visited.Add(current)) continue;
            var links = await _links.ListAsync(EntityTypes.Task, current, EntityTypes.Task, null,
                LinkRelations.DependsOn, 500, ct).ConfigureAwait(false);
            foreach (var link in links) queue.Enqueue(link.ToId);
        }
        return false;
    }

    private Task AddHistoryAsync(
        TaskItem task, string action, string? actor, int? from, int? to, object changes, CancellationToken ct)
        => _history.AddAsync(EntityHistoryEntry.Create(
            EntityTypes.Task, task.Id, action, actor, from, to, JsonSerializer.Serialize(changes, JsonOptions)), ct);

    private static string Marker(TaskState status) => status switch
    {
        TaskState.Open => " ",
        TaskState.InProgress => "~",
        TaskState.Blocked => "!",
        TaskState.Done => "x",
        TaskState.Cancelled => "-",
        TaskState.Archived => "x",
        _ => " "
    };

    private static string HashKey(Guid repositoryId, string path, string headingPath, string title)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            repositoryId + "|" + path + "|" + headingPath + "|" + title));
        return "todo:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static List<ParsedTodo> ParseTodoMarkdown(string content)
    {
        var result = new List<ParsedTodo>();
        var headings = new SortedDictionary<int, string>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var text = heading.Groups[2].Value.Trim();
                foreach (var key in headings.Keys.Where(k => k >= level).ToList()) headings.Remove(key);
                headings[level] = text;
                continue;
            }

            var match = CheckboxRegex().Match(line);
            if (!match.Success) continue;
            var marker = match.Groups[1].Value;
            var title = match.Groups[2].Value.Trim();
            if (title.Length == 0) continue;
            result.Add(new ParsedTodo(
                title,
                string.Join(" / ", headings.Values),
                marker switch
                {
                    "~" or ">" => TaskState.InProgress,
                    "x" or "X" => TaskState.Done,
                    "!" => TaskState.Blocked,
                    "-" => TaskState.Cancelled,
                    _ => TaskState.Open
                }));
        }
        return result;
    }

    private sealed record ParsedTodo(string Title, string HeadingPath, TaskState Status);

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+\[([ xX~>!\-])\]\s+(.+)$")]
    private static partial Regex CheckboxRegex();
}
