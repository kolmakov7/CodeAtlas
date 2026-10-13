# TODO: Некорректная статистика скорости обработки embeddings

## Цель / Definition of done
На странице Jobs поле Speed для embedding-джобы показывает скорость обработки
эмбеддингов (chunks/s), а не «скорость запросов» и не размытое временем ожидания
среднее. Прод не трогаем — только код + локальные unit-тесты.

## Диагноз
Было две причины некорректной цифры:
1. `IndexingJob.Create` ставил `StartedAt` в момент постановки в очередь, а
   `MarkRunning()` его не обновлял → в знаменатель `itemsDone/elapsed` попадало
   время ожидания в очереди / слоте семафора / per-repo локе и начальное чтение
   чанков из БД. Скорость занижалась, ETA завышался.
2. В UI подпись была `items/s` для всех типов, а значение считалось как среднее
   с момента старта. Пользователь видел «запросы», а не эмбеддинги.
   (Единица у embedding-джобы — чанки.)

## Изменения
- [x] Создать TODO
- [x] `IndexingJob.MarkRunning()` выставляет `StartedAt = at ?? UtcNow` (фактический старт)
- [x] `Jobs.tsx`: единица через `jobUnit()` (Embedding → chunks, иначе files)
- [x] `Jobs.tsx`: мгновенная скорость `useJobSpeeds()` по дельтам опроса (2s) —
      реальный поток chunks/s, не размывается ожиданием/чтением; fallback на среднюю
- [x] Speed/ETA подписаны `chunks/s` для embedding
- [x] Unit-тест `IndexingJobTimingTests` (5 тестов) — start от выполнения, очередь в знаменатель не входит
- [x] `tsc -b` для Web — OK
- [x] Полный прогон unit-тестов локально: 363 passed, 1 skipped

## Лог
- Изучены: `IndexingJob.cs`, `IndexingJobStore.cs`, `JobRunner.cs`,
  `IndexingBackgroundService.cs`, `IndexingService.cs`, `EmbeddingService.cs`,
  `EmbeddingJobHandler.cs`, `OpenAICompatibleEmbeddingProvider.cs`, `Jobs.tsx`.

## Прод
Не трогали. Деплой (пересборка образа + рестарт) — по отдельной команде,
чтобы не сбить идущую индексацию.
