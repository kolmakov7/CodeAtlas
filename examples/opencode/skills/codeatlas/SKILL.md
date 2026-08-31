---
name: codeatlas
description: Search and navigate code across repositories indexed by CodeAtlas. Use for semantic/keyword code search, finding symbols by name or FQN, and tracing callers, callees, references, and implementations.
license: MIT
compatibility: opencode
metadata:
  mcp_server: codeatlas
---

## What I do

I give you code-intelligence over repositories already indexed by a running CodeAtlas
MCP server. All tools are prefixed with `codeatlas_` in OpenCode. I combine:

- **Semantic search** (vector) — "find code like this intent"
- **Full-text search** (FTS5) — exact tokens / identifiers
- **Structural search** — symbol name, FQN, and relationship graph (calls, inheritance,
  implementations, references)

## Tools

| Tool | Purpose | Key arguments |
|---|---|---|
| `codeatlas_repositories` | List indexed repos + counts; get `repositoryId`s for scoping | — |
| `codeatlas_embedding_status` | Show configured code/text embedding model + dimensions (code=`http://192.168.137.25:8082` / text=`http://192.168.137.25:8083`) | — |
| `codeatlas_search_code` | Search with explicit mode. Returns `repositoryId`, `file`, `lines`, `chunkId`, `symbolId` | `query`, `mode` (Hybrid\|Semantic\|SemanticCode\|SemanticText\|FullText\|Symbol), `limit`, `repositoryIds`, `languages`, `symbolKind`, `minScore` |
| `codeatlas_get_code` | Read a slice of an indexed file (relative path) | `repositoryId`, `path`, `startLine`, `endLine`, `maxLines` |
| `codeatlas_find_symbol` | Find symbols by name/FQN substring (ranks Class/Interface first) | `query`, `repositoryId`, `language`, `limit` |
| `codeatlas_get_symbol` | Full details for one symbol by id (includes container type) | `symbolId` |
| `codeatlas_get_relations` | Traverse a symbol's relations | `symbolId`, `direction` (outgoing\|incoming\|both), `relationTypes` (Calls, Inherits, Implements, DependsOn, Contains, Overrides, References), `limit` |
| `codeatlas_find_references` | Who uses a symbol (incoming Calls/DependsOn/References) | `symbolId` |
| `codeatlas_find_implementations` | Implementations of an interface (incoming Implements) | `symbolId` |
| `codeatlas_find_callers` | Callers of a method (incoming Calls) | `symbolId` |
| `codeatlas_find_callees` | What a method calls (outgoing Calls) | `symbolId` |

## When to use me

- "What does X do / where is X implemented?" → `search_code` (Hybrid default) or `find_symbol`, then `get_symbol`/`get_code`.
- "Who calls / uses X?" → `find_symbol` → `find_callers` or `find_references` → `get_symbol` of caller.
- "What does X call?" → `find_symbol` → `find_callees`.
- "Which classes implement interface I?" → `find_symbol` on the interface → `find_implementations` → `get_relations` on impl.
- Browsing an unfamiliar codebase → `repositories` first, then targeted `search_code`.

## Search mode guide

- **Hybrid (default)** — `search_code(query)` without mode. Combines FullText + semantic (code+text) + exact + structural. Best first try; scope with `languages: "csharp"` to drop docs when you want code.
- **SemanticCode** — `mode=SemanticCode` (code embedding `jina-code` @ `http://192.168.137.25:8082`). Use for natural-language code intent: "catalog service retrieving products", "where is basket checkout?". Searches only code chunks (321k? actually 5007 code vs 321 text).
- **SemanticText** — `mode=SemanticText` (text embedding `jina-text` @ `http://192.168.137.25:8083`). Use for documentation/prose: "How is the application configured?", "How does catalog service work?" — searches only markdown/docs corpus.
- **Semantic** — generic merged (code+text). Kept for compat; prefer explicit SemanticCode/SemanticText.
- **FullText** — lexical FTS5 only. Use for exact identifiers/tokens: "CatalogService", "ApiUrlBase".
- **Symbol** — exact `name==query OR fqn==query` → chunks. Use when you know the symbol name.

If a weak/ambiguous query like `youtube`/`timeout`/`endpoint` returns 0 results, that's intentional — raise `minScore` or prefer Hybrid/FullText instead of forcing semantic.

## Recommended workflow

1. If you don't already know the repo, call `codeatlas_repositories` to learn the
   `repositoryId`s and confirm the repo is indexed.
2. Locate with `codeatlas_search_code` (default `mode=Hybrid`) or `codeatlas_find_symbol`.
   For code intent use `SemanticCode`, for docs use `SemanticText`, for identifiers use `FullText`.
3. Follow up with the `repositoryId`/`file`/`lines` and `symbolId` the result gives you:
   - read code → `codeatlas_get_code` (path is relative to repo root, e.g. `src/Catalog.API/Apis/CatalogApi.cs`)
   - inspect a symbol → `codeatlas_get_symbol` (returns kind, FQN, namespace, signature, location, container)
   - navigate the graph → `codeatlas_get_relations` (direction `outgoing|incoming|both`, filter `relationTypes`), `find_callers` / `find_callees` / `find_references` / `find_implementations`
4. Chain: `search → symbolId → get_symbol → get_relations → related symbolId → get_symbol/get_code` — verify each step, follow the endpoint ids to keep traversing.
5. Scope multi-repo searches with `repositoryIds` (Guid array) and narrow with `languages` / `symbolKind`.

## Notes

- Guids must be full 36-char strings (e.g. `d597dcbc-4d0c-4d52-9dd0-f02dfd9fc5dd`).
- `search_code` results carry `score` (0..1) and `origin` (`vector`/`fts`/`exact`); raise
  `minScore` to drop weak matches.
- If a domain is unconfigured, `search_code` still returns full-text/exact hits and reports
  a warning; check `codeatlas_embedding_status` to see the effective model + dimensions.
- I only see what has been indexed. If a repo or recent commit is missing, it needs
  re-indexing in CodeAtlas first.
