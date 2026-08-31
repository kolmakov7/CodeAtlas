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
# http://localhost:8080
curl http://localhost:8080/health/ready  # 200 → ready
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

**Embeddings** — any OpenAI-compatible API:
`POST {endpoint}/v1/embeddings` with `{model, input, encoding_format}`. Default `http://llama-embeddings:8080/v1/embeddings`, model `bge-m3` (1024 dim). Batching, retry with backoff, timeout, and an LRU cache. Without an endpoint, search degrades to FTS + structural (a warning in the UI/API).

Enable the local model:
```bash
docker compose --profile embeddings up -d  # downloads bge-m3-Q8_0.gguf ~2GB, needs ~4GB RAM
```

Switch to an external API via settings (see below).

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
| **Embedding** | `Embedding:Endpoint` | `http://llama-embeddings:8080/v1/embeddings` | OpenAI-compatible URL |
| | `Embedding:Model` | `bge-m3` | model name, part of invalidation |
| | `Embedding:Dimensions` | `1024` | dimensionality |
| | `Embedding:BatchSize` / `Timeout` / `MaxRetries` | `32 / 30s / 3` | batching and retry |
| | `Embedding:ApiKey` / `ProxyUrl` | — | key and proxy for an external API |
| **Indexer** | `Indexer:MaxConcurrentIndexers` | `2` | parallel jobs |
| | `Indexer:DefaultSyncIntervalSeconds` | `300` | repo polling |
| | `Indexer:FullReindexThreshold` | `0.5` | >50% of files changed → full reindex |
| | `Indexer:MaxChunkChars` | `1500` | split oversized symbol chunks into windows of at most this many chars |
| | `Indexer:EnableFileWatcher` | `false` | FS watcher is dev-only |
| **Git** | `Git:ReposRoot` | `/data/repos` | where to clone |
| | `Git:DefaultTimeoutSeconds` | `120` | git command timeout |
| | `Git:EnableFileWatcher` | `false` | — |

Per-repository: `branch`, `proxy_id`, `credential_id`, `sync_interval_seconds`, `enabled`.

Example `.env` for an external embedding:
```
CODERAG__Embedding__Endpoint=https://api.openai.com/v1/embeddings
CODERAG__Embedding__Model=text-embedding-3-small
CODERAG__Embedding__ApiKey=sk-...
CODERAG__Embedding__Dimensions=1536
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
