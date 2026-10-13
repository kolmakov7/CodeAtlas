import { useSearchParams, Link } from "react-router-dom";
import { useMemo, useState } from "react";
import {
  useRepositories,
  useDocumentationList,
  useDocumentationDetail,
  useDocumentationActions,
  useJobs,
} from "@/api/hooks";
import { ProgressBar } from "@/components/ProgressBar";
import { formatDate } from "@/utils/format";
import type { Documentation, DocumentationTargetType } from "@/api/types";

const STATUS_ORDER: Record<string, number> = {
  Generated: 0,
  Stale: 1,
  Generating: 2,
  Failed: 3,
  NotGenerated: 4,
};

const TARGET_TYPES: DocumentationTargetType[] = ["Repository", "Module", "Symbol"];

function DocRow({ doc, onOpen, onDelete, deleting }: { doc: Documentation; onOpen: () => void; onDelete: () => void; deleting: boolean }) {
  return (
    <tr style={{ cursor: "pointer" }} onClick={onOpen}>
      <td>
        <span className="badge">{doc.targetType}</span>
      </td>
      <td style={{ fontFamily: "monospace", fontSize: 13, wordBreak: "break-all" }}>{doc.title}</td>
      <td><span className={`badge ${doc.status.toLowerCase()}`}>{doc.status}</span></td>
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

  const paramRepo = searchParams.get("repositoryId");
  const repoId = paramRepo && repos.some((r) => r.id === paramRepo)
    ? paramRepo
    : (paramRepo ?? repos[0]?.id);

  const [targetFilter, setTargetFilter] = useState<DocumentationTargetType | "all">("all");
  const [openId, setOpenId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // single-target generation form
  const [genType, setGenType] = useState<DocumentationTargetType>("Symbol");
  const [genRef, setGenRef] = useState("");

  const listQ = useDocumentationList(repoId, targetFilter === "all" ? undefined : targetFilter);
  const detailQ = useDocumentationDetail(openId ?? undefined);
  const jobsQ = useJobs(repoId, null);

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
    actions.generateRepository.mutate(repoId, {
      onError: (e: any) => setError(e.response?.data?.title || e.message),
    });
  };

  const startTargetGenerate = async () => {
    if (!repoId || !genRef.trim()) return;
    setError(null);
    try {
      await actions.generateTarget.mutateAsync({ repositoryId: repoId, targetType: genType, targetRef: genRef.trim() });
      setGenRef("");
      setOpenId(null);
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

  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 16, flexWrap: "wrap" }}>
        <div>
          <h1>AI Documentation</h1>
          <div style={{ color: "var(--text-muted)", fontSize: 13, marginTop: 4 }}>
            Generated from the code index (symbols, relations, source). All generated content is in English.
          </div>
        </div>
        <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
          <select value={repoId ?? ""} onChange={(e) => setRepo(e.target.value)}>
            {repos.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
          <button className="primary" onClick={startRepoGenerate} disabled={actions.generateRepository.isPending}>
            {actions.generateRepository.isPending ? "Queued…" : "Generate all"}
          </button>
        </div>
      </div>

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
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap", marginBottom: 12 }}>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <span style={{ fontSize: 13, color: "var(--text-muted)" }}>Filter:</span>
            <select value={targetFilter} onChange={(e) => setTargetFilter(e.target.value as any)}>
              <option value="all">All types</option>
              {TARGET_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
          </div>
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <span style={{ fontSize: 13, color: "var(--text-muted)" }}>Generate target:</span>
            <select value={genType} onChange={(e) => setGenType(e.target.value as DocumentationTargetType)}>
              {TARGET_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
            <input
              value={genRef}
              onChange={(e) => setGenRef(e.target.value)}
              placeholder={genType === "Module" ? "module path, e.g. src/core" : genType === "Symbol" ? "symbol name / FQN" : "— (repo)"}
              style={{ minWidth: 240 }}
              onKeyDown={(e) => { if (e.key === "Enter") startTargetGenerate(); }}
            />
            <button onClick={startTargetGenerate} disabled={actions.generateTarget.isPending || !genRef.trim() || genType === "Repository"}>
              {actions.generateTarget.isPending ? "Generating…" : "Generate"}
            </button>
          </div>
        </div>

        {listQ.isLoading ? <div className="skeleton" style={{ height: 160 }} /> : docs.length === 0 ? (
          <div style={{ color: "var(--text-muted)", fontSize: 14 }}>
            No documentation yet for {selected?.name ?? "this repository"}. Click <strong>Generate all</strong> or generate a single target above.
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr><th>Type</th><th>Target</th><th>Status</th><th>Updated</th><th /></tr>
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
                  <h3 style={{ marginBottom: 4 }}>{detailQ.data.title}</h3>
                  <div style={{ fontSize: 12, color: "var(--text-muted)" }}>
                    <span className="badge">{detailQ.data.targetType}</span>
                    <span style={{ marginLeft: 8 }}>
                      Updated {formatDate(detailQ.data.updatedAt)}
                      {detailQ.data.version != null ? ` · v${detailQ.data.version}` : ""}
                    </span>
                  </div>
                </div>
                <button className="danger" onClick={() => handleDelete(detailQ.data.id)} disabled={actions.remove.isPending}>Delete</button>
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
