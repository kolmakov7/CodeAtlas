# AI Documentation Generation — Task Plan

Goal: add LLM-based technical documentation generation to the RAG server,
built strictly on top of existing index/RAG data (symbols, relations, chunks,
source on disk), without breaking existing search/indexing.

Definition of done = all acceptance criteria from the task brief:
generate for class/interface/method/module/repository; RAG context auto-built;
LLM gets only needed context; Markdown saved + served via API + MCP + UI;
stale detection via content hashes; regenerate; background job w/ status;
LLM errors handled; tests pass; existing pipeline intact.

Language of generated docs: English (enforced by system prompt).

## Architecture notes (Phase 1 findings)

- .NET 10, MVC controllers, camelCase JSON, SQLite + Dapper, embedded SQL migrations
  (`src/CodeRag.Infrastructure/Persistence/Migrations/{Sqlite|Postgres}/Main/NNN_*.sql`).
- Hexagonal: Domain ports -> Infrastructure Dapper stores.
- Jobs: `JobRunner` (channel) + `IIndexingJobHandler` strategies in CodeRag.Indexer;
  API pattern = 202 + `{jobId}`; statuses Queued/Running/Completed/Failed.
- Settings: `settings` table -> IConfiguration (DB overrides env) + IOptionsMonitor hot reload;
  key format `Group:Key`; add Options class + DI Configure + SettingsDtos entry.
- ISearchService (CodeRag.Search): SearchCodeAsync / FindSymbolAsync / GetSymbolAsync /
  GetRelationsAsync / FindReferencesAsync / FindImplementationsAsync / FindCallersAsync /
  FindCalleesAsync / GetCodeAsync (source from disk/git checkout, fallback to chunks).
- IRelationStore.GetBySymbolAsync / QueryEndpointsAsync; RelationType:
  Calls, Inherits, Implements, References, Contains, Imports, Overrides, Uses, DependsOn.
- Hashes: FileEntry.Hash (per file), Chunk.ContentHash (per chunk) -> reuse for staleness.
- MCP: ModelContextProtocol SDK, assembly scan, `[McpServerToolType]` classes in
  CodeRag.Mcp/Tools + snake_case alias + TrackAsync.
- Web: React 18 + TS + Vite + react-query + axios; pages/src/pages, hooks in src/api/hooks.ts,
  types in src/api/types.ts, nav in components/Layout.tsx, routes in App.tsx.
- No LLM chat client exists yet -> first one (OpenAI-compatible, mirrors
  OpenAICompatibleEmbeddingProvider style: endpoint/model/key/timeout/retry).

## Checklist

### Phase 1 — Investigate architecture, define integration points
- [x] Study project structure, API, indexing, storage, embeddings, search
- [x] Study symbols/relations, ID formats, background jobs, MCP, UI, settings
- [x] Check toolchain (dotnet 10, node 20) and solution file
- [x] Create this TODO plan

### Phase 2 — Core engine (context builder, generator, model/storage)
- [x] Domain: `Documentation` entity + `DocumentationStatus` enum + ports
      (`IDocumentationStore`, `ITargetContentHasher`)
- [x] New `CodeRag.Llm` project: `LlmOptions` (Endpoint/Model/Temperature/MaxTokens/
      Timeout/MaxRetries/ApiKey/ProxyUrl), `IChatCompletionClient`,
      OpenAI-compatible implementation (POST {endpoint}/chat/completions)
- [x] New `CodeRag.Documentation` project:
      - `DocumentationTarget` (type: repository|module|symbol + id + name/fqn/file)
      - `DocumentationContextBuilder` (symbol/module/repository contexts; related symbols,
        dependencies, calls/references, tests + config via search; dedupe; token budget;
        structured text blocks TARGET/TYPE/FILE/SOURCE/RELATED/DEPENDENCIES/CALLS/...)
      - Versioned English prompt (`DocumentationPrompts`, v1) with anti-hallucination rules
      - `IDocumentationGenerator` + `LlmDocumentationGenerator` (IChatCompletionClient)
      - Post-processing/validation (`DocumentationValidator`: non-empty, Markdown sanity)
      - `DocumentationService` (build context -> generate -> validate -> save;
        stale check via source hash; regenerate; repo batch + progress)
- [x] Infrastructure: Dapper `DocumentationStore` + table migration
      `002_documentation.sql` (SQLite + Postgres)
- [x] DI wiring + add both projects to solution (both register as singletons)

### Phase 3 — Generation API
- [x] `DocumentationController`: repo generate (202+jobId), single-target generate (inline),
      list for repo, get by id, get by target, delete
- [x] DTOs in `Api/Models/DocumentationDtos.cs`
- [x] Wire `AddLlm()` + `AddDocumentation()` in `Program.cs` + add project refs

