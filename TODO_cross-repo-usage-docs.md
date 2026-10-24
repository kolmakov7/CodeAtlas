# Cross-repo relations, usage statistics & cost-bounded documentation

Separate task plan (requested after the doc-gen fix work). Source of truth for this
initiative. All items start as `[ ]` (not started); one item at a time becomes `[~]`.

## Why (three problems observed in practice)

1. **Full documentation generation is too heavy.** eva-db alone has ~12 400 documentable
   objects (7 930 procedures, 1 799 tables, 1 440 functions, 1 232 views); at ~20–90 s per
   LLM call a full run is days. We need to generate *selectively* and *incrementally*.
2. **No usage statistics.** The logical selection rule is "document the objects whose data
   is actually requested/used", but we persist no per-object usage. This must be built.
3. **No cross-repository relations.** Generated docs claim "no direct callers" when callers
   exist in a *neighbouring* repository (e.g. app repo `bcs_eva_app` calling SQL objects in
   `eva-db`). Relations are strictly per-repository today. Cross-repo links are needed, and
   declaring which project relates to which requires **human-in-the-loop** input.

## Definition of done (explicit, per the request)

The task is complete only when **I have verified myself** that all three problems are
resolved and **the UI is not broken**:

- [x] Usage statistics exist, are persisted, exposed via API and visible in the UI.
- [x] Cross-repository relations exist; declaring a repo↔repo link is possible in the UI;
      cross-repo callers appear in documentation context and in the UI.
- [x] Documentation generation is cost-bounded/usage-prioritised: a run targets a bounded,
      high-value set (configurable budget), not "everything".
- [x] **Automated UI tests** cover each of the above and a full-app smoke test proves the UI
      still works. "It builds" is NOT sufficient. (14 Playwright tests, all green.)
- [x] Backend unit tests + full solution build + web typecheck/build green
      (369 passed / 1 skip; web `tsc -b` + `vite build` green).

## Key findings (current state — verified)

- **Relations**: `relations(from_symbol_id, to_symbol_id, relation_type, repository_id)`,
  PK `(from,to,type)`; `repository_id` is a single nullable non-FK tag. `RelationType` has 9
  domain values; analyzers emit up to 13 and unresolved ones are **silently dropped** on
  persist (`FileIndexer`, `RepositoryRelationService`). Extractors are per-repo and load
  `ISymbolCatalog.GetSymbolsAsync(context.RepositoryId, …)` — a target can never resolve to
  another repo. Files: `src/CodeRag.Infrastructure/Stores/RelationStore.cs`,
  `src/CodeRag.Indexer/Services/Indexing/RepositoryRelationService.cs`,
  `src/CodeRag.Indexer/Services/Handlers/RelationsJobHandler.cs`,
  `src/CodeRag.Analyzers.{CSharp,Sql,TreeSitter}`.
- **Usage**: only `mcp_tool_usage(at, tool, status, duration_ms, repository_id)` via
  `IMcpUsageRecorder` / `GET /api/statistics/mcp`. **No symbol/file/chunk/query-level usage.**
  `repository_stats`/`global_stats` are index counts, not usage.
- **Repo model**: `Repository` + `RepositoryGroup` (proxy/credentials only). **No link concept.**
- **Doc prioritisation**: `DocumentationService.GenerateForRepositoryAsync` = dedupe by target
  key → `KindRank` → `Fqn`, then `Take(maxSymbols)`. `DocumentationContextBuilder` uses
  incoming-relation degree only. Job options `{force, maxSymbols}` via
  `indexing_jobs.options` (migration 003).
- **UI**: React + Vite, routes in `src/CodeRag.Web/src/App.tsx`, pages in `src/pages/`.
  Production SPA served by the API from `wwwroot`; dev on :5173 with `/api` proxy → :8080;
  docker maps host :8085. `SearchRequest.repositoryIds` already supports multiple repos
  end-to-end (unused by UI).
- **UI tests: none exist.** No `playwright.config.*`, no e2e specs, no `tests/e2e`; scripts
  `test`/`test:e2e` point at **uninstalled** binaries; no `data-testid` anywhere. CI has a
  commented Playwright block expecting `tests/e2e`.

## Open decisions (need confirmation before/while implementing)

- [ ] Relation storage for cross-repo: preferred shape is to stop relying on the single
      `repository_id` tag and add explicit `from_repository_id` / `to_repository_id`
      (backfilled), plus `origin_repository_id`. Confirm migration/back-compat approach.
- [ ] Repo-link model & semantics: directed vs undirected, 1:N, and whether links also carry
      an "expected target language" (e.g. app → SQL) to guide matching.
- [ ] Usage scoring formula + default budget (how many docs per run, and the threshold).
- [ ] Where the human declares links: Repositories page, RepositoryDashboard, or a new
      "Integrations / Links" settings tab. Auto-suggest candidates (shared FQNs) or manual only?
