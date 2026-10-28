using System.ComponentModel;
using System.Text;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public sealed class TaskTools
{
    private readonly TaskService _tasks;
    private readonly ProjectService _projects;
    private readonly IMcpUsageRecorder? _usage;
    private readonly ILogger<TaskTools> _logger;

    public TaskTools(TaskService tasks, ProjectService projects, ILogger<TaskTools> logger, IMcpUsageRecorder? usage = null)
    {
        _tasks = tasks;
        _projects = projects;
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

    [McpServerTool, Description("Create a task in the central task store. kind: work|temporary|research|note. status: open|in_progress|blocked|done|cancelled. priority: 0=critical..4=backlog. externalKey makes the call idempotent. projectId scopes the task to a project; repositoryId narrows it to one repository (a project may link several).")]
    public Task<string> CreateTaskAsync(
        [Description("Task title")] string title,
        [Description("Optional task description / acceptance criteria")] string? description = null,
        [Description("work|temporary|research|note")] string? kind = null,
        [Description("open|in_progress|blocked|done|cancelled")] string? status = null,
        [Description("0=critical, 1=high, 2=normal, 3=low, 4=backlog")] int? priority = null,
        [Description("Project Id (Guid)")] Guid? projectId = null,
        [Description("Repository Id (Guid)")] Guid? repositoryId = null,
        [Description("Parent task Id (Guid)")] Guid? parentTaskId = null,
        [Description("Idempotency key; repeat calls return the existing task")] string? externalKey = null,
        [Description("Author/source label")] string? createdBy = null,
        [Description("Create in global scope (cross-project). Explicit opt-in only.")] bool @global = false,
        CancellationToken ct = default)
        => TrackAsync("create_task", repositoryId, async () =>
        {
            if (projectId is null && repositoryId is null && !@global)
                return "Error: scope required. Pass projectId (call list_projects or create_project first) or repositoryId. Use global=true only for genuinely cross-project tasks.";
            if (@global && (projectId is not null || repositoryId is not null))
                return "Error: pass either projectId/repositoryId or global=true, not both.";
            var request = new CreateTaskRequest(
                title, description, ParseKind(kind), ParseState(status), ParsePriority(priority),
                projectId, repositoryId, parentTaskId, "agent", externalKey, createdBy);
            var result = await _tasks.CreateAsync(request, ct).ConfigureAwait(false);
            return $"Task {result.Task.Id} ({(result.Created ? "created" : "existing")}) version={result.Task.Version}\n" +
                   FormatTask(result.Task);
        });

    [McpServerTool, Description("Get a task with comments, links and change history.")]
    public Task<string> GetTaskAsync(
        [Description("Task Id (Guid)")] Guid taskId, CancellationToken ct = default)
        => TrackAsync("get_task", null, async () =>
        {
            var detail = await _tasks.GetDetailAsync(taskId, ct).ConfigureAwait(false);
            return FormatTask(detail.Task) +
                   $"\nComments ({detail.Comments.Count}):\n" +
                   string.Join("\n", detail.Comments.Select(c => $"- {c.Author} {c.CreatedAt:u}: {Truncate(c.Body, 300)}")) +
                   $"\nLinks ({detail.Links.Count}):\n" +
                   string.Join("\n", detail.Links.Select(l => $"- {l.Relation} -> {l.ToType}:{l.ToId}")) +
                   $"\nHistory ({detail.History.Count}):\n" +
                   string.Join("\n", detail.History.Select(h => $"- {h.At:u} {h.Actor ?? "-"} {h.Action} v{h.VersionFrom}->{h.VersionTo}"));
        });

    [McpServerTool, Description("List/filter tasks. By default archived tasks are hidden. Use query for title/description substring.")]
    public Task<string> ListTasksAsync(
        [Description("Title/description substring")] string? query = null,
        [Description("open|in_progress|blocked|done|cancelled")] string? status = null,
        [Description("work|temporary|research|note")] string? kind = null,
        [Description("Project Id (Guid)")] Guid? projectId = null,
        [Description("Repository Id (Guid)")] Guid? repositoryId = null,
        [Description("Parent task Id (Guid)")] Guid? parentTaskId = null,
        [Description("Include archived tasks")] bool includeArchived = false,
        [Description("Only root tasks (no parent)")] bool rootOnly = false,
        [Description("Max results (1..200)")] int limit = 50,
        [Description("Offset")] int offset = 0,
        CancellationToken ct = default)
        => TrackAsync("list_tasks", repositoryId, async () =>
        {
            var tasks = await _tasks.ListAsync(new TaskQuery(
                query, ParseStateOrNull(status), ParseKindOrNull(kind), projectId, repositoryId,
                parentTaskId, includeArchived, rootOnly, limit, offset), ct).ConfigureAwait(false);
            if (tasks.Count == 0) return "No tasks found";
            var sb = new StringBuilder($"Tasks ({tasks.Count}):\n");
            foreach (var task in tasks) sb.AppendLine(FormatTask(task));
            return sb.ToString();
        });

    [McpServerTool, Description("Update a task using optimistic concurrency. expectedVersion must match the current version; on conflict re-read the task and retry.")]
    public Task<string> UpdateTaskAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("Version returned by the last read/write")] int expectedVersion,
        [Description("New title")] string? title = null,
        [Description("New description")] string? description = null,
        [Description("work|temporary|research|note")] string? kind = null,
        [Description("open|in_progress|blocked|done|cancelled|archived")] string? status = null,
        [Description("0..4")] int? priority = null,
        [Description("New parent task Id (Guid)")] Guid? parentTaskId = null,
        [Description("Clear the parent task")] bool clearParent = false,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("update_task", null, async () =>
        {
            var request = new UpdateTaskRequest(
                expectedVersion, title, description, ParseKindOrNull(kind), ParseStateOrNull(status),
                priority is null ? null : ParsePriority(priority), ParentTaskId: parentTaskId,
                ClearParent: clearParent, Actor: actor);
            var result = await _tasks.UpdateAsync(taskId, request, ct).ConfigureAwait(false);
            return FormatTask(result.Task);
        });

    [McpServerTool, Description("Get the task hierarchy as a tree. If rootTaskId is omitted, returns root tasks for the project/repository.")]
    public Task<string> GetTaskTreeAsync(
        [Description("Root task Id (Guid); omit for all roots")] Guid? rootTaskId = null,
        [Description("Project Id (Guid)")] Guid? projectId = null,
        [Description("Repository Id (Guid)")] Guid? repositoryId = null,
        [Description("Max depth (1..20)")] int maxDepth = 10,
        [Description("Include archived tasks")] bool includeArchived = false,
        CancellationToken ct = default)
        => TrackAsync("get_task_tree", repositoryId, async () =>
        {
            var tree = await _tasks.GetTreeAsync(rootTaskId, projectId, repositoryId, maxDepth, includeArchived, ct)
                .ConfigureAwait(false);
            var sb = new StringBuilder();
            void Append(TaskTreeNode node, int level)
            {
                sb.AppendLine(new string(' ', level * 2) + FormatTask(node.Task));
                foreach (var child in node.Children) Append(child, level + 1);
            }
            foreach (var root in tree.Roots) Append(root, 0);
            return sb.Length == 0 ? "No tasks found" : sb.ToString();
        });

    [McpServerTool, Description("Add a depends_on dependency between tasks. Cycles are rejected.")]
    public Task<string> AddTaskDependencyAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("Task that must be completed first (Guid)")] Guid dependsOnTaskId,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("add_task_dependency", null, async () =>
        {
            var result = await _tasks.AddDependencyAsync(taskId, dependsOnTaskId, actor, ct).ConfigureAwait(false);
            return $"{(result.Created ? "Dependency added" : "Dependency already existed")}: {taskId} depends_on {dependsOnTaskId}";
        });

    [McpServerTool, Description("Remove a depends_on dependency between tasks.")]
    public Task<string> RemoveTaskDependencyAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("Dependency task Id (Guid)")] Guid dependsOnTaskId,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("remove_task_dependency", null, async () =>
        {
            var removed = await _tasks.RemoveDependencyAsync(taskId, dependsOnTaskId, actor, ct).ConfigureAwait(false);
            return removed ? "Dependency removed" : "Dependency not found";
        });

    [McpServerTool, Description("Add a comment or research result to a task.")]
    public Task<string> AddTaskCommentAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("Comment body")] string body,
        [Description("Author label")] string? author = null,
        CancellationToken ct = default)
        => TrackAsync("add_task_comment", null, async () =>
        {
            var result = await _tasks.AddCommentAsync(taskId, body, author, ct).ConfigureAwait(false);
            return $"Comment {result.Comment.Id} added to task {taskId}";
        });

    [McpServerTool, Description("Archive a task (requires the current version). Archived tasks are excluded by default.")]
    public Task<string> ArchiveTaskAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("Current task version")] int expectedVersion,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("archive_task", null, async () =>
            FormatTask(await _tasks.ArchiveAsync(taskId, expectedVersion, actor, ct).ConfigureAwait(false)));

    [McpServerTool, Description("Restore an archived task to its previous status.")]
    public Task<string> RestoreTaskAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("Current task version")] int expectedVersion,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("restore_task", null, async () =>
            FormatTask(await _tasks.RestoreAsync(taskId, expectedVersion, actor, ct).ConfigureAwait(false)));

    [McpServerTool, Description("Show the change history of a task (append-only audit trail).")]
    public Task<string> GetTaskHistoryAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("Max entries")] int limit = 100,
        CancellationToken ct = default)
        => TrackAsync("get_task_history", null, async () =>
        {
            var result = await _tasks.GetHistoryAsync(taskId, limit, ct).ConfigureAwait(false);
            return $"History for task {taskId} ({result.History.Count}):\n" +
                   string.Join("\n", result.History.Select(h =>
                       $"- {h.At:u} {h.Actor ?? "-"} {h.Action} v{h.VersionFrom}->{h.VersionTo} {Truncate(h.Changes, 300)}"));
        });

    [McpServerTool, Description("Link a task to knowledge, another task, a symbol, file, repository or project.")]
    public Task<string> LinkTaskAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("knowledge|task|symbol|file|repository|project")] string targetType,
        [Description("Target Id (Guid)")] Guid targetId,
        [Description("relates|depends_on|blocks|derived_from|supersedes|contradicts|implements|source|mentions")] string relation = "relates",
        [Description("Optional note")] string? note = null,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("link_task", null, async () =>
        {
            var result = await _tasks.LinkAsync(taskId,
                new KnowledgeLinkRequest(targetType, targetId, relation, note), actor, ct).ConfigureAwait(false);
            return $"Linked task {taskId} -> {result.Link.ToType}:{result.Link.ToId} ({result.Link.Relation})";
        });

    [McpServerTool, Description("Remove a link from a task.")]
    public Task<string> UnlinkTaskAsync(
        [Description("Task Id (Guid)")] Guid taskId,
        [Description("knowledge|task|symbol|file|repository|project")] string targetType,
        [Description("Target Id (Guid)")] Guid targetId,
        [Description("Relation to remove")] string relation = "relates",
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("unlink_task", null, async () =>
        {
            var removed = await _tasks.UnlinkAsync(taskId, targetType, targetId, relation, actor, ct)
                .ConfigureAwait(false);
            return removed ? "Link removed" : "Link not found";
        });

    [McpServerTool, Description("Import checkbox TODO items from a markdown file in a repository checkout. Repeat imports are idempotent (external key per item). Use dryRun to preview.")]
    public Task<string> ImportTodosAsync(
        [Description("Repository Id (Guid)")] Guid repositoryId,
        [Description("Path relative to the repository root")] string path,
        [Description("Project Id (Guid); defaults to the repository's project")] Guid? projectId = null,
        [Description("Preview without writing")] bool dryRun = false,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("import_todos", repositoryId, async () =>
        {
            var result = await _tasks.ImportTodosAsync(repositoryId, path, projectId, dryRun, actor, ct)
                .ConfigureAwait(false);
            return $"Import {(result.DryRun ? "(dry run) " : "")}found={result.Found} created={result.Created} " +
                   $"updated={result.Updated} skipped={result.Skipped}\n" +
                   string.Join("\n", result.Items.Take(100).Select(i => $"- [{i.Action}] {i.Status} {i.Title}"));
        });

    [McpServerTool, Description("Export tasks as a markdown checklist compatible with the legacy TODO-file workflow. Returns the markdown text.")]
    public Task<string> ExportTasksAsync(
        [Description("Project Id (Guid)")] Guid? projectId = null,
        [Description("Repository Id (Guid)")] Guid? repositoryId = null,
        [Description("Include archived tasks")] bool includeArchived = false,
        CancellationToken ct = default)
        => TrackAsync("export_tasks", repositoryId, async () =>
        {
            var result = await _tasks.ExportAsync(projectId, repositoryId, includeArchived, ct).ConfigureAwait(false);
            return $"File: {result.FileName} ({result.Count} tasks)\n\n{result.Content}";
        });

    private static string StatusLabel(TaskState status)
        => status == TaskState.InProgress ? "in_progress" : status.ToString().ToLowerInvariant();

    private static string FormatTask(TaskItem task)
        => $"Task {task.Id} v{task.Version} [{StatusLabel(task.Status)}] " +
           $"{task.Kind.ToString().ToLowerInvariant()} p{(int)task.Priority} — {task.Title}" +
           (task.ProjectId is null ? " (global)" : $" project={task.ProjectId}") +
           (task.RepositoryId is null ? "" : $" repo={task.RepositoryId}") +
           (task.ParentTaskId is null ? "" : $" parent={task.ParentTaskId}") +
           (string.IsNullOrWhiteSpace(task.Description) ? "" : $"\n  {Truncate(task.Description, 500)}");

    private static TaskKind ParseKind(string? value)
        => TryParseEnum(value, out TaskKind parsed) ? parsed : TaskKind.Work;

    private static TaskKind? ParseKindOrNull(string? value)
        => TryParseEnum(value, out TaskKind parsed) ? parsed : null;

    private static TaskState ParseState(string? value)
        => TryParseEnum(value, out TaskState parsed) ? parsed : TaskState.Open;

    private static TaskState? ParseStateOrNull(string? value)
        => TryParseEnum(value, out TaskState parsed) ? parsed : null;

    private static TaskPriority ParsePriority(int? value)
        => value is null ? TaskPriority.Normal : (TaskPriority)Math.Clamp(value.Value, 0, 4);

    // Tool schemas document snake_case values (in_progress, in-progress), but the enums are
    // PascalCase (InProgress). Enum.TryParse alone rejects the documented spelling and the
    // update silently keeps the old value.
    private static bool TryParseEnum<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace("_", "").Replace("-", "").Replace(" ", "");
        return Enum.TryParse(normalized, ignoreCase: true, out parsed);
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";
}
