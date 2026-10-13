# CodeAtlas

Self-hosted search and navigation for code. Connect Git repositories and get fast search across source, symbols, and relationships.

> **⚠️ Deep alpha.** The DB schema, index format, and API change without migrations. On any update the DB may become incompatible — you'll need `rm -rf ./data && docker compose up -d` and a full reindex. Don't run in production without a backup.

## Features

- Search: exact (by FQN/name), full-text (FTS5 trigram), semantic (vector), structural (symbol relationships)
- Navigation: definition / references / implementations / callers / callees
- Web UI to manage repositories, search, and diagnose

## Quick start

```bash
docker compose up -d
# http://localhost:8085
curl http://localhost:8085/health/ready  # 200 → ready
```

Stop: `docker compose down`. Data lives in `./data` — don't delete it.

## How to use

1. **Repositories → Add Repository** — Git URL, branch, proxy/credentials if needed
2. **Jobs** — wait for indexing to finish
3. **Search** — query + filter by repo/language/symbol type; click → view the file with highlighting

## Analyzers

| Language | Engine | What it extracts |
|---|---|---|
| **C#** | Roslyn (`CSharpCompilation`, `SemanticModel`) | namespace/class/interface/record/struct/enum/method/property/field/event, inheritance, implementations, calls, parameter/return types. Distinguishes overloads and `NsA.Foo` vs `NsB.Foo` |
| **SQL** | `TSql170Parser` (ScriptDom) | `PROCEDURE/FUNCTION/VIEW/TABLE/TRIGGER` (CREATE/ALTER). Requires `GO` between batches |
| **VBS/VBScript** | Regex `^(Sub\|Function\|Class\|Property)` | Sub/Function/Class/Property + Class→method nesting, chunks split on `End Sub/Class` |
| **Python, JS/TS, Go, C++** | Tree-sitter | function/class/method + `Calls` by name. Medium depth, no full semantics. TS/TSX maps to `javascript` |

Unknown languages — sliding-window chunking (800 tokens, overlap 80). A single broken file never kills indexing.

Oversized symbol chunks (a whole class/method) are split into windows of at most `Indexer:MaxChunkChars` (default 1500 chars ≈ 375 tokens) so each embedding stays focused instead of being diluted by an entire file.

## Git and embeddings

**Git** — only through the local `git` CLI. Each repository gets a checkout in `/data/repos/<slug>`:
`clone --branch`, `fetch origin <branch>`, `diff --name-status -M IndexedCommit..newHead`, `ls-tree`, `rev-parse`. Supports proxy (`http/https/socks5`), `Credentials` (token/SSH), and `ls-remote` for validation. Sync is polling on `sync_interval` (default 5 min) + manual Sync/Re-index.