- [ ] Whether runtime query logs (e.g. SQL Server DMVs) are ever an input, or usage = index
      graph + CodeAtlas queries only.

## Phases

### Phase 0 — Design & decisions
- [x] Resolve the open decisions above; write a short design note in `docs/`.
- [x] Enumerate the human-in-the-loop UX for declaring links (and linking suggestions).
- [x] Define the usage-score and the doc-generation budget/knobs.
      → `docs/cross-repo-usage-docs-design.md`

### Phase 1 — UI test infrastructure (do first; verification is mandatory)
- [x] Add `@playwright/test` (+ `@types/node`); `playwright.config.ts` (testDir `tests/e2e`,
      webServer for API + Vite dev, baseURL).
- [x] E2E target: built-from-source API with a seeded SQLite fixture (`CODERAG__E2E_SEED=1`,
      throwaway data dir) — no real index.
- [x] Seed two tiny repos (`E2eDataSeeder`): app + db, 3 symbols.
- [x] `data-testid` on nav (smoke uses roles/text elsewhere).
- [x] Full-app smoke test: all 7 routes render, no 404 fallback, no page errors; seeded repos listed.
- [x] CI: real `ui` job added to `.github/workflows/ci.yml` (build API, install Playwright, run e2e).
- [x] Removed stale `vite.config.js`/`orval.config.js` (shadowed the .ts config) + gitignore.
- [ ] (Optional) Vitest + Testing Library for component-level tests.

> Note: port 8080 is taken locally by `llama-swap`; e2e uses API :8091 + Vite :5173 with
> `VITE_API_TARGET`.

### Phase 2 — Usage statistics (problem 2)
- [x] Migration `006_symbol_usage.sql` (SQLite + Postgres): per-object usage counters.
- [x] `IUsageRecorder` + Dapper impl (non-blocking), `IUsageQueryService` + Dapper impl.
- [x] Record from `search`/`find_symbol`/`get_symbol`.
- [x] Cross-repo usage hook — relations feed the context; usage derived from lookups + refs.
- [x] API `GET /api/repositories/{id}/usage` + `GET /api/usage/top`.
- [x] Unit tests (`UsageStatsTests`).
- [ ] UI usage panel (Phase 6).

### Phase 3 — Repository links (problem 3, human-in-the-loop)
- [x] Migration `005_repository_links.sql` + entity/port/store + DI.
- [x] API `GET/POST/DELETE /api/repository-links` (+ `?repositoryId=`).
- [x] Tests (`RepositoryLinkStoreTests`).
- [ ] Suggestion helper (overlapping FQNs) — deferred.
- [ ] UI: manage links (Phase 6).
- [ ] UI test: create/delete link (Phase 7).

### Phase 4 — Cross-repo relation extraction (problem 3, mechanism)
- [x] Relations storage: `from_repository_id`/`to_repository_id`/`origin_repository_id`
      (migration 004), endpoint-aware stats/delete, origin-scoped cleanup.
- [x] Multi-repo `ISymbolCatalog` overload (default impl keeps test doubles working).
- [x] `RelationExtractionContext.LinkedRepositoryIds`; SQL extractor resolves linked repos.
- [x] Cross-repo text reference extractor in `RepositoryRelationService` (name-based,
      language-agnostic; maps matches to the enclosing local symbol; Calls/References).
- [x] Context builder tags cross-repo related symbols with `[repo: X]`.
- [x] Ownership/cleanup test (`DeleteCrossRepoByOriginAsync`).
- [ ] Prompt/validator wording pass (Phase 6/7).
- [ ] Re-index hook when a link changes — deferred.

### Phase 5 — Cost-bounded / usage-driven documentation generation (problem 1)
- [x] Usage-ranked selection in `GenerateForRepositoryAsync` (`strategy`, `minUsage`, `maxSymbols`).
- [x] Job options extended: `{force, maxSymbols, strategy, minUsage}`; controller + MCP pass through.
- [x] Guardrail: budget respected; dedupe before cap.
- [ ] Incremental `changedOnly` scheduling — deferred (hash staleness already skips unchanged).
- [x] UI budget/strategy controls (Phase 6): Max targets, "Prioritise by usage"/"By type",
      "Only used objects" (Documentation page).
- [x] UI test: budget respected (Phase 7).

### Phase 6 — Surface it all in the UI
- [x] RepositoryLinks management UI — `RepositoryLinksCard` on RepositoryDashboard
      (target repo + type select, add, list, delete).
- [x] Usage statistics UI — `RepositoryUsageCard` (top used objects, count, last used).
- [x] Documentation redesign: usage-based prioritisation + budget controls + "Only used".
- [x] Navigation/entry points consistent with existing Layout/Dashboard patterns.
- [ ] Doc prompt wording pass ("callers incl. linked repos") — cross-repo callers are now
      tagged `[repo: X]` in context; explicit prompt wording deferred.

