import { useState, useMemo } from "react";
import { useSearchParams, Link } from "react-router-dom";
import { useSearch, useRepositories, useFileContent } from "@/api/hooks";
import type { SearchMode, SearchHit } from "@/api/types";
import { SourceView, PreviewWithHighlight } from "@/components/SourceView";

const modes: SearchMode[] = ["Hybrid", "FullText", "Semantic", "Symbol"];
const languages = ["C#", "SQL", "Python", "JavaScript", "C++", "Go", "VBS"];

export default function SearchPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const initialQ = searchParams.get("q") ?? "";
  const initialRepo = searchParams.get("repositoryId") ?? "";
  const [query, setQuery] = useState(initialQ);
  const [mode, setMode] = useState<SearchMode>((searchParams.get("mode") as SearchMode) ?? "Hybrid");
  const [repositoryId, setRepositoryId] = useState(initialRepo);
  const [language, setLanguage] = useState(searchParams.get("language") ?? "");
  const [symbolKind, setSymbolKind] = useState(searchParams.get("symbolKind") ?? "");
  const [selected, setSelected] = useState<SearchHit | null>(null);

  const reposQ = useRepositories();

  const req = useMemo(() => ({
    query,
    mode,
    repositoryIds: repositoryId ? [repositoryId] : undefined,
    languages: language ? [language] : undefined,
    symbolKind: symbolKind || undefined,
    limit: 20,
    offset: 0,
  }), [query, mode, repositoryId, language, symbolKind]);

  const searchQ = useSearch(req, query.trim().length >= 2);

  const fileQ = useFileContent(
    selected?.repositoryId,
    selected?.filePath,
    selected?.startLine ?? undefined,
    selected?.endLine ?? undefined,
  );

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const p = new URLSearchParams();
    if (query) p.set("q", query);
    if (mode !== "Hybrid") p.set("mode", mode);
    if (repositoryId) p.set("repositoryId", repositoryId);
    if (language) p.set("language", language);
    if (symbolKind) p.set("symbolKind", symbolKind);
    setSearchParams(p);
  };

  return (
    <div>
      <h1 style={{ marginBottom: 12 }}>Search</h1>
      <form onSubmit={onSubmit} className="card" style={{ display: "flex", flexDirection: "column", gap: 10 }}>
        <div style={{ display: "flex", gap: 8 }}>
          <input
            placeholder="Search code... (e.g. DeactivateClient, IClientService, WHERE status = )"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            style={{ flex: 1 }}
          />
          <select value={mode} onChange={(e) => setMode(e.target.value as SearchMode)} style={{ width: 160 }}>
            {modes.map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
          <button type="submit" className="primary">Search</button>
        </div>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <select value={repositoryId} onChange={(e) => setRepositoryId(e.target.value)} style={{ flex: 1, minWidth: 160 }}>
            <option value="">All repositories</option>
            {reposQ.data?.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
          <select value={language} onChange={(e) => setLanguage(e.target.value)} style={{ flex: 1, minWidth: 140 }}>
            <option value="">All languages</option>
            {languages.map((l) => <option key={l} value={l}>{l}</option>)}
          </select>
          <select value={symbolKind} onChange={(e) => setSymbolKind(e.target.value)} style={{ flex: 1, minWidth: 140 }}>
            <option value="">All symbol kinds</option>
            {["Class", "Interface", "Method", "Function", "Property", "Field", "Enum", "Namespace", "Struct"].map((k) => <option key={k} value={k}>{k}</option>)}
          </select>
        </div>
        {searchQ.data?.warnings?.length ? (
          <div className="banner" style={{ fontSize: 13 }}>
            {searchQ.data.warnings.join(" · ")}
          </div>
        ) : null}
      </form>

      <div style={{ display: "flex", gap: 16, marginTop: 16, alignItems: "flex-start" }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          {searchQ.isLoading && query.trim().length >= 2 ? (
            <div className="skeleton" style={{ height: 200 }} />
          ) : searchQ.data ? (
            <>
              <div style={{ fontSize: 13, color: "var(--text-muted)", marginBottom: 8 }}>
                {searchQ.data.total} results {searchQ.data.total > searchQ.data.items.length ? `(showing ${searchQ.data.items.length})` : ""}
              </div>
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                {searchQ.data.items.map((hit, idx) => (
                  <div
                    key={`${hit.repositoryId}-${hit.filePath}-${hit.symbolId ?? idx}`}
                    className="card"
                    style={{ cursor: "pointer", borderColor: selected === hit ? "var(--accent)" : undefined }}
                    onClick={() => setSelected(hit)}
                  >
                    <div style={{ display: "flex", justifyContent: "space-between", gap: 8, fontSize: 13 }}>
                      <span style={{ fontWeight: 600 }}>{hit.repositoryName} <span style={{ color: "var(--text-muted)", fontWeight: 400 }}>/ {hit.filePath}</span></span>
                      <span style={{ color: "var(--accent)", fontWeight: 600 }}>{hit.score.toFixed(3)}</span>
                    </div>
                    <div style={{ fontSize: 12, color: "var(--text-muted)", marginTop: 2 }}>
                      {[hit.symbolName && `${hit.symbolKind ?? "symbol"} ${hit.symbolName}`, hit.language, hit.startLine && `L${hit.startLine}-${hit.endLine}`].filter(Boolean).join(" · ")}
                    </div>
                    <div className="search-preview" style={{ marginTop: 8 }}>
                      <PreviewWithHighlight preview={hit.preview} query={query} />
                    </div>
                    <div style={{ marginTop: 8, display: "flex", gap: 6, flexWrap: "wrap" }}>
                      {hit.symbolId && <Link to={`/symbols/${hit.symbolId}`} onClick={(e) => e.stopPropagation()}><button style={{ fontSize: 12, padding: "4px 8px" }}>Symbol →</button></Link>}
                      <span style={{ fontSize: 11, color: "var(--text-muted)", alignSelf: "center" }}>click to view source</span>
                    </div>
                  </div>
                ))}
                {searchQ.data.items.length === 0 && <div className="card" style={{ color: "var(--text-muted)", textAlign: "center" }}>No results for “{query}”.</div>}
              </div>
            </>
          ) : (
            <div style={{ color: "var(--text-muted)", fontSize: 14, marginTop: 12 }}>Enter a query (≥2 chars) to search. Try <code>Hybrid</code> for best recall.</div>
          )}
        </div>

        <div style={{ width: 560, maxWidth: "45%", position: "sticky", top: 16 }}>
          {selected ? (
            <div>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 8 }}>
                <strong style={{ fontSize: 13, wordBreak: "break-all" }}>{selected.filePath}</strong>
                <button onClick={() => setSelected(null)} style={{ fontSize: 12 }}>Close</button>
              </div>
              {fileQ.isLoading ? <div className="skeleton" style={{ height: 300 }} /> : fileQ.isError ? (
                <div className="card" style={{ borderColor: "var(--danger)", fontSize: 13 }}>Failed to load file: {(fileQ.error as Error).message}</div>
              ) : fileQ.data ? (
                <>
                  <SourceView
                    content={fileQ.data.content}
                    language={fileQ.data.language}
                    startLine={fileQ.data.startLine}
                    highlightLines={selected.startLine && selected.endLine ? [selected.startLine, selected.endLine] : null}
                  />
                  {selected.symbolId && (
                    <div style={{ display: "flex", gap: 6, marginTop: 8, flexWrap: "wrap" }}>
                      <Link to={`/symbols/${selected.symbolId}?tab=definition`}><button style={{ fontSize: 12 }}>Definition</button></Link>
                      <Link to={`/symbols/${selected.symbolId}?tab=references`}><button style={{ fontSize: 12 }}>References</button></Link>
                      <Link to={`/symbols/${selected.symbolId}?tab=implementations`}><button style={{ fontSize: 12 }}>Implementations</button></Link>
                      <Link to={`/symbols/${selected.symbolId}?tab=callers`}><button style={{ fontSize: 12 }}>Callers</button></Link>
                      <Link to={`/symbols/${selected.symbolId}?tab=callees`}><button style={{ fontSize: 12 }}>Callees</button></Link>
                    </div>
                  )}
                </>
              ) : null}
            </div>
          ) : (
            <div className="card" style={{ color: "var(--text-muted)", fontSize: 13, textAlign: "center", padding: 24 }}>
              Select a result to view source<br />
              <span style={{ fontSize: 12 }}>GET /api/repositories/{"{id}"}/file?path=&fromLine=&toLine=</span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
