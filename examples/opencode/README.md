# CodeAtlas → OpenCode (MCP) example

Connect [OpenCode](https://opencode.ai) to a running [CodeAtlas](../../README.md) MCP
server so the agent can do semantic (code+text isolated) + full-text + structural code search and navigation
across repositories you've indexed. Default `search_code` is Hybrid (lexical+semantic+exact+structural);
explicit `SemanticCode` (`jina-code` @ `http://192.168.137.25:8082` — code chunks) and `SemanticText` (`jina-text` @ `http://192.168.137.25:8083` — docs/markdown) are isolated.

This folder contains:

| File | Goes to |
|---|---|
| `opencode.json` | your project root (or `~/.config/opencode/`) |

> No machine-specific paths are hardcoded. Everything path-related is driven by two
> environment variables (see step 2).

## Prerequisites

1. **CodeAtlas is built and indexed.** You have a data directory containing `coderag.db`
   with at least one repository indexed. Build if needed:
   ```bash
   dotnet build CodeAtlas.sln -c Release
   ```
2. **A .NET 10 runtime** is on `PATH` (the MCP is a `dotnet <dll>` stdio server).
3. **OpenCode** is installed.

The MCP reads embedding endpoints/models from the **`settings` table in `coderag.db`**
(loaded at startup), so you do **not** have to point it at the embedding servers via
env vars — the DB already holds them. You only tell it where the DLL and the data dir are.

## 1. Export the two variables

Set these for your shell (values depend on where you built CodeAtlas):

```bash
# Absolute path to the MCP assembly
export CODEATLAS_MCP_DLL="$PWD/src/CodeRag.Mcp/bin/Release/net10.0/CodeRag.Mcp.dll"

# Absolute path to the CodeAtlas data directory (the one with coderag.db)
export CODEATLAS_DATA_DIR="$PWD/data"
```

Run the `export`s from the CodeAtlas source root (or replace `$PWD` with your path).

## 2. Add the MCP config

Copy `opencode.json` into your project root (it merges with any existing `opencode.json`).
The `mcp.codeatlas` block launches the server with:

- `command: ["dotnet", "{env:CODEATLAS_MCP_DLL}"]` — starts the stdio server.
- `environment.CODERAG__DataDir: "{env:CODEATLAS_DATA_DIR}"` — where the MCP reads the
  DB (and therefore the settings table) from.
- `timeout: 30000` — the cold start (DB init + settings load) is slower than OpenCode's
  5 s default.

OpenCode registers the server's tools prefixed with the server name, so they appear as
`codeatlas_search_code`, `codeatlas_find_symbol`, `codeatlas_repositories`, etc.

The agent learns how to use the tools entirely from the MCP tool descriptions (each tool
documents its parameters, output format, and recommended chaining), so no skill or extra
prompt is required.

## 3. Verify

```bash
opencode mcp list        # "codeatlas" should show as running/connected
```

Then in an OpenCode session, ask:

```
Use codeatlas to find where payment orchestration is implemented in the eShop repo.
```

You should see the agent call `codeatlas_repositories` / `codeatlas_search_code` and
return real vector + full-text results. To confirm the effective embedding config, ask it
to call `codeatlas_embedding_status`.

Minimal tool check (inside OpenCode):

```
- codeatlas_repositories → eShop should appear with files/chunks/symbols/relations
- codeatlas_search_code("CatalogService", "Symbol") → exact CatalogService classes
- codeatlas_search_code("catalog service retrieving products", "SemanticCode") → CatalogService code
- codeatlas_search_code("How is the application configured?", "SemanticText") → docs
- codeatlas_search_code("youtube", "Semantic") → 0 results (weak query filtered, not garbage)
```

Available tools: `codeatlas_repositories`, `codeatlas_embedding_status`, `codeatlas_search_code` (modes Hybrid|Semantic|SemanticCode|SemanticText|FullText|Symbol), `codeatlas_get_code`, `codeatlas_find_symbol`, `codeatlas_get_symbol`, `codeatlas_get_relations`, `codeatlas_find_references`, `codeatlas_find_implementations`, `codeatlas_find_callers`, `codeatlas_find_callees`.
Chain: `search/find_symbol → get_symbol → get_relations → related symbolId → get_symbol/get_code`.

## Optional: override embedding endpoints

The MCP uses the settings table by default. To override a domain for one session, set the
matching env var (same names the API uses):

```bash
export EMBEDDINGS__CODE__ENDPOINT="http://host:port/v1/embeddings"
export EMBEDDINGS__CODE__MODEL="jina-code"
export EMBEDDINGS__CODE__DIMENSIONS="896"
# ... and/or EMBEDDINGS__TEXT__* for the text domain
```

Pass them through by adding them to the `mcp.codeatlas.environment` object in
`opencode.json`.

## Troubleshooting

- **`codeatlas` not connecting** — `dotnet` must be on `PATH` and `CODEATLAS_MCP_DLL`
  must point at a real `CodeRag.Mcp.dll`. The server logs to stderr; check the tool's
  timeout isn't too low.
- **`embedding_status` shows a wrong model / `search_code` says embeddings unavailable** —
  the settings table in `coderag.db` doesn't match the running embedding servers. Fix the
  `settings` rows (`Embeddings:Code:*` / `Embeddings:Text:*`) or use the env overrides
  above.
- **Agent doesn't know how to use the tools** — the tool descriptions come from the MCP
  server's `CodeRag.Mcp` assembly; if you built the DLL from an older commit, rebuild and
  re-point `CODEATLAS_MCP_DLL` at the new assembly.