**Embeddings** — two independent OpenAI-compatible pipelines (ADR-011), each with its own endpoint, model, and vector space:
- **Code** — source code (C#/Python/SQL/VBS/C++/JS/Go; comments stay code). Default `http://code-embeddings:8080/v1/embeddings`, model `bge-m3`.
- **Text** — Markdown/README/docs/prose. Default `http://text-embeddings:8080/v1/embeddings`, model `all-MiniLM-L6-v2`.

Each calls `POST {endpoint}/v1/embeddings` with `{model, input, encoding_format}`; batching, retry with backoff, timeout, and an LRU cache. Dimension is auto-detected from the first response (`0 = auto`). Search merges code + text vector results as one semantic signal alongside FTS and structural. Without an endpoint for a domain, that domain degrades gracefully (a warning in the UI/API).

Enable the bundled local models (opt-in):
```bash
docker compose --profile embeddings up -d  # downloads the code + text GGUF models, needs ~8GB RAM total
```

Point either domain at an external API via settings (see below) — e.g. a different code vs text embedding server.

## Settings

Changeable without a rebuild: Web UI **Settings** → DB (`settings` table) → takes priority over `env CODERAG__*` and `/data/settings.json` (hot reload).

| Group | Key | Default | What it does |
|---|---|---|---|
| **Search** | `Search:DefaultSearchMode` | `Hybrid` | default mode: `semantic` / `lexical` / `structural` / `hybrid` |
| | `Search:LexicalWeight` | `0.35` | FTS weight in hybrid ranking |
| | `Search:SemanticWeight` | `0.35` | vector weight |
| | `Search:StructuralWeight` | `0.15` | relationship weight + `ExactBonus=1.0` |
| | `Search:MinSemanticScore` | `0.25` | drop vector matches below this cosine score (0 disables the filter) |
| | `Search:DefaultResultCount` | `20` | results per query (max 100) |
| | `Search:PreviewMaxLines` | `20` | preview lines |
| | `Search:VectorTopK` | `50` | candidates from the vector index |
| **Embedding (code)** | `Embeddings:Code:Endpoint` | `http://code-embeddings:8080/v1/embeddings` | OpenAI-compatible URL for code |
| | `Embeddings:Code:Model` | `bge-m3` | code model name, part of invalidation |
| | `Embeddings:Code:Dimensions` | `0` (auto) | code dimensionality (0 = auto-detect) |
| | `Embeddings:Code:BatchSize` / `Timeout` / `MaxRetries` | `32 / 30s / 3` | code batching and retry |
| | `Embeddings:Code:ApiKey` / `ProxyUrl` | — | code key and proxy |
| **Embedding (text)** | `Embeddings:Text:Endpoint` | `http://text-embeddings:8080/v1/embeddings` | OpenAI-compatible URL for text |
| | `Embeddings:Text:Model` | `all-MiniLM-L6-v2` | text model name, part of invalidation |
| | `Embeddings:Text:Dimensions` | `0` (auto) | text dimensionality (0 = auto-detect) |
| | `Embeddings:Text:BatchSize` / `Timeout` / `MaxRetries` | `32 / 30s / 3` | text batching and retry |
| | `Embeddings:Text:ApiKey` / `ProxyUrl` | — | text key and proxy |
| **LLM (docs)** | `Llm:Endpoint` | — (empty = off) | OpenAI-compatible base URL for documentation generation; empty → feature reports "LLM not configured" |
| | `Llm:Model` | `local-model` | model name in the request body |
| | `Llm:Temperature` | `0.2` | sampling temperature (0.0 – 2.0) |
| | `Llm:MaxTokens` | `4096` | max completion tokens (0 = server decides) |
| | `Llm:TimeoutSeconds` / `MaxRetries` / `BaseDelayMs` | `300 / 2 / 500` | per-request timeout, retries, backoff base (ms, exponential) |
| | `Llm:MaxContextChars` | `60000` | cap on prompt context chars sent to the model (0 = unlimited) |
| | `Llm:ApiKey` / `ProxyUrl` | — | bearer key and HTTP proxy |
| **Indexer** | `Indexer:MaxConcurrentIndexers` | `2` | parallel jobs |
| | `Indexer:DefaultSyncIntervalSeconds` | `300` | repo polling |
| | `Indexer:FullReindexThreshold` | `0.5` | >50% of files changed → full reindex |
| | `Indexer:MaxChunkChars` | `1500` | split oversized symbol chunks into windows of at most this many chars |
| | `Indexer:EnableFileWatcher` | `false` | FS watcher is dev-only |
| **Git** | `Git:ReposRoot` | `/data/repos` | where to clone |
| | `Git:DefaultTimeoutSeconds` | `120` | git command timeout |
| | `Git:EnableFileWatcher` | `false` | — |

Per-repository: `branch`, `proxy_id`, `credential_id`, `sync_interval_seconds`, `enabled`.

Example `.env` for external code + text embeddings:
```
CODE_EMBEDDINGS_ENDPOINT=https://api.external-host.com/v1/embeddings
CODE_EMBEDDINGS_MODEL=code-embed-v1
CODE_EMBEDDINGS_DIMENSIONS=1536
TEXT_EMBEDDINGS_ENDPOINT=https://api.external-host.com/v1/embeddings
TEXT_EMBEDDINGS_MODEL=text-embed-v1
TEXT_EMBEDDINGS_DIMENSIONS=1536
```

## MCP

```bash
docker compose --profile mcp up -d  # sidecar with /data:ro, stdio
```

## Updating and backup

```bash
docker compose pull && docker compose up -d  # migrations run at start

# backup (stop writes)
docker compose stop coderag
cp -a ./data ./data.backup-$(date +%F)
docker compose start coderag
```

If `health/ready != 200` or the schema is incompatible after an update — delete `./data` and reindex.

Diagnostics: `docker compose logs coderag`, `GET /api/health` (database/vector/embedding/indexer/storage), `GET /health/ready`.
