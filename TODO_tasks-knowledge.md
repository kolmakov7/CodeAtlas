# CodeAtlas: централизованные задачи и управляемая база знаний

Источник истины по прогрессу этой задачи. Состояния: `[ ]` не делал, `[~]` в процессе,
`[x]` закончил, `[!]` заблокировано.

## Цель / Definition of Done

- [x] Task Management: задачи/подзадачи/зависимости/история через MCP и REST, без обязательных TODO-файлов.
- [x] Knowledge Memory: хранение, embeddings, семантический и текстовый поиск, области видимости, связи.
- [x] Ревизия: детерминированные кандидаты, устойчивая очередь, ручной LLM-анализ, apply с проверкой версий и undo.
- [x] LLM необязательна: сервер, MCP, поиск, очередь и UI работают без неё.
- [x] Планировщик недельной ревизии, идемпотентный, без автоматического LLM-применения.
- [x] UI: задачи, знания, очередь консолидации, сравнение и управление.
- [x] Миграции SQLite+PostgreSQL без потери данных; существующий RAG без регрессий.
- [x] Автотесты backend + UI smoke/e2e, сборка solution и web.
- [x] build-todo агент заменён на работу через централизованные MCP-задачи.

## Этап 1. Исследование

- [x] Изучить хранение, миграции, порты и stores.
- [x] Изучить MCP-хост, инструменты, соглашения REST API.
- [x] Изучить embeddings, vector stores, FTS, гибридный поиск.
- [x] Изучить фоновые задачи, планировщик, конфигурацию, health, UI, тесты.
- [x] Найти и разобрать текущий build-todo агент (`~/.config/opencode/agent/build-todo.md`) и skill `project-task`.
- [x] Подготовить отчёт `docs/tasks-knowledge-research.md` с привязкой к файлам.
- [x] Согласовать ключевые архитектурные решения с пользователем.

## Этап 2. Проектирование

- [x] Утвердить модель сущностей и связей (tasks, knowledge, entity_links, history, consolidation).
- [x] Утвердить стратегию embeddings для знаний (отдельная `vec_knowledge`).
- [x] Утвердить конкурентный протокол (optimistic version + external_key).
- [x] Утвердить состояния очереди ревизии и поведение при недоступной LLM.
- [x] Утвердить план UI и MCP/REST API.
- [x] Утвердить план тестов и миграций.

## Этап 3. Task Management

- [x] Domain: Task, TaskKind/Status/Priority, TaskComment, entity links/history ports.
- [x] Миграции Main 007 (SQLite + PostgreSQL).
- [x] Stores: TaskStore, HistoryStore, LinkStore.
- [x] TaskService: create/get/list/update/tree/deps/comments/archive/restore/history.
- [x] Optimistic concurrency и идемпотентность (external_key).
- [x] REST `/api/tasks`.
- [x] MCP tools `tasks`.
- [x] Импорт/экспорт TODO-файлов с дедупликацией.
- [x] Тесты: CRUD, версии, циклы зависимостей, параллельные агенты, импорт.

## Этап 4. Knowledge Memory

- [x] Domain: KnowledgeEntry, KnowledgeType/Scope/Status, порты.
- [x] Миграции: knowledge_entries, knowledge_fts, entity_links/history.
- [x] KnowledgeStore + FTS sync.
- [x] `vec_knowledge` (отдельная коллекция) и KnowledgeEmbeddingService (text provider).
- [x] KnowledgeSearchService: semantic/lexical/hybrid, фильтры scope/type/status.
- [x] Связи knowledge↔task↔symbol/file, история, устаревание.
- [x] REST `/api/knowledge`.
- [x] MCP tools `knowledge`.
- [x] Тесты: изоляция project/global, поиск, идемпотентность, недоступные embeddings.

## Этап 5. Ревизия и консолидация

- [x] Миграции: consolidation_jobs/items, revision_runs.
- [x] Детерминированная инвентаризация: дубликаты, группы, без источников, устаревшие ссылки, противоречия-кандидаты.
- [x] Идемпотентная очередь (dedupe_key), состояния, attempts/last_error.
- [x] LLM-анализ по команде; `awaiting_llm` без авто-ретраев.
- [x] Proposal + input version/hashes.
- [x] Apply с транзакцией, проверкой версий, supersede originals, history, re-embed.
- [x] Reject/cancel/postpone/retry/undo.
- [x] REST `/api/revision`.
- [x] MCP tools `revision`.
- [x] Тесты: идемпотентность, LLM off, конфликт версий, apply/undo.

## Этап 6. Планировщик и эксплуатация

- [x] RevisionSchedulerService (только Api host), недельный интервал, ручной запуск.
- [x] Настройки Revision:* и отображение состояния LLM как зависимости.
- [x] Health/diagnostics: LLM status, queue depth, revision last run.
- [x] Восстановление после рестарта, накопление очереди, длительная недоступность LLM.

## Этап 7. UI

- [x] Страница Tasks: список, фильтры, дерево, детали, история, комментарии.
- [x] Страница Knowledge: поиск, фильтры, детали, источники, связи.
- [x] Страница Review: очередь, сравнение, запуск анализа, accept/reject/undo.
- [x] Навигация, типы, hooks, настройки Revision.
- [x] Playwright smoke + сценарии.

## Этап 8. build-todo агент и документация

- [x] Переписать `~/.config/opencode/agent/build-todo.md` на MCP-задачи.
- [x] Обновить skill `project-task` (или заменить его ссылкой на MCP-протокол).
- [x] Документация MCP, моделей данных, миграции TODO и руководство пользователя.
- [x] Финальная проверка: solution build, dotnet test (376 passed/1 skip), web build, e2e (17 passed).

## Решения (подтверждены)

- Контекст MCP: revision tools скрыты по умолчанию (`Knowledge:Revision:ToolsEnabled` / `MCP_REVISION_TOOLS=1`).

## Открытые решения

- [x] Отдельная vector-коллекция `vec_knowledge` (подтверждено).
- [x] Область: `projects` + `project_repositories` (проект ↔ несколько репозиториев),
      `project_id = NULL` — глобальная память (подтверждено).
- [x] build-todo агент: MCP-only, без файлового fallback (подтверждено).
- [x] Приоритет: целое 0..4 (подтверждено).
- [x] История: одна таблица `entity_history` (рекомендуется) vs раздельные таблицы.
