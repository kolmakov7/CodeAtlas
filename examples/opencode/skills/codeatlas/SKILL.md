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
| `codeatlas_embedding_status` | Show configured code/text embedding model + dimensions | — |
| `codeatlas_search_code` | Hybrid search (semantic + full-text + exact). Returns `repositoryId`, `file`, `lines`, `chunkId`, `symbolId` | `query`, `mode` (Hybrid\|Semantic\|FullText\|Symbol), `limit`, `repositoryIds`, `languages`, `symbolKind`, `minScore` |
| `codeatlas_get_code` | Read a slice of an indexed file | `repositoryId`, `path`, `startLine`, `endLine`, `maxLines` |
| `codeatlas_find_symbol` | Find symbols by name/FQN substring | `query`, `repositoryId`, `language`, `limit` |
| `codeatlas_get_symbol` | Full details for one symbol by id | `symbolId` |
| `codeatlas_get_relations` | Traverse a symbol's relations | `symbolId`, `direction` (outgoing\|incoming\|both), `relationTypes` (Calls, Inherits, Implements, DependsOn, Contains, Overrides), `limit` |
| `codeatlas_find_references` | Who uses a symbol | `symbolId` |
| `codeatlas_find_implementations` | Implementations of an interface | `symbolId` |
| `codeatlas_find_callers` | Callers of a method | `symbolId` |
| `codeatlas_find_callees` | What a method calls | `symbolId` |

## When to use me

- "What does X do / where is X implemented?" → `search_code` or `find_symbol`, then `get_symbol`/`get_code`.
- "Who calls / uses X?" → `find_symbol` → `find_callers` or `find_references`.
- "What does X call?" → `find_symbol` → `find_callees`.
- "Which classes implement interface I?" → `find_symbol` on the interface → `find_implementations`.
- Browsing an unfamiliar codebase → `repositories` first, then targeted `search_code`.

## Recommended workflow

1. If you don't already know the repo, call `codeatlas_repositories` to learn the
   `repositoryId`s and confirm the repo is indexed.
2. Locate with `codeatlas_search_code` (default `mode=Hybrid`) or `codeatlas_find_symbol`.
   Use `mode=Semantic` for intent, `mode=FullText` for exact identifiers.
3. Follow up with the `repositoryId`/`file`/`lines` and `symbolId` the result gives you:
   - read code → `codeatlas_get_code`
   - inspect a symbol → `codeatlas_get_symbol`
   - navigate the graph → `codeatlas_find_callers` / `find_callees` / `find_references` /
     `find_implementations` / `get_relations`
4. Scope multi-repo searches with `repositoryIds` (Guid array) and narrow with
   `languages` / `symbolKind`.

## Notes

- Guids must be full 36-char strings (e.g. `d597dcbc-4d0c-4d52-9dd0-f02dfd9fc5dd`).
- `search_code` results carry `score` (0..1) and `origin` (`vector`/`fts`/`exact`); raise
  `minScore` to drop weak matches.
- If a domain is unconfigured, `search_code` still returns full-text/exact hits and reports
  a warning; check `codeatlas_embedding_status` to see the effective model + dimensions.
- I only see what has been indexed. If a repo or recent commit is missing, it needs
  re-indexing in CodeAtlas first.