### Phase 4 — Background generation
- [x] `JobType.Documentation` (=6) + `DocumentationJobHandler` in CodeRag.Indexer
      (runs DocumentationService.GenerateForRepositoryAsync, throttled job progress)
- [x] `TargetContentHasher` (source + one-hop direct dependencies, from index metadata)
- [ ] Stale detection hook: after index job, recompute hashes -> mark stale (optional v2)

### Phase 5 — Incremental / stale detection (verification)
- [x] Same source hash -> skip (return existing); changed -> regenerate
- [x] Dependency change propagates staleness (direct deps v1, via hasher one-hop)

### Phase 6 — UI
- [x] React: documentation page (list + filter by type, generate all / single target,
      view Markdown detail, delete, running-job progress bar) + hooks + types —
      `CodeRag.Web/src/pages/Documentation.tsx`, `api/hooks.ts`, `api/types.ts`
- [x] Wire entry points: nav link (Layout.tsx), route (App.tsx), shortcut from
      RepositoryDashboard (`/documentation?repositoryId=...`)
- [x] Web typecheck (`tsc -b`) + `npm run build` pass

### Phase 7 — MCP tools
- [x] `generate_documentation`, `get_documentation`, `get_documentation_status`
      (snake_case aliases, TrackAsync usage recording) — `CodeRag.Mcp/Tools/DocumentationTools.cs`,
      registered in Program.cs

### Phase 8 — Tests + docs
- [x] Context builder tests (symbol + repository paths; relations categorization)
      -> `tests/CodeRag.Tests/Unit/DocumentationContextBuilderTests.cs`
- [x] Relations-in-context test (one-hop, both directions, categorized; unresolvable symbol warning)
- [x] Mock LLM test (context -> prompt -> markdown; unconfigured throws; non-markdown fallback)
      -> `LlmDocumentationGeneratorTests.cs`
- [x] Storage tests (SQLite round-trip, stable-identity upsert, GetByRepositoryAndType filter,
      MarkStaleByRepository semantics, DeleteAsync) -> `DocumentationStoreTests.cs`
- [x] Incremental tests (fresh skip, stale regenerate, force) -> `DocumentationServiceTests.cs`
- [x] Entity tests (staleness vs hash, Mark* transitions, UpdateContent, Create invariants)
      -> `DocumentationEntityTests.cs`
- [x] Target content hasher tests (deterministic, target/dependency change, null when no source, repo)
      -> `TargetContentHasherTests.cs`
- [x] Prompt/target tests (English + type sections + anti-hallucination, user prompt,
      BuildTargetRef, NormalizePath) + validator tests (fence/preamble/sign-off/blank lines)
      -> `DocumentationPromptsAndTargetTests.cs`, `DocumentationValidatorTests.cs`
- [ ] API tests (start + fetch) — not added (would need the web/webhost test client harness);
      covered indirectly by controller DTOs + full build
- [x] Run full solution build + existing test suite; no regressions
      (build clean; `dotnet test` => 0 failed / 350 passed / 1 pre-existing skip)

### Phase 9 — Docs
- [x] README settings table: added **LLM (docs)** group with the `Llm:*` keys
      (Endpoint/Model/Temperature/MaxTokens/TimeoutSeconds/MaxRetries/BaseDelayMs/
      MaxContextChars/ApiKey/ProxyUrl). Note: context-size knobs are per-request
      (`DocumentationBuildOptions`, defaults) — not settings-table keys, so no `Documentation`
      group was added.

## Log
- Phase 1 complete: two explore reports (API/storage/jobs; search/relations/MCP/web).
  Decision: new projects CodeRag.Llm + CodeRag.Documentation; job type for background;
  reuse FileEntry.Hash/Chunk.ContentHash + direct-dep symbol hashes for staleness.
- Phase 8 complete: 8 unit test files written under `tests/CodeRag.Tests/Unit/`. Full suite
  green (0 failed / 350 passed / 1 pre-existing skip). Writing the tests surfaced 3 fixes:
  - Extracted `IDocumentationContextBuilder` (interface) so `DocumentationService` is unit-testable
    with NSubstitute (the concrete builder is `sealed`, which Castle can't proxy). Updated
    `DocumentationService` ctor + `ServiceCollectionExtensions` registration.
  - `DocumentationValidator.StripTrailingSignOff`: old guard `i < lines.Length - 1` never fired
    (sign-off is always the final non-empty line after trim), so sign-offs were never stripped.
    Rewrote to locate the last non-empty line and strip it when it matches a meta marker.
  - `DocumentationTarget.NormalizePath`: now drops a leading `./` / `.` segment
    ("  ./src  " -> "src").
- Phase 9 (pending): README settings table for Llm/Documentation config groups.
