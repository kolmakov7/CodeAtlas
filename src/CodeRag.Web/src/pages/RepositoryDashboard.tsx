import { useParams, Link } from "react-router-dom";
import { useState } from "react";
import { useRepository, useRepositoryStatistics, useRepositoryActions, useJobs } from "@/api/hooks";
import { StatCard } from "@/components/StatCard";
import { ConfirmModal } from "@/components/ConfirmModal";
import { ProgressBar } from "@/components/ProgressBar";
import { formatDate } from "@/utils/format";

export default function RepositoryDashboard() {
  const { id } = useParams<{ id: string }>();
  const repoQ = useRepository(id);
  const statsQ = useRepositoryStatistics(id);
  const jobsQ = useJobs(id);
  const actions = useRepositoryActions();
  const [deleteOpen, setDeleteOpen] = useState(false);

  if (repoQ.isLoading) return <div className="skeleton" style={{ height: 200 }} />;
  if (repoQ.isError || !repoQ.data) return <div className="card">Repository not found. <Link to="/repositories">Back</Link></div>;
  const r = repoQ.data;
  const s = statsQ.data;

  const busy = r.status === "Syncing" || r.status === "Indexing";
  return (
    <div>
      <Link to="/repositories" style={{ fontSize: 13 }}>← All repositories</Link>
      <div className="card" style={{ marginTop: 12 }}>
        <div style={{ display: "flex", justifyContent: "space-between", gap: 16, flexWrap: "wrap" }}>
          <div>
            <h1 style={{ display: "flex", alignItems: "center", gap: 8 }}>{r.name} <span className={`badge ${r.status.toLowerCase()}`}>{r.status}</span></h1>
            <div style={{ color: "var(--text-muted)", fontSize: 13, marginTop: 4, wordBreak: "break-all" }}>
              <div>Git: <code>{r.gitUrl}</code> · Branch: <code>{r.branch}</code></div>
              <div>HEAD: <code>{r.currentCommit ?? "—"}</code> · Indexed: <code>{r.indexedCommit ?? "—"}</code></div>
              <div>LastSync: {formatDate(r.lastSyncAt)} · LastIndex: {formatDate(r.lastIndexAt)}</div>
              {r.lastError && <div style={{ color: "var(--danger)", marginTop: 4 }}>Error: {r.lastError}</div>}
            </div>
          </div>
          <div style={{ display: "flex", gap: 8, alignItems: "flex-start", flexWrap: "wrap" }}>
            <button onClick={() => actions.sync.mutate(r.id)} disabled={busy || actions.sync.isPending}>Sync</button>
            <button onClick={() => actions.reindex.mutate(r.id)} disabled={busy || actions.reindex.isPending}>Re-index</button>
            {r.status !== "Paused" ? (
              <button onClick={() => actions.pause.mutate(r.id)} disabled={actions.pause.isPending}>Pause</button>
            ) : (
              <button onClick={() => actions.resume.mutate(r.id)} disabled={actions.resume.isPending} className="primary">Resume</button>
            )}
            <button className="danger" onClick={() => setDeleteOpen(true)}>Delete</button>
          </div>
        </div>
      </div>

      <div className="grid grid-5" style={{ marginTop: 16 }}>
        <StatCard label="Files" value={s?.fileCount ?? "—"} />
        <StatCard label="Symbols" value={s?.symbolCount ?? "—"} />
        <StatCard label="Chunks" value={s?.chunkCount ?? "—"} />
        <StatCard label="Relations" value={"—"} />
        <StatCard label="Embeddings" value={s ? `${s.vectorCount}/${s.chunkCount}` : "—"} sub={s ? `vectors / chunks` : undefined} />
      </div>

      {s?.languageBreakdown && Object.keys(s.languageBreakdown).length > 0 && (
        <div className="card" style={{ marginTop: 16 }}>
          <h3 style={{ marginBottom: 8 }}>Language breakdown</h3>
          <div style={{ display: "flex", gap: 8, alignItems: "stretch", height: 24, borderRadius: 6, overflow: "hidden", border: "1px solid var(--border)" }}>
            {(() => {
              const total = Object.values(s.languageBreakdown).reduce((a, b) => a + b, 0) || 1;
              const colors: Record<string, string> = { "C#": "#178600", "Python": "#3572A5", "JavaScript": "#f1e05a", "Go": "#00ADD8", "C++": "#f34b7d", "SQL": "#e38c00", "VBS": "#818181" };
              return Object.entries(s.languageBreakdown).map(([lang, cnt]) => (
                <div key={lang} title={`${lang}: ${cnt}`} style={{ width: `${(cnt / total) * 100}%`, background: colors[lang] ?? "var(--accent)", display: "flex", alignItems: "center", justifyContent: "center", fontSize: 10, color: lang === "JavaScript" ? "#000" : "#fff" }}>
                  {cnt > total * 0.08 ? lang : ""}
                </div>
              ));
            })()}
          </div>
          <div style={{ display: "flex", gap: 12, flexWrap: "wrap", marginTop: 8, fontSize: 12, color: "var(--text-muted)" }}>
            {Object.entries(s.languageBreakdown).map(([k, v]) => <span key={k}><strong style={{ color: "var(--text)" }}>{k}</strong> {v}</span>)}
          </div>
        </div>
      )}

      <div className="grid grid-2" style={{ marginTop: 16 }}>
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Recent jobs</h3>
          {jobsQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : jobsQ.data?.length ? (
            <table className="table">
              <thead><tr><th>Type</th><th>Status</th><th>Progress</th><th>Duration</th></tr></thead>
              <tbody>
                {jobsQ.data.slice(0, 8).map((j) => (
                  <tr key={j.id}>
                    <td>{j.type}</td>
                    <td><span className="badge">{j.status}</span></td>
                    <td style={{ minWidth: 100 }}><ProgressBar value={j.progress} /></td>
                    <td style={{ fontSize: 12, color: "var(--text-muted)" }}>{j.startedAt ? new Date(j.finishedAt ?? new Date().toISOString()).getTime() - new Date(j.startedAt).getTime() + "ms" : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No jobs yet</div>}
        </div>
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Shortcuts</h3>
          <div style={{ display: "flex", flexDirection: "column", gap: 8, fontSize: 14 }}>
            <Link to={`/search?repositoryId=${r.id}`}>Search in this repository →</Link>
            <Link to={`/jobs?repositoryId=${r.id}`}>View all jobs for this repo →</Link>
          </div>
        </div>
      </div>

      <ConfirmModal
        open={deleteOpen}
        title="Delete repository?"
        message={`This will delete repository "${r.name}" and all its index data (files, symbols, chunks, relations, vectors, FTS). This action cannot be undone.`}
        confirmLabel="Delete"
        danger
        showCheckoutOption
        onClose={() => setDeleteOpen(false)}
        onConfirm={(deleteCheckout) => {
          actions.remove.mutate({ id: r.id, deleteCheckout });
          setDeleteOpen(false);
        }}
      />
    </div>
  );
}
