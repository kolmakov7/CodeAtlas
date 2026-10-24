import { useSearchParams, Link } from "react-router-dom";
import { useMemo, useState } from "react";
import {
  useRepositories,
  useDocumentationList,
  useDocumentationDetail,
  useDocumentationActions,
  useFindSymbol,
  useSettings,
  useJobs,
} from "@/api/hooks";
import { ProgressBar } from "@/components/ProgressBar";
import { formatDate } from "@/utils/format";
import type { Documentation, DocumentationTargetType, SymbolInfo } from "@/api/types";

const STATUS_ORDER: Record<string, number> = {
  generated: 0,
  stale: 1,
  generating: 2,
  failed: 3,
  notgenerated: 4,
};

const TARGET_TYPE_LABEL: Record<DocumentationTargetType, string> = {
  repository: "Repository",
  module: "Module / directory",
  symbol: "Symbol",
};

const TARGET_TYPES: DocumentationTargetType[] = ["repository", "module", "symbol"];

function StatusBadge({ status }: { status: string }) {
  const color = status === "generated" ? "var(--success)"
    : status === "failed" ? "var(--danger)"
    : status === "stale" ? "var(--warning, #d97706)"
    : "var(--text-muted)";
  return <span className="badge" style={{ color, borderColor: color }}>{status}</span>;
}

function DocRow({ doc, onOpen, onDelete, deleting }: { doc: Documentation; onOpen: () => void; onDelete: () => void; deleting: boolean }) {
  return (
    <tr style={{ cursor: "pointer" }} onClick={onOpen}>
      <td><span className="badge">{TARGET_TYPE_LABEL[doc.targetType] ?? doc.targetType}</span></td>
      <td style={{ wordBreak: "break-all" }}>
        <div style={{ fontWeight: 500 }}>{doc.name}</div>
        {doc.targetRef !== doc.name && (
          <div style={{ fontFamily: "monospace", fontSize: 11, color: "var(--text-muted)" }}>{doc.targetRef}</div>
        )}
      </td>
      <td><StatusBadge status={doc.status} /></td>
      <td style={{ fontSize: 12, color: "var(--text-muted)", whiteSpace: "nowrap" }}>{doc.model ?? "—"}</td>
      <td style={{ fontSize: 12, color: "var(--text-muted)", whiteSpace: "nowrap" }}>{formatDate(doc.updatedAt)}</td>
      <td style={{ textAlign: "right" }}>
        <button
          className="danger"
          style={{ fontSize: 12 }}
          disabled={deleting}
          onClick={(e) => {
            e.stopPropagation();
            onDelete();
          }}
        >
          Delete
        </button>
      </td>
    </tr>
  );
}

