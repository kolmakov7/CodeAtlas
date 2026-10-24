# Documentation Generation — Fixes & UX

Goal (from user):
1. Add LLM model settings for documentation; default model = `qwen3.8:27b`
   (from opencode config), endpoint = opencode base URL.
2. Make the Documentation page understandable: "Generate target" is cryptic
   (type `repo`, free-text field → user must guess). Redesign so intent is obvious.
3. After implementing, generate docs for `eFinServiceSubject` in the `eva-db`
   repository via CodeAtlas and verify quality. If it is nonsense, rework and retry.

Definition of done:
- A Settings group to configure the doc-generation LLM (endpoint/model/temperature/
  max tokens/timeout/etc.), defaulting to qwen3.8:27b.
- Backend can resolve a symbol target from a name/FQN (not only `symbolId`).
- Documentation UI is self-explanatory; API field contract matches frontend types.
- Full .NET build + tests green; web typecheck/build green.
- Real end-to-end doc for `dbo.eFinServiceSubject` generated and reviewed.

## Checklist

### Phase 0 — Recon
- [x] Read existing `TODO_documentation-generation.md` + explore backend/UI
- [x] Confirm eva-db indexed, `eFinServiceSubject` symbol exists, LLM gateway reachable
- [x] Record findings / design

### Phase 1 — LLM settings (default qwen3.8:27b)
- [x] `LlmOptions`: add `DefaultEndpoint` / `DefaultModel` constants
- [x] `appsettings.json`: add `Llm` section (opencode endpoint + qwen3.8:27b)
- [x] `AppSettingsDto`: new `Llm` group (`Flatten` + `FromConfiguration` with defaults)
- [x] `SettingsController`: validate optional `Llm:Endpoint`
- [x] README settings table: reflect new defaults

### Phase 2 — Backend symbol resolution
- [x] `DocumentationController`: resolve symbol from `targetRef`/name/FQN when
      `symbolId` is absent (uses `ISymbolStore.SearchAsync`)

### Phase 3 — Frontend contract + UX
- [x] `types.ts`: align `Documentation*` with API (name/status casing/model/error…)
- [x] `types.ts`: add `llm` to `Settings`
- [x] `hooks.ts`: payloads (`symbolId`, `force`, repo body)
- [x] `Documentation.tsx`: redesign (clear labels, symbol picker, help, llm banner)
- [x] `Settings.tsx`: new "Documentation (LLM)" tab

### Phase 4 — Verify build/tests
- [x] `dotnet build` clean
- [x] `dotnet test` green (0 failed / 363 passed / 1 skip)
- [x] web `tsc -b` + `npm run build` green

### Phase 5 — End-to-end validation on eva-db
- [x] Rebuild/run app with LLM configured (docker image rebuilt, container restarted)
- [x] Generate doc for `dbo.eFinServiceSubject` (main 15k-line procedure) → adequate
- [x] Generate doc for `dbo.fnIsServiceCatalogPUT` via name/FQN (no symbolId) → adequate
- [x] Review output; quality is good (grounded, anti-hallucination working)
- [x] (finding, not fixed) Symbol target identity = FQN → duplicate-FQN stubs collide

### Phase 6 — Repository-wide generation for SQL repos
- [x] `DocumentationService.IsDocumentable`: include SQL kinds (Procedure/Function/View/Table)
- [x] Deduplicate candidates by target key before the `maxSymbols` cap; keep the most
      substantial declaration (largest line span) — fixes 25 symbols → 16 docs
- [x] Order candidates by importance (KindRank) instead of raw FQN — avoids temp tables
- [x] Wire `maxSymbols`/`force` through the background job: new `indexing_jobs.options`
      JSON column + migration `003_job_options.sql` (SQLite + Postgres), entity + store,
      handler parsing, controller + MCP passing options
- [x] UI: "Max targets" input next to "Generate repository docs"
- [x] Build/test/web green
- [x] E2E: repo overview + 25 real procedures for eva-db → 26/26 generated, 0 failed

## Log
- Recon: eva-db repo id `04056b97-5c77-462e-80b5-e8b01f16b9f5`; symbol
  `eFinServiceSubject` (SQL Procedure, fqn `dbo.eFinServiceSubject`, main file
  `schema/09_procedures/dbo_eFinServiceSubject.sql`, 15365 lines). LLM gateway
  `http://192.168.137.25:4000/v1` reachable from host and container; exposes
  `qwen3.8:27b`. No `Llm` settings group exists; endpoint default empty → feature off.
- Done: LLM settings group added (default endpoint `http://192.168.137.25:4000/v1`,
  model `qwen3.8:27b`); symbol targets resolvable by name/FQN; Documentation page
  redesigned with a symbol picker + clear labels; Settings has a "Documentation (LLM)"
  tab. Build/tests/web green.
- E2E: rebuilt image, restarted container. `dbo.eFinServiceSubject` doc (8000 chars,
  91s) correctly names real tables/params/functions/task IDs and says "Not enough
  information" for tests. `dbo.fnIsServiceCatalogPUT` generated in 32s from FQN only.
- Known limitation (pre-existing, flagged): symbol `TargetRef` is the FQN, and
  (repository, target_type, target_ref) is the unique key. eva-db has external stub
  symbols sharing a FQN with the real body (e.g. `P_dbo_eFinServiceSubject.sql` stub
  vs `dbo_eFinServiceSubject.sql`), so documenting one overwrites the other. UI shows
  file paths in the picker so the user can choose, but only one entry per FQN persists.
- Phase 6: eva-db is SQL-only (12 401 documentable objects: 7 930 procedures, 1 799
  tables, 1 440 functions, 1 232 views) — the batch previously documented none of them.
  Widened kinds + dedupe + importance ordering + job `maxSymbols`. First capped run
  (25 raw symbols) produced only 16 docs (duplicate FQNs ate the budget) and skewed to
  temp tables; after the fix the capped run produced exactly 26 docs (repo overview +
  25 distinct procedures), 0 failed, all from `schema/09_procedures/`. Reviewed
  `apiGetBrokerGenAgreements`, `apiSearchClients`, `#AllBankAccounts`, `__test000` —
  accurate and grounded.
- Note: the repository-overview doc for eva-db is skewed toward HTML/JSON samples
  (`data-rules.json`, `data/deviants/Card/*.html`) because `DocumentationContextBuilder`
  samples files by symbol/related-file count; it is honest about the missing SQL context
  but could be improved by biasing the sample toward the dominant language.
