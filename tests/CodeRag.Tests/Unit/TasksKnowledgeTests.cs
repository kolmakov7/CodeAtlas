using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Stores;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Knowledge;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using CodeRag.Llm;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeRag.Tests.Unit;

public sealed class TasksKnowledgeTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string? _previousDataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly TaskService _tasks;
    private readonly KnowledgeService _knowledge;
    private readonly RevisionService _revision;
    private readonly ConsolidationService _consolidation;
    private readonly IChatCompletionClient _llm;
    private readonly Guid _repositoryId;

    public TasksKnowledgeTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(Path.Combine(_dataDir, "repos", "checkout"));
        _previousDataDir = Environment.GetEnvironmentVariable("CODERAG__DataDir");
        Environment.SetEnvironmentVariable("CODERAG__DataDir", _dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"),
            NullLogger<SqliteConnectionFactory>.Instance);
        new DbInitializer(_factory, NullLogger<DbInitializer>.Instance).InitializeAsync().GetAwaiter().GetResult();

        var repoStore = new RepositoryStore(_factory);
        var projectStore = new ProjectStore(_factory);
        var taskStore = new TaskStore(_factory);
        var linkStore = new EntityLinkStore(_factory);
        var historyStore = new EntityHistoryStore(_factory);
        var knowledgeStore = new KnowledgeStore(_factory);
        var jobStore = new ConsolidationStore(_factory);
        var runStore = new RevisionRunStore(_factory);
        var projects = new ProjectService(projectStore, repoStore);

        _tasks = new TaskService(taskStore, projectStore, repoStore, linkStore, historyStore, projects);

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.Model.Returns("test-embed");
        embeddingProvider.ConfigHash.Returns("cfg");
        embeddingProvider.Dimensions.Returns(4);
        embeddingProvider.ContextLength.Returns(512);
        embeddingProvider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<float[][]>>(_ => throw new InvalidOperationException("embeddings unavailable"));
        var providerFactory = Substitute.For<IEmbeddingProviderFactory>();
        providerFactory.Text.Returns(embeddingProvider);
        var vectorStoreFactory = Substitute.For<IVectorStoreFactory>();
        vectorStoreFactory.Knowledge.Returns(Substitute.For<IVectorStore>());
        var options = Substitute.For<IOptionsMonitor<KnowledgeOptions>>();
        options.CurrentValue.Returns(new KnowledgeOptions
        {
            Revision = new RevisionOptions { SimilarityThreshold = 0.8, MaxEntriesPerRun = 100, StaleDays = 3650 },
            Embeddings = new KnowledgeEmbeddingOptions()
        });
        var embedding = new KnowledgeEmbeddingService(providerFactory, vectorStoreFactory, knowledgeStore, options,
            NullLogger<KnowledgeEmbeddingService>.Instance);
        var search = new KnowledgeSearchService(knowledgeStore, providerFactory, vectorStoreFactory,
            NullLogger<KnowledgeSearchService>.Instance);
        _knowledge = new KnowledgeService(knowledgeStore, taskStore, projectStore, repoStore, linkStore, historyStore,
            projects, embedding, search);

        _llm = Substitute.For<IChatCompletionClient>();
        _llm.IsConfigured.Returns(true);
        var llmOptions = Substitute.For<IOptionsMonitor<LlmOptions>>();
        llmOptions.CurrentValue.Returns(new LlmOptions());
        _revision = new RevisionService(knowledgeStore, jobStore, linkStore, runStore, options,
            NullLogger<RevisionService>.Instance);
        _consolidation = new ConsolidationService(jobStore, knowledgeStore, embedding, _llm, options, llmOptions,
            NullLogger<ConsolidationService>.Instance);

        _repositoryId = SeedRepositoryAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODERAG__DataDir", _previousDataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<Guid> SeedRepositoryAsync()
    {
        var id = Guid.NewGuid();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,'file:///x',@p,'active');",
            new { i = id.ToString(), n = "repo-" + id.ToString("N")[..6], p = Path.Combine(_dataDir, "repos", "checkout") });
        return id;
    }

    [Fact]
    public async Task Create_task_auto_creates_project_and_is_idempotent()
    {
        var first = await _tasks.CreateAsync(new CreateTaskRequest(
            "Implement feature", "details", RepositoryId: _repositoryId, ExternalKey: "agent:1"));
        first.Created.Should().BeTrue();
        first.Task.ProjectId.Should().NotBeNull();

        var second = await _tasks.CreateAsync(new CreateTaskRequest(
            "Implement feature", "details", RepositoryId: _repositoryId, ExternalKey: "agent:1"));
        second.Created.Should().BeFalse();
        second.Task.Id.Should().Be(first.Task.Id);
    }

    [Fact]
    public async Task Concurrent_update_with_stale_version_throws_conflict()
    {
        var created = await _tasks.CreateAsync(new CreateTaskRequest("Concurrent task"));
        var version = created.Task.Version;

        var updated = await _tasks.UpdateAsync(created.Task.Id,
            new UpdateTaskRequest(version, Status: TaskState.InProgress));
        updated.Task.Version.Should().Be(version + 1);

        var act = () => _tasks.UpdateAsync(created.Task.Id,
            new UpdateTaskRequest(version, Title: "stale write"));
        await act.Should().ThrowAsync<VersionConflictException>();
    }

    [Fact]
    public async Task Dependency_cycle_is_rejected()
    {
        var a = await _tasks.CreateAsync(new CreateTaskRequest("A"));
        var b = await _tasks.CreateAsync(new CreateTaskRequest("B"));

        await _tasks.AddDependencyAsync(a.Task.Id, b.Task.Id);
        var act = () => _tasks.AddDependencyAsync(b.Task.Id, a.Task.Id);
        await act.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task Todo_import_is_idempotent_and_export_preserves_statuses()
    {
        var todoPath = Path.Combine(_dataDir, "repos", "checkout", "TODO_demo.md");
        await File.WriteAllTextAsync(todoPath, """
            # Demo

            - [ ] first item
            - [x] second item
            - [~] third item
            """);

        var first = await _tasks.ImportTodosAsync(_repositoryId, "TODO_demo.md");
        first.Created.Should().Be(3);
        var second = await _tasks.ImportTodosAsync(_repositoryId, "TODO_demo.md");
        second.Created.Should().Be(0);
        second.Skipped.Should().Be(3);

        var exported = await _tasks.ExportAsync(null, _repositoryId);
        exported.Count.Should().Be(3);
        exported.Content.Should().Contain("- [ ] first item");
        exported.Content.Should().Contain("- [x] second item");
        exported.Content.Should().Contain("- [~] third item");
    }

    [Fact]
    public async Task Knowledge_search_respects_project_and_global_isolation()
    {
        var projectTask = await _tasks.CreateAsync(new CreateTaskRequest(
            "Scope task", RepositoryId: _repositoryId));
        var projectId = projectTask.Task.ProjectId!.Value;

        await _knowledge.SaveAsync(new SaveKnowledgeRequest(
            "Project database fact", "The project database uses PostgreSQL.",
            KnowledgeType.Fact, projectId, _repositoryId, ExternalKey: "k-project"));
        await _knowledge.SaveAsync(new SaveKnowledgeRequest(
            "Global database fact", "The shared database uses SQLite.",
            KnowledgeType.Fact, ProjectId: null, ExternalKey: "k-global"));

        var projectHits = await _knowledge.SearchAsync(new KnowledgeSearchRequest(
            "database", "lexical", ProjectId: projectId));
        projectHits.Hits.Should().ContainSingle();
        projectHits.Hits[0].Entry.ExternalKey.Should().Be("k-project");

        var globalHits = await _knowledge.SearchAsync(new KnowledgeSearchRequest(
            "database", "lexical", GlobalOnly: true));
        globalHits.Hits.Should().ContainSingle();
        globalHits.Hits[0].Entry.ExternalKey.Should().Be("k-global");
    }

    [Fact]
    public async Task Revision_is_idempotent_and_accept_undo_preserves_sources()
    {
        var task = await _tasks.CreateAsync(new CreateTaskRequest("Consolidation scope", RepositoryId: _repositoryId));
        var projectId = task.Task.ProjectId!.Value;
        var first = await _knowledge.SaveAsync(new SaveKnowledgeRequest(
            "Database decision", "We use PostgreSQL for the primary database.",
            KnowledgeType.Decision, projectId, _repositoryId, SourceRef: "ADR-1", ExternalKey: "dup-1"));
        var second = await _knowledge.SaveAsync(new SaveKnowledgeRequest(
            "Database decision", "We use PostgreSQL for the primary database.",
            KnowledgeType.Decision, projectId, _repositoryId, SourceRef: "ADR-2", ExternalKey: "dup-2"));

        var run1 = await _revision.RunAsync(projectId: projectId);
        run1.JobsCreated.Should().BeGreaterThan(0);
        var run2 = await _revision.RunAsync(projectId: projectId);
        run2.JobsCreated.Should().Be(0);
        run2.JobsSkipped.Should().BeGreaterThan(0);

        var jobId = run1.JobIds.First();
        _llm.CompleteAsync(Arg.Any<ChatCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatCompletionResult(
                """{"title":"Primary database","content":"PostgreSQL is the primary database.","type":"decision","rationale":"same decision","confidence":0.9,"contradictions":[]}""",
                "fake"));

        var analysis = await _consolidation.AnalyzeAsync(jobId);
        analysis.Status.Should().Be("ReadyForReview");

        var accepted = await _consolidation.AcceptAsync(jobId, 1, "test");
        accepted.Success.Should().BeTrue();
        accepted.ResultKnowledgeId.Should().NotBeNull();

        var merged = await _knowledge.GetAsync(accepted.ResultKnowledgeId!.Value);
        merged.Status.Should().Be(KnowledgeStatus.Active);
        (await _knowledge.GetAsync(first.Entry.Id)).Status.Should().Be(KnowledgeStatus.Superseded);
        (await _knowledge.GetAsync(second.Entry.Id)).Status.Should().Be(KnowledgeStatus.Superseded);

        var undone = await _consolidation.UndoAsync(jobId, "test");
        undone.Success.Should().BeTrue();
        (await _knowledge.GetAsync(first.Entry.Id)).Status.Should().Be(KnowledgeStatus.Active);
        (await _knowledge.GetAsync(second.Entry.Id)).Status.Should().Be(KnowledgeStatus.Active);
        (await _knowledge.GetAsync(merged.Id)).Status.Should().Be(KnowledgeStatus.Archived);
    }

    [Fact]
    public async Task Revision_waits_for_llm_without_losing_the_job()
    {
        var task = await _tasks.CreateAsync(new CreateTaskRequest("LLM scope", RepositoryId: _repositoryId));
        var projectId = task.Task.ProjectId!.Value;
        await _knowledge.SaveAsync(new SaveKnowledgeRequest("Same", "Same body",
            KnowledgeType.Fact, projectId, _repositoryId, ExternalKey: "llm-1"));
        await _knowledge.SaveAsync(new SaveKnowledgeRequest("Same", "Same body",
            KnowledgeType.Fact, projectId, _repositoryId, ExternalKey: "llm-2"));

        var run = await _revision.RunAsync(projectId: projectId);
        var jobId = run.JobIds.First();
        _llm.IsConfigured.Returns(false);

        var analysis = await _consolidation.AnalyzeAsync(jobId);
        analysis.Status.Should().Be("AwaitingLlm");
        analysis.LlmConfigured.Should().BeFalse();

        var detail = await _consolidation.GetDetailAsync(jobId);
        detail.Job.Status.Should().Be(ConsolidationJobStatus.AwaitingLlm);
        detail.Items.Should().HaveCount(2);
    }
}
