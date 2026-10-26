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
public sealed class WorkContextTools
{
    private readonly TaskService _tasks;
    private readonly KnowledgeService _knowledge;
    private readonly ProjectService _projects;
    private readonly IRepositoryStore _repositories;
    private readonly IMcpUsageRecorder? _usage;
    private readonly ILogger<WorkContextTools> _logger;

    public WorkContextTools(
        TaskService tasks,
        KnowledgeService knowledge,
        ProjectService projects,
        IRepositoryStore repositories,
        ILogger<WorkContextTools> logger,
        IMcpUsageRecorder? usage = null)
    {
        _tasks = tasks;
        _knowledge = knowledge;
        _projects = projects;
        _repositories = repositories;
        _logger = logger;
        _usage = usage;
    }

    [McpServerTool, Description(
        "One-call work context for a repository or project: open/in-progress/blocked tasks plus the most recent active/stale knowledge, with ids and versions. Use this first at session start (and after a break) instead of calling list_tasks and search_knowledge separately. Pass repositoryId when you work in a repo; projectId when the work spans several repos. Omit both only to discover available projects.")]
    public Task<string> GetWorkContextAsync(
        [Description("Repository Id (Guid); resolves its project automatically")] Guid? repositoryId = null,
        [Description("Project Id (Guid); use when work spans several repositories")] Guid? projectId = null,
        [Description("Max tasks to return (1..100, default 20)")] int? taskLimit = null,
        [Description("Max knowledge entries to return (1..50, default 10)")] int? knowledgeLimit = null,
        [Description("Include archived tasks/knowledge")] bool? includeArchived = null,
        CancellationToken ct = default)
        => TrackAsync("get_work_context", repositoryId, async () =>
        {
            var takeTasks = Math.Clamp(taskLimit ?? 20, 1, 100);
            var takeKnowledge = Math.Clamp(knowledgeLimit ?? 10, 1, 50);
            var withArchived = includeArchived ?? false;

            if (repositoryId is null && projectId is null)
                return await DescribeProjectsAsync(ct).ConfigureAwait(false);

            Project? project = null;
            if (projectId is not null)
            {
                project = await _projects.GetAsync(projectId.Value, ct).ConfigureAwait(false);
            }
            else if (repositoryId is not null)
            {
                var projects = await _projects.GetByRepositoryAsync(repositoryId.Value, ct).ConfigureAwait(false);
                project = projects.FirstOrDefault();
            }

            Repository? repository = repositoryId is null
                ? null
                : await _repositories.GetByIdAsync(repositoryId.Value, ct).ConfigureAwait(false);

            // A repository normally belongs to a project, and tasks created for that repository are
            // stored under the project. Scope by project when known so project-level tasks (no
            // repositoryId) are included; fall back to the repository otherwise.
            var scopeProjectId = project?.Id;
            var scopeRepositoryId = scopeProjectId is null ? repositoryId : null;

            var tasks = await _tasks.ListAsync(new TaskQuery(
                ProjectId: scopeProjectId,
                RepositoryId: scopeRepositoryId,
                IncludeArchived: withArchived,
                Limit: 500), ct).ConfigureAwait(false);

            var openTasks = tasks
                .Where(t => t.Status is TaskState.Open or TaskState.InProgress or TaskState.Blocked)
                .OrderBy(t => t.Status switch
                {
                    TaskState.InProgress => 0,
                    TaskState.Blocked => 1,
                    _ => 2
                })
                .ThenBy(t => t.Priority)
                .ThenByDescending(t => t.UpdatedAt)
                .Take(takeTasks)
                .ToList();

            var knowledge = await _knowledge.ListAsync(new KnowledgeQuery(
                ProjectId: scopeProjectId,
                RepositoryId: scopeRepositoryId,
                Statuses: new[] { KnowledgeStatus.Active, KnowledgeStatus.Stale },
                IncludeArchived: withArchived,
                Limit: takeKnowledge), ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.AppendLine("# Work context");
            sb.AppendLine();
            sb.AppendLine(project is null
                ? "Project: (none — tasks/knowledge are scoped by repository)"
                : $"Project: {project.Name ?? "(unnamed)"} ({project.Id}) repos={project.RepositoryIds.Count}");
            if (repository is not null)
                sb.AppendLine($"Repository: {repository.Name} ({repository.Id})");
            sb.AppendLine();

            var inProgress = tasks.Count(t => t.Status == TaskState.InProgress);
            var blocked = tasks.Count(t => t.Status == TaskState.Blocked);
            sb.AppendLine($"## Open tasks ({openTasks.Count} shown; in_progress={inProgress} blocked={blocked})");
            if (openTasks.Count == 0)
            {
                sb.AppendLine("No open tasks. Create one with create_task when work starts.");
            }
            else
            {
                foreach (var task in openTasks)
                    sb.AppendLine(FormatTask(task));
            }
            sb.AppendLine();

            sb.AppendLine($"## Recent knowledge ({knowledge.Count})");
            if (knowledge.Count == 0)
            {
                sb.AppendLine("No knowledge yet. Save durable decisions, constraints and research with save_knowledge.");
            }
            else
            {
                foreach (var entry in knowledge.OrderByDescending(e => e.UpdatedAt))
                    sb.AppendLine(FormatEntry(entry));
            }
            sb.AppendLine();
            sb.AppendLine("Next: move the current item to in_progress with update_task(expectedVersion), " +
                          "add findings with add_task_comment, and store durable decisions with save_knowledge.");
            return sb.ToString().TrimEnd();
        });

    private async Task<string> DescribeProjectsAsync(CancellationToken ct)
    {
        var projects = await _projects.ListAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine("# Work context");
        sb.AppendLine();
        sb.AppendLine("No repositoryId or projectId given. Pass one to get tasks and recent knowledge.");
        sb.AppendLine();
        sb.AppendLine($"## Projects ({projects.Count})");
        foreach (var project in projects.OrderBy(p => p.Name))
            sb.AppendLine($"- {project.Name ?? "(unnamed)"} id={project.Id} repos=[{string.Join(", ", project.RepositoryIds)}]");
        sb.AppendLine();
        sb.AppendLine("Tip: call repositories to find repositoryId values; a project is resolved automatically from a repository.");
        return sb.ToString().TrimEnd();
    }

    private static string StatusLabel(TaskState status)
        => status == TaskState.InProgress ? "in_progress" : status.ToString().ToLowerInvariant();

    private static string FormatTask(TaskItem task)
        => $"- [{StatusLabel(task.Status)}] p{(int)task.Priority} " +
           $"{task.Kind.ToString().ToLowerInvariant()} — {task.Title} " +
           $"(id={task.Id}, v={task.Version}, updated={task.UpdatedAt:yyyy-MM-dd})" +
           (string.IsNullOrWhiteSpace(task.Description) ? "" : $"\n  {Truncate(task.Description, 300)}");

    private static string FormatEntry(KnowledgeEntry entry)
        => $"- [{entry.Type.ToString().ToLowerInvariant()}] {entry.Title} " +
           $"(id={entry.Id}, v={entry.Version}, updated={entry.UpdatedAt:yyyy-MM-dd}" +
           (string.IsNullOrWhiteSpace(entry.SourceRef) ? ")" : $", source={entry.SourceRef})") +
           $"\n  {Truncate(entry.Content, 300)}";

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";

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
}