### Phase 7 — Verification (required) + docs
- [x] E2E: **cross-repo** — create/delete a link in the UI; caller from a linked repo visible
      on the SymbolExplorer "Callers" tab.
- [x] E2E: **usage stats** — symbol lookup recorded; appears in the repository usage panel.
- [x] E2E: **cost-bounded docs** — budget respected (job totalItems == repo overview + budget).
- [x] E2E: full-app smoke test (all 7 routes) — proves the UI is not broken.
- [x] Run: `dotnet build`, `dotnet test` (369 pass / 1 skip), web `tsc -b` + `npm run build`,
      `npx playwright test` (14 pass).
- [x] Live image rebuilt + restarted; migrations 004/005/006 applied to the real DB; new APIs
      return 200; new UI shipped in the bundle.
- [x] Update README/docs (relations, links, usage, doc budget).

## Dependencies & sequencing

- Phase 1 (UI tests) first — it is the acceptance gate for everything else.
- Phase 3 (links) → Phase 4 (cross-repo extraction) → feeds Phase 2 (usage from relations).
- Phase 2 + Phase 4 → Phase 5 (usage-driven docs) → Phase 6 UI.
- Phases 2 and 3 are largely independent and can be built in parallel.

## Risks / hazards (from the code audit)

- Relation-table ownership/cleanup is unsafe for cross-repo edges today (single
  `repository_id` tag, `ReplaceForLanguageAsync` semantics) — must be redesigned first.
- Analyzer↔domain `RelationType` mismatch silently drops edges; fix while touching this.
- Ambiguous name/FQN matching across repos (external stubs, `#temp` tables) can create false
  cross-repo edges — needs confidence + origin tracking.
- Playwright e2e against the real 1.3 GB index is slow/nondeterministic → seed a fixture.
- LLM cost/latency: budget defaults must be conservative; document them.

## Blockers / external input

- [x] Human decision on repo-link semantics + UX — decided: directed source→target,
      types depends_on/calls/references/related, managed on the RepositoryDashboard.
- [x] Usage-scoring weights and default budget — decided: score = sum of 30-day usage counts,
      default budget 25, strategy "usage", minUsage 0 (UI "Only used objects" = 1).
- [x] e2e hosting/fixture — decided: seeded SQLite fixture, API :8091 + Vite :5173.

## Log
- Plan created from a code audit (relations, usage, repo model, doc prioritisation, UI, tests).
- Implemented all phases end-to-end:
  - Phase 1: Playwright + seeded fixture (`E2eDataSeeder`) + smoke; removed stale
    `vite.config.js` shadowing; CI `ui` job.
  - Phase 2: `symbol_usage` + `IUsageRecorder`/`IUsageQueryService` + API; recorded from
    search/find_symbol/get_symbol.
  - Phase 3: `repository_links` + entity/store/API + UI card.
  - Phase 4: relations gained endpoint/origin ownership; multi-repo catalog; cross-repo
    resolution in the SQL extractor + a language-agnostic text reference extractor; context
    tags cross-repo symbols `[repo: X]`.
  - Phase 5: usage-ranked selection + budget/strategy/minUsage through the job options.
  - Phase 6: links + usage UI cards; Documentation budget/strategy controls.
  - Phase 7: 14 Playwright tests green; 369 backend tests green; live image upgraded and
    migrations applied to the real DB.
- Deferred (documented): link suggestions, `changedOnly` scheduling, re-index-on-link-change,
  explicit doc-prompt wording pass, app→SQL detection beyond name matching (heuristic in place).
- Note: the cross-repo text extractor is name-based (len≥4 identifiers); it is intentionally
  conservative and only runs when an outgoing link is declared, so it cannot create edges
  without human intent.
- Real-repo verification (bcs_eva_app → eva-db, link `calls`):
  - first run produced 5,156 edges but 3,798 targeted `Unknown` JSON/HTML schema symbols
    (`Name`, `Type`, `Card`…) — noise;
  - restricted cross-repo targets to real code kinds and required distinctive matching
    (bare names only for procedures/functions/types; tables/views need `dbo.X`);
  - final: **647 edges** (634 Procedure + 13 Table), **484 distinct eva-db objects**
    referenced from **148** app symbols — e.g. `servGetBookmarkComposite`,
    `spGetCurrentPersonInfo`, `spBrokerServiceRSTestingConfirm`.
  - `/api/callers` for `dbo.spGetCurrentPersonInfo` returns `GC.DAL`/`BCS.DAL` from
    `bcs_eva_app` (cross-repo callers surface).
  - the 484 referenced objects are recorded as `symbol_usage` (source=cross_repo) and appear
    in `GET /api/repositories/{eva-db}/usage`, feeding usage-prioritised documentation.
  - 369 backend tests + 14 Playwright tests still green.
