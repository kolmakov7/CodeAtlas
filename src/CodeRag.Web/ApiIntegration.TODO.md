# TODO: CodeRag.Api static integration (Phase 7)

When `src/CodeRag.Api` is created (ASP.NET Core .NET 8/10), add:

```csharp
// Program.cs — after builder.Build()
var app = builder.Build();

app.UseStaticFiles(); // serves CodeRag.Web/dist via wwwroot
// On .NET 9+ prefer:
// app.MapStaticAssets();

app.MapFallbackToFile("index.html"); // SPA fallback for React Router

// Ensure static files are copied:
// In CodeRag.Api.csproj:
// <ItemGroup>
//   <Content Include="..\CodeRag.Web\dist\**" CopyToPublishDirectory="PreserveNewest" LinkBase="wwwroot" />
// </ItemGroup>

// Or in Dockerfile (multi-stage):
// FROM node:20 AS web-build
// WORKDIR /src/CodeRag.Web
// COPY src/CodeRag.Web/package*.json ./
// RUN npm ci
// COPY src/CodeRag.Web ./
// RUN npm run build
//
// FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
// COPY --from=web-build /src/CodeRag.Web/dist /src/CodeRag.Api/wwwroot
```

Current status: `CodeRag.Api` does NOT exist yet in this repo (only Domain/Infrastructure/etc). 
`CodeRag.Web/dist` is built and ready at `src/CodeRag.Web/dist`. 
On first `CodeRag.Api` creation, apply the snippet above.

Vite proxy (`vite.config.ts`) already points `/api` → `http://localhost:8080` for dev.
