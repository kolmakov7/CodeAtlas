# CodeRag.Web — React + Vite + TypeScript

Web UI — first-class control plane (docs/web-ui.md, ADR-006). Статика раздаётся `CodeRag.Api` через `MapStaticAssets` / `UseStaticFiles` + `MapFallbackToFile("index.html")`.

## Стек
- React 18 + Vite 5 + TypeScript 5
- react-router-dom 6, @tanstack/react-query 5, axios
- orval / openapi-typescript (генерация ApiClient из OpenAPI)
- Минимальный CSS (Mantine/shadcn — placeholder, можно добавить позже)

## Разработка
```bash
cd src/CodeRag.Web
npm install
npm run dev          # vite dev server, proxy /api -> http://localhost:8080
npm run build        # tsc + vite build -> dist/
npm run preview      # preview dist on 4173
npm run generate:api # orval из http://localhost:8080/openapi/v1.json
```

## Интеграция с Api
`vite.config.ts` проксирует `/api`, `/health`, `/openapi` на `http://localhost:8080`.

`CodeRag.Api` (когда появится) должен:

```csharp
// Program.cs
app.UseStaticFiles(); // или app.MapStaticAssets() на .NET 9+
app.MapFallbackToFile("index.html");
```

`dist/` копируется в `CodeRag.Api/wwwroot` при публикации (или Docker `COPY --from=web-build`).

Без Node: структура уже готова, можно собрать на CI с Node 18+.

## Страницы
- Dashboard — GET /api/statistics (global)
- Repositories / Repository Dashboard — CRUD + Sync/Re-index/Pause/Resume/Delete
- Search — Hybrid/FTS/Semantic/Symbol + SourceView (GET /api/repositories/{id}/file)
- Symbol Explorer — Definition/References/Implementations/Callers/Callees
- Jobs — polling GET /api/jobs 2s, Cancel/Retry
- Settings — 8 секций + Proxies/Credentials
- Diagnostics — DB/Vector/Embedding/Indexer/Storage OK/FAIL, баннер Semantic unavailable

## E2E
Playwright `npx playwright test` против `vite preview` + `docker compose up -d`.
