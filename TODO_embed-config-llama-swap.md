# TODO: Embed-конфиг через llama-swap

## Цель
Эмбеддинги должны идти через llama-swap (единый endpoint, роутинг по `model`),
а не через старые порты 8082/8083. Векторы должны персиститься в sqlite-vec.

## Диагноз
1. `settings` (БД, приоритетнее env) содержал старые endpoints
   `192.168.137.25:8082` (code) и `:8083` (text).
2. Отдельный дефект образа: в `/app/vec0.so` попадал **arm64** бинарь
   (Dockerfile `find -name vec0.so` матчил и linux-arm64) → на x86_64
   `Loaded sqlite-vec` падал, стор уходил в InMemory (векторы не персистились).

## Изменения
- [x] `PUT /api/settings`: code/text endpoint → `http://192.168.137.25:8080/v1/embeddings`,
      models `jina-code` (dim 896) / `jina-text` (dim 1024)
- [x] Dockerfile: копировать `vec0.so` строго под arch сборки
      (`runtimes/linux-x64/native/vec0.so`), + проверка e_machine в билде
- [x] Пересобрать образ, перезапустить контейнер
- [x] Пересоздать пустой `coderag-vec.db` (0 векторов) чтобы таблицы создались
      на корректных dim: code=896, text=1024

## Проверка
- health: embedding code/text = Healthy (jina-code 896, jina-text 1024)
- логи: `Loaded sqlite-vec extension from /app/vec0.so`,
  `Created vec_chunks_code ... dim=896`, `Created vec_chunks_text ... dim=1024`
- fallback в InMemory больше не срабатывает

## Дополнительно найдено и исправлено (прод-инцидент "1 запрос/10с")
3. `vector_entries` имел FK `REFERENCES chunks(id)`, а `chunks` теперь в отдельной
   БД → любой insert падал `FOREIGN KEY constraint failed`; векторы не сохранялись.
   Таблица была пустой → пересоздана без FK (+ `repository_id`).
4. `EmbeddingService`: N+1 запись (по чанку = отдельное соединение) → один
   `UpsertBatchAsync` (одна транзакция).
5. `SqliteVecVectorStore.UpsertVectorEntriesAsync`: по строке в autocommit (64 WAL-коммита
   на батч) → одна транзакция.
6. `EmbeddingService`: чанк помечается `embedded` только ПОСЛЕ успешной записи вектора
   (иначе чанк навсегда остаётся без вектора).

## Результат
- Скорость эмбеддинга: ~0.1 chunks/s → ~22 chunks/s (≈37 мин на 48k вместо ~2 ч).
- `Vector upsert failed` = 0; `vector_entries` наполняется; `warnings` в семантическом
  поиске пусто, выдача векторная.

## Открытый вопрос
- `/api/statistics` = 500 из-за остаточного рассинхрона схемы
  (`repository_stats.language_breakdown` и пр. — колонки добавлены, требуется проверить).