export default function DocumentationPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const reposQ = useRepositories();
  const repos = reposQ.data ?? [];
  const actions = useDocumentationActions();
  const settingsQ = useSettings();

  const paramRepo = searchParams.get("repositoryId");
  const repoId = paramRepo && repos.some((r) => r.id === paramRepo)
    ? paramRepo
    : (paramRepo ?? repos[0]?.id);

  const [targetFilter, setTargetFilter] = useState<DocumentationTargetType | "all">("all");
  const [openId, setOpenId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // single-target generation
  const [genMode, setGenMode] = useState<"symbol" | "module">("symbol");
  const [symbolQuery, setSymbolQuery] = useState("");
  const [selectedSymbol, setSelectedSymbol] = useState<SymbolInfo | null>(null);
  const [moduleRef, setModuleRef] = useState("");
  // repository-wide generation
  const [repoLimit, setRepoLimit] = useState<string>("");
  const [repoStrategy, setRepoStrategy] = useState<"usage" | "kind">("usage");
  const [repoOnlyUsed, setRepoOnlyUsed] = useState(false);

  const listQ = useDocumentationList(repoId, targetFilter === "all" ? undefined : targetFilter);
  const detailQ = useDocumentationDetail(openId ?? undefined);
  const jobsQ = useJobs(repoId, null);
  const findQ = useFindSymbol(selectedSymbol ? "" : symbolQuery, repoId);

  const llmConfigured = settingsQ.data?.llm?.configured ?? true;

  const docs = useMemo(() => {
    const list = listQ.data ?? [];
    return [...list].sort((a, b) => {
      const t = (STATUS_ORDER[a.status] ?? 9) - (STATUS_ORDER[b.status] ?? 9);
      if (t !== 0) return t;
      return (b.updatedAt ?? "").localeCompare(a.updatedAt ?? "");
    });
  }, [listQ.data]);

  const docJobs = (jobsQ.data ?? []).filter(
    (j) => j.type === "Documentation" && (j.status === "Running" || j.status === "Queued"),
  );

  const setRepo = (id: string) => {
    const next = new URLSearchParams(searchParams);
    if (id) next.set("repositoryId", id);
    else next.delete("repositoryId");
    setSearchParams(next);
    setOpenId(null);
  };

  const handleDelete = (id: string) => {
    if (!window.confirm("Delete this documentation entry?")) return;
    actions.remove.mutate(id);
    if (openId === id) setOpenId(null);
  };

  const startRepoGenerate = () => {
    if (!repoId) return;
    setError(null);
    const maxSymbols = repoLimit.trim() ? Number(repoLimit) : undefined;
    actions.generateRepository.mutate(
      {
        repositoryId: repoId,
        maxSymbols: maxSymbols && maxSymbols > 0 ? maxSymbols : undefined,
        strategy: repoStrategy,
        minUsage: repoOnlyUsed ? 1 : 0,
      },
      { onError: (e: any) => setError(e.response?.data?.title || e.message) },
    );
  };

  const canGenerateTarget = genMode === "symbol" ? !!selectedSymbol : !!moduleRef.trim();

  const startTargetGenerate = async (force = false) => {
    if (!repoId) return;
    const payload = genMode === "symbol"
      ? (selectedSymbol ? { repositoryId: repoId, targetType: "symbol" as const, symbolId: selectedSymbol.id, force } : null)
      : (moduleRef.trim() ? { repositoryId: repoId, targetType: "module" as const, targetRef: moduleRef.trim(), force } : null);
    if (!payload) return;
    setError(null);
    try {
      await actions.generateTarget.mutateAsync(payload);
      if (genMode === "module") setModuleRef("");
      setOpenId(null);
    } catch (e: any) {
      setError(e.response?.data?.title || e.message);
    }
  };

  const regenerateDetail = async (doc: Documentation) => {
    setError(null);
    try {
      await actions.generateTarget.mutateAsync({
        repositoryId: doc.repositoryId,
        targetType: doc.targetType,
        targetRef: doc.targetRef,
        symbolId: doc.targetType === "symbol" && doc.targetId ? doc.targetId : undefined,
        force: true,
      });
    } catch (e: any) {
      setError(e.response?.data?.title || e.message);
    }
  };

  if (reposQ.isLoading) return <div className="skeleton" style={{ height: 240 }} />;
  if (repos.length === 0) {
    return (
      <div className="card">
        <h1>Documentation</h1>
        <p style={{ color: "var(--text-muted)" }}>No repositories yet. <Link to="/repositories">Add a repository</Link> to generate AI documentation.</p>
      </div>
    );
  }

  const selected = repos.find((r) => r.id === repoId);
  const matches = findQ.data ?? [];

  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 16, flexWrap: "wrap" }}>
        <div>
          <h1>AI Documentation</h1>
          <div style={{ color: "var(--text-muted)", fontSize: 13, marginTop: 4 }}>
            English technical docs generated from the index (symbols, relations, source). Pick a repository, then generate for the whole repo or a single target.
          </div>
        </div>
        <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
          <select value={repoId ?? ""} onChange={(e) => setRepo(e.target.value)}>
            {repos.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
          <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, color: "var(--text-muted)" }} title="Leave empty to document every documentable object (can be very slow on large repos)">
            Max targets
            <input
              type="number"
              min={1}
              value={repoLimit}
              onChange={(e) => setRepoLimit(e.target.value)}
              placeholder="all"
              style={{ width: 80 }}
              data-testid="repo-max-targets"
            />
          </label>
          <select
            value={repoStrategy}
            onChange={(e) => setRepoStrategy(e.target.value as "usage" | "kind")}
            title="How to choose which objects to document first"
            data-testid="repo-strategy"
          >
            <option value="usage">Prioritise by usage</option>
            <option value="kind">By type</option>
          </select>
          <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, color: "var(--text-muted)" }} title="Only document objects that have recorded usage">
            <input type="checkbox" checked={repoOnlyUsed} onChange={(e) => setRepoOnlyUsed(e.target.checked)} style={{ width: "auto" }} data-testid="repo-only-used" />
            Only used objects
          </label>
          <button
            className="primary"
            onClick={startRepoGenerate}
            disabled={actions.generateRepository.isPending}
            title="Generate the repository overview plus one document per documentable object (class/interface/struct/record/enum/procedure/function/view/table), skipping unchanged ones"
            data-testid="generate-repo"
          >
            {actions.generateRepository.isPending ? "Queued…" : "Generate repository docs"}
          </button>
        </div>
      </div>

      {!llmConfigured && (
        <div className="banner" style={{ marginTop: 12 }}>
          ⚠ Documentation LLM is not configured — generation will fail. Configure it in <Link to="/settings">Settings → Documentation (LLM)</Link>.
        </div>
      )}

      {docJobs.length > 0 && (
        <div style={{ marginTop: 12, display: "flex", flexDirection: "column", gap: 6, padding: "8px 12px", border: "1px solid var(--border)", borderRadius: 8, background: "rgba(96,165,250,0.06)" }}>
          {docJobs.map((j) => (
            <div key={j.id} style={{ display: "flex", alignItems: "center", gap: 10, fontSize: 13 }}>
              <span className="badge">{j.status}</span>
              <strong>Documentation</strong>
              {j.phase && <span style={{ color: "var(--text-muted)" }}>· {j.phase}</span>}
              <div style={{ flex: "0 1 240px", minWidth: 140 }}><ProgressBar value={j.progress} /></div>
              <span style={{ fontSize: 12, fontVariantNumeric: "tabular-nums", whiteSpace: "nowrap" }}>
                {j.totalItems != null && j.totalItems > 0 ? `${(j.itemsDone ?? 0).toLocaleString()} / ${j.totalItems.toLocaleString()} targets · ` : ""}
                {((j.progress ?? 0) * 100).toFixed(0)}%
              </span>
              <Link to={`/jobs?repositoryId=${repoId}`} style={{ fontSize: 12 }}>details →</Link>
            </div>
          ))}
        </div>
      )}

      {error && <div className="banner" style={{ marginTop: 12 }}>⚠ {error}</div>}
      {actions.generateRepository.isError && (
        <div className="banner" style={{ marginTop: 12 }}>⚠ {(actions.generateRepository.error as any)?.response?.data?.title || (actions.generateRepository.error as Error)?.message || "Failed to start generation"}</div>
      )}

      <div className="card" style={{ marginTop: 16 }}>
        <h3 style={{ marginTop: 0 }}>Generate for a single target</h3>
        <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 12 }}>
          Repository docs are generated per type. Use this when you want one specific symbol or one directory documented right now.
        </div>

        <div style={{ display: "flex", gap: 8, marginBottom: 12 }}>
          <button className={genMode === "symbol" ? "primary" : undefined} onClick={() => setGenMode("symbol")}>Symbol</button>
          <button className={genMode === "module" ? "primary" : undefined} onClick={() => setGenMode("module")}>Directory / module</button>
        </div>

        {genMode === "symbol" && (
          <div>
            <label style={{ display: "block", fontSize: 13, marginBottom: 4 }}>Search a symbol by name, FQN or namespace</label>
            <input
              value={selectedSymbol ? `${selectedSymbol.name}${selectedSymbol.fqn ? ` — ${selectedSymbol.fqn}` : ""}` : symbolQuery}
              onChange={(e) => { setSelectedSymbol(null); setSymbolQuery(e.target.value); }}
              placeholder="e.g. eFinServiceSubject or dbo.eFinServiceSubject"
              style={{ minWidth: 320 }}
              data-testid="symbol-search"
            />
            <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 4 }}>
              Type at least 2 characters, then click the matching symbol below. Pick the entry whose file path matches the implementation you want.
            </div>

            {!selectedSymbol && symbolQuery.trim().length > 1 && (
              <div style={{ marginTop: 8, border: "1px solid var(--border)", borderRadius: 6, maxHeight: 220, overflowY: "auto" }}>
                {findQ.isLoading && <div style={{ padding: 8, fontSize: 12, color: "var(--text-muted)" }}>Searching…</div>}
                {!findQ.isLoading && matches.length === 0 && (
                  <div style={{ padding: 8, fontSize: 12, color: "var(--text-muted)" }}>No symbols found.</div>
                )}
                {matches.map((s) => (
                  <div
                    key={s.id}
                    onClick={() => setSelectedSymbol(s)}
                    style={{ padding: "6px 10px", borderBottom: "1px solid var(--border)", cursor: "pointer" }}
                  >
                    <div style={{ fontSize: 13 }}><strong>{s.name}</strong> <span className="badge" style={{ marginLeft: 6 }}>{s.kind}</span></div>
                    <div style={{ fontFamily: "monospace", fontSize: 11, color: "var(--text-muted)" }}>{s.fqn} · {s.filePath}</div>
                  </div>
                ))}
              </div>
            )}

            {selectedSymbol && (
              <div style={{ marginTop: 8, display: "flex", alignItems: "center", gap: 8, fontSize: 12, padding: "6px 10px", border: "1px solid var(--border)", borderRadius: 6 }}>
                <span>Selected: <strong>{selectedSymbol.name}</strong> <span className="badge">{selectedSymbol.kind}</span></span>
                <span style={{ fontFamily: "monospace", color: "var(--text-muted)" }}>{selectedSymbol.filePath}</span>
                <button style={{ fontSize: 12 }} onClick={() => { setSelectedSymbol(null); setSymbolQuery(""); }}>Clear</button>
              </div>
            )}
          </div>
        )}

        {genMode === "module" && (
          <div>
            <label style={{ display: "block", fontSize: 13, marginBottom: 4 }}>Directory path relative to the repository root</label>
            <input
              value={moduleRef}
              onChange={(e) => setModuleRef(e.target.value)}
              placeholder="e.g. src/core  (use . for the repository root)"
              style={{ minWidth: 320 }}
              onKeyDown={(e) => { if (e.key === "Enter") startTargetGenerate(); }}
            />
            <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 4 }}>
              Documents the representative types in that directory and their external dependencies.
            </div>
          </div>
        )}

        <div style={{ marginTop: 12 }}>
          <button
            className="primary"
            onClick={() => startTargetGenerate(false)}
            disabled={actions.generateTarget.isPending || !canGenerateTarget}
          >
            {actions.generateTarget.isPending ? "Generating…" : "Generate documentation"}
          </button>
        </div>
      </div>

      <div className="card" style={{ marginTop: 16 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap", marginBottom: 12 }}>
          <h3 style={{ margin: 0 }}>Generated documents{selected ? ` — ${selected.name}` : ""}</h3>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <span style={{ fontSize: 13, color: "var(--text-muted)" }}>Filter by type:</span>
            <select value={targetFilter} onChange={(e) => setTargetFilter(e.target.value as any)}>
              <option value="all">All types</option>
              {TARGET_TYPES.map((t) => <option key={t} value={t}>{TARGET_TYPE_LABEL[t]}</option>)}
            </select>
          </div>
        </div>

        {listQ.isLoading ? <div className="skeleton" style={{ height: 160 }} /> : docs.length === 0 ? (
          <div style={{ color: "var(--text-muted)", fontSize: 14 }}>
            No documentation yet for {selected?.name ?? "this repository"}. Click <strong>Generate repository docs</strong> or generate a single target above.
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr><th>Type</th><th>Target</th><th>Status</th><th>Model</th><th>Updated</th><th /></tr>
            </thead>
            <tbody>
              {docs.map((d) => (
                <DocRow
                  key={d.id}
                  doc={d}
                  deleting={actions.remove.isPending}
                  onOpen={() => setOpenId(openId === d.id ? null : d.id)}
                  onDelete={() => handleDelete(d.id)}
                />
              ))}
            </tbody>
          </table>
        )}
      </div>

      {openId && (
        <div className="card" style={{ marginTop: 16 }}>
          {detailQ.isLoading ? (
            <div className="skeleton" style={{ height: 240 }} />
          ) : detailQ.isError || !detailQ.data ? (
            <div style={{ color: "var(--danger)", fontSize: 13 }}>Could not load documentation.</div>
          ) : (
            <div>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap", marginBottom: 12 }}>
                <div>
                  <h3 style={{ marginBottom: 4 }}>{detailQ.data.name}</h3>
                  <div style={{ fontSize: 12, color: "var(--text-muted)", display: "flex", gap: 10, flexWrap: "wrap", alignItems: "center" }}>
                    <span className="badge">{TARGET_TYPE_LABEL[detailQ.data.targetType] ?? detailQ.data.targetType}</span>
                    <StatusBadge status={detailQ.data.status} />
                    {detailQ.data.model && <span>model {detailQ.data.model}</span>}
                    {detailQ.data.promptVersion && <span>prompt {detailQ.data.promptVersion}</span>}
                    {detailQ.data.outputChars != null && <span>{detailQ.data.outputChars.toLocaleString()} chars</span>}
                    <span>updated {formatDate(detailQ.data.updatedAt)}</span>
                  </div>
                  {detailQ.data.targetRef && (
                    <div style={{ fontFamily: "monospace", fontSize: 11, color: "var(--text-muted)", marginTop: 4 }}>{detailQ.data.targetRef}</div>
                  )}
                  {detailQ.data.error && <div style={{ color: "var(--danger)", fontSize: 12, marginTop: 4 }}>{detailQ.data.error}</div>}
                </div>
                <div style={{ display: "flex", gap: 8 }}>
                  <button onClick={() => regenerateDetail(detailQ.data!)} disabled={actions.generateTarget.isPending}>
                    {actions.generateTarget.isPending ? "Generating…" : "Regenerate"}
                  </button>
                  <button className="danger" onClick={() => handleDelete(detailQ.data!.id)} disabled={actions.remove.isPending}>Delete</button>
                </div>
              </div>
              <pre
                style={{
                  whiteSpace: "pre-wrap",
                  wordBreak: "break-word",
                  fontFamily: "inherit",
                  fontSize: 14,
                  lineHeight: 1.6,
                  background: "var(--bg-hover)",
                  border: "1px solid var(--border)",
                  borderRadius: 8,
                  padding: 16,
                  margin: 0,
                }}
              >
                {detailQ.data.content || <em style={{ color: "var(--text-muted)" }}>No content.</em>}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
