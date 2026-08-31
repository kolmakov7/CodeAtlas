using CodeRag.Domain.Enums;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Search;
using CodeRag.Search.Models;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Regression tests (eShop MCP verification, point 4 + point 3):
/// 1) get_relations type filter must accept agent-style tokens (lowercase, snake_case,
///    aliases such as refs) and map them onto the canonical relation_type values;
///    before the fix "implements"/"depends_on" silently matched nothing (exact SQL match).
/// 2) every symbol kind emitted by the analyzers (including C# Record and Event) must map
///    onto Domain.Enums.SymbolKind; before the fix records were persisted as Unknown
///    (81/3676 symbols in eShop), breaking get_symbol identity and find_symbol ordering.
/// </summary>
public sealed class SearchRelationAndSymbolKindTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly Guid _repo;
    private readonly Guid _file;
    private readonly Guid _symA;
    private readonly Guid _symB;
    private readonly Guid _symC;
    private readonly Guid _symD;
    private readonly Guid _symE;
    private readonly Guid _symF;

    public SearchRelationAndSymbolKindTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-relkind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);

        _repo = Guid.NewGuid();
        _file = Guid.NewGuid();
        _symA = Guid.NewGuid();
        _symB = Guid.NewGuid();
        _symC = Guid.NewGuid();
        _symD = Guid.NewGuid();
        _symE = Guid.NewGuid();
        _symF = Guid.NewGuid();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<SearchService> InitializeAsync()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s)",
            new { i = _repo.ToString(), n = "unit", g = "file:///x", p = "/x", s = "ready" });
        await conn.ExecuteAsync(
            "INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
            new { i = _file.ToString(), r = _repo.ToString(), p = "src/A.cs", l = "csharp" });
        var syms = new (Guid Id, string Name, string Kind)[]
        {
            (_symA, "A", "Class"),
            (_symB, "IA", "Interface"),
            (_symC, "Dep", "Class"),
            (_symD, "Member", "Method"),
            (_symE, "Base", "Class"),
            (_symF, "Callee", "Method"),
        };
        foreach (var (id, name, kind) in syms)
        {
            await conn.ExecuteAsync(
                "INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn) VALUES(@i,@r,@f,'csharp',@k,@n,@fqn)",
                new { i = id.ToString(), r = _repo.ToString(), f = _file.ToString(), k = kind, n = name, fqn = "global::" + name });
        }
        var relations = new (string From, string To, string Type)[]
        {
            (_symA.ToString(), _symB.ToString(), "Implements"),
            (_symA.ToString(), _symC.ToString(), "DependsOn"),
            (_symA.ToString(), _symD.ToString(), "Contains"),
            (_symA.ToString(), _symE.ToString(), "Inherits"),
            (_symA.ToString(), _symF.ToString(), "Calls"),
        };
        foreach (var (from, to, type) in relations)
        {
            await conn.ExecuteAsync(
                "INSERT INTO relations(from_symbol_id,to_symbol_id,relation_type) VALUES(@f,@t,@t2)",
                new { f = from, t = to, t2 = type });
        }

        var vectorFactory = Substitute.For<IVectorStoreFactory>();
        var embedFactory = Substitute.For<IEmbeddingProviderFactory>();
        var monitor = Substitute.For<IOptionsMonitor<SearchOptions>>();
        monitor.CurrentValue.Returns(new SearchOptions());
        return new SearchService(
            _factory,
            new FtsHelper(_factory),
            vectorFactory,
            embedFactory,
            NullLogger<SearchService>.Instance,
            monitor);
    }

    private static string? ToSymbolName(RelationEntry r) => r.ToSymbol?.Name;

    [Fact]
    public async Task GetRelations_lowercase_type_tokens_should_match_canonical_types()
    {
        var service = await InitializeAsync();

        var implements = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "implements" });
        implements.Should().HaveCount(1);
        ToSymbolName(implements[0]).Should().Be("IA");

        var depends = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "depends_on" });
        depends.Should().HaveCount(1);
        ToSymbolName(depends[0]).Should().Be("Dep");

        var contains = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "contains" });
        contains.Should().HaveCount(1);
        ToSymbolName(contains[0]).Should().Be("Member");

        var inherits = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "inherits" });
        inherits.Should().HaveCount(1);
        ToSymbolName(inherits[0]).Should().Be("Base");

        var calls = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "calls" });
        calls.Should().HaveCount(1);
        ToSymbolName(calls[0]).Should().Be("Callee");
    }

    [Fact]
    public async Task GetRelations_mixed_case_and_canonical_tokens_should_still_work()
    {
        var service = await InitializeAsync();

        var canonical = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "Implements" });
        canonical.Should().HaveCount(1);

        var mixed = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "cAllS", "IMPLEMENTS" });
        mixed.Should().HaveCount(2);
        mixed.Select(ToSymbolName).Should().BeEquivalentTo("Callee", "IA");

        var incoming = await service.GetRelationsAsync(_symA, RelationDirection.Incoming, new[] { "contains" });
        incoming.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRelations_refs_alias_should_be_accepted_and_return_known_rows_only()
    {
        var service = await InitializeAsync();

        var refs = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "refs" });
        refs.Should().BeEmpty();

        var all = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, null);
        all.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetRelations_unknown_type_token_should_pass_through_without_error()
    {
        var service = await InitializeAsync();

        var custom = await service.GetRelationsAsync(_symA, RelationDirection.Outgoing, new[] { "References" });
        custom.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeRelationType_should_map_agent_spellings_to_canonical_names()
    {
        SearchService.NormalizeRelationType("implements").Should().Be("Implements");
        SearchService.NormalizeRelationType("IMPLEMENTS").Should().Be("Implements");
        SearchService.NormalizeRelationType("depends_on").Should().Be("DependsOn");
        SearchService.NormalizeRelationType("Depends-On").Should().Be("DependsOn");
        SearchService.NormalizeRelationType("contains").Should().Be("Contains");
        SearchService.NormalizeRelationType("inherits").Should().Be("Inherits");
        SearchService.NormalizeRelationType("calls").Should().Be("Calls");
        SearchService.NormalizeRelationType("overrides").Should().Be("Overrides");
        SearchService.NormalizeRelationType("refs").Should().Be("References");
        SearchService.NormalizeRelationType("references").Should().Be("References");
        SearchService.NormalizeRelationType("  uses ").Should().Be("Uses");
        SearchService.NormalizeRelationType("imports").Should().Be("Imports");
        SearchService.NormalizeRelationType("UnknownType").Should().Be("UnknownType");
        SearchService.NormalizeRelationType(null).Should().BeNull();
        SearchService.NormalizeRelationType("  ").Should().BeNull();
    }

    [Fact]
    public void SymbolKind_should_contain_record_and_event()
    {
        Enum.IsDefined(typeof(SymbolKind), nameof(SymbolKind.Record)).Should().BeTrue();
        Enum.IsDefined(typeof(SymbolKind), nameof(SymbolKind.Event)).Should().BeTrue();
        Enum.Parse<SymbolKind>("Record").Should().NotBe(SymbolKind.Unknown);
    }

    [Fact]
    public void All_analyzer_emitted_kinds_should_map_onto_domain_SymbolKind()
    {
        // Kinds the C# Roslyn analyzer emits (CodeRag.Analyzers.Abstractions.Symbol constants);
        // before the fix "Record" (and "Event") failed Enum.TryParse and were stored as Unknown.
        var analyzerKinds = new[]
        {
            "Namespace", "Class", "Interface", "Record", "Struct", "Enum", "EnumMember",
            "Method", "Constructor", "Function", "Property", "Field", "Event",
        };
        foreach (var kind in analyzerKinds)
        {
            var parsed = Enum.TryParse<SymbolKind>(kind, true, out var value);
            parsed.Should().BeTrue($"analyzer kind '{kind}' must be representable in Domain.Enums.SymbolKind");
            value.Should().NotBe(SymbolKind.Unknown);
        }
    }
}
