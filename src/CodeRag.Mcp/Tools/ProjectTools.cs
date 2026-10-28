using System.ComponentModel;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using CodeRag.Knowledge.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public sealed class ProjectTools
{
    private readonly ProjectService _projects;
    private readonly IMcpUsageRecorder? _usage;
    private readonly ILogger<ProjectTools> _logger;

    public ProjectTools(ProjectService projects, ILogger<ProjectTools> logger, IMcpUsageRecorder? usage = null)
    {
        _projects = projects;
        _logger = logger;
        _usage = usage;
    }

    private async Task<string> TrackAsync(string tool, Func<Task<string>> execute)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result;
        try { result = await execute().ConfigureAwait(false); }
        catch (Exception ex) { result = $"Error: {ex.Message}"; }
        sw.Stop();
        _usage?.Record(tool, !result.StartsWith("Error:", StringComparison.Ordinal), (int)sw.ElapsedMilliseconds, null);
        return result;
    }

    [McpServerTool, Description("Create a project that groups tasks and knowledge. repositoryIds may be empty: use an empty project for topics that have no indexed repository yet. Pass the returned projectId to create_task and save_knowledge.")]
    public Task<string> CreateProjectAsync(
        [Description("Project name, e.g. the product or topic name")] string name,
        [Description("Optional comma-separated repository GUIDs to link")] string? repositoryIds = null,
        [Description("Optional path hint")] string? path = null,
        [Description("Optional primary language hint")] string? language = null,
        CancellationToken ct = default)
        => TrackAsync("create_project", async () =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Error: project name is required.";
            var ids = ParseGuids(repositoryIds);
            var project = await _projects.CreateAsync(name.Trim(), ids, path, language, ct).ConfigureAwait(false);
            return $"Project {project.Id} created: {project.Name ?? "(unnamed)"} repos=[{string.Join(", ", project.RepositoryIds)}]\n" +
                   "Use this projectId as the scope for create_task/save_knowledge.";
        });

    [McpServerTool, Description("List projects with their linked repositories. Use it to find the projectId for the current work; create a new project when no project matches.")]
    public Task<string> ListProjectsAsync(CancellationToken ct = default)
        => TrackAsync("list_projects", async () =>
        {
            var projects = await _projects.ListAsync(ct).ConfigureAwait(false);
            if (projects.Count == 0)
                return "No projects found. Create one with create_project (repositoryIds may be empty).";
            return string.Join("\n", projects.Select(p =>
                $"- {p.Id} {p.Name ?? "(unnamed)"} repos=[{string.Join(", ", p.RepositoryIds)}]"));
        });

    private static IReadOnlyList<Guid> ParseGuids(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Guid>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }
}
