# CodeAtlas — CodeRAG

Self-hosted система кодовой аналитики для OpenCode. Поиск по коду: FTS5 + Vector + Structural. (§1–§59 ТЗ)

## Быстрый старт

```bash
docker compose up -d
# открой http://localhost:8080
# health: curl http://localhost:8080/health/ready
```

Без профиля `embeddings` CodeRAG работает, semantic search деградирует (FTS only).

## Deployment

### Production (минимально)

```bash
mkdir -p ./data
docker compose up -d
docker compose logs -f coderag
curl -f http://localhost:8080/health/ready   # 200 → ready
curl -f http://localhost:8080/api/health     # { database, vector, embedding, indexer, storage }
```

Persist: единый volume `./data:/data` (§6 deployment.md):

```
./data/
  coderag.db          # SQLite
  coderag.db-wal/shm
  vector/             # векторный индекс (sqlite-vec / FAISS) — если внешний
  repos/<slug>/       # Git checkouts
  keys/               # DataProtection keys + SSH keys (chmod 700)
  models/             # embedding GGUF (если профиль embeddings)
  settings.json       # переопределяемые настройки (приоритет над env)
```

Конфигурация без пересборки (§34, deployment.md §7): `appsettings.json` + env `CODERAG__*` + `/data/settings.json` (последний — приоритет, hot reload через `IOptionsMonitor`). Секреты — через env или Docker secrets, логи без секретов.

### С эмбеддингами (профиль `embeddings`)

Автоматическое скачивание модели `bge-m3-Q8_0.gguf` при первом запуске (§57):

```bash
docker compose --profile embeddings up -d
# модель скачивается один раз в ./data/models/bge-m3-Q8_0.gguf
# проверить: docker compose logs llama-embeddings
curl -f http://localhost:8081/health   # llama.cpp
curl -f http://localhost:8080/api/health | jq .embedding
```

Переопределение модели через `.env` (см. `.env.example`):

```
MODEL_URL=https://huggingface.co/BAAI/bge-m3/resolve/main/bge-m3-Q8_0.gguf
MODEL_FILE=/models/bge-m3-Q8_0.gguf
LLAMA_ARGS=--embedding --port 8080 --threads 4
CODERAG__Embedding__Endpoint=http://llama-embeddings:8080/v1/embeddings
CODERAG__Embedding__Model=bge-m3
```

Требования: RAM ≥ 4GB для `bge-m3-Q8_0` (2GB модель + overhead), GPU опционально (`ghcr.io/ggerganov/llama.cpp:server-cuda`).

Альтернатива — `MODEL_URL` для `nomic-embed-text`, `e5-mistral`.

### Dev

```bash
docker compose -f docker-compose.dev.yml up -d
# ASPNETCORE_ENVIRONMENT=Development, логи Debug
docker compose -f docker-compose.dev.yml --profile embeddings up -d
```

### Health & Diagnostics (§35)

```
GET /health/live   → 200 если процесс жив
GET /health/ready  → 200 если DB + Vector + Indexer готовы (embedding — не блокирует)
GET /api/health    → детально: { database, vector, embedding, indexer, storage }
```

Web UI Diagnostics — из `/api/health`.

### Backup

Копия `./data` при остановленном writer или `VACUUM INTO`:

```bash
# Остановить запись
docker compose stop coderag
cp -a ./data ./data.backup-$(date +%F)
# или SQLite online backup:
sqlite3 ./data/coderag.db "VACUUM INTO './data/coderag.backup.db'"
docker compose start coderag
```

Для vector index (`sqlite-vec`) — тот же механизм (файл в `./data/`).

### Обновление

```bash
docker compose pull
docker compose up -d
```

Миграции применяются автоматически при старте (`DbInitializer`, `schema_migrations`).

### MCP (optional)

```bash
docker compose --profile mcp up -d
# coderag-mcp — stdio sidecar, volume /data:ro, зависит от coderag
```

Если MCP — тот же процесс что Api — `coderag-mcp` не нужен.

## Архитектура

См. `docs/architecture.md` (исключена из git, локально). Слои: `Domain`, `Application`, `Infrastructure` (SQLite+FTS5+Vector), `Git`, `Embeddings`, `Indexer`, `Analyzers` (Roslyn / Tree-sitter / SQL), `Api`, `Mcp`.

## Тестирование

```bash
dotnet test --collect:"XPlat Code Coverage"
docker compose -f docker-compose.test.yml up --exit-code-from api-tests  # интеграционные
```

См. `docs/testing.md`.

## Performance

Синтетический генератор: `python tests/perf/generate.py --repos 50 --chunks 1000000 -o ./data/perf`

Baseline: `docs/progress/perf-baseline.md`.

## CI

`.github/workflows/ci.yml` — `test` + `docker` jobs.

## Лицензия

TBD
