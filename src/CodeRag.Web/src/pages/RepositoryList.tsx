import { Link } from "react-router-dom";
import { useRepositories } from "@/api/hooks";
import { formatDate } from "@/utils/format";

export default function RepositoryList() {
  const q = useRepositories();
  if (q.isLoading) return <div className="skeleton" style={{ height: 200 }} />;
  if (q.isError) return <div className="card" style={{ borderColor: "var(--danger)" }}>Error: {(q.error as Error).message} <button onClick={() => q.refetch()} style={{ marginLeft: 8 }}>Retry</button></div>;
  const repos = q.data ?? [];
  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 16 }}>
        <h1>Repositories</h1>
        <span style={{ color: "var(--text-muted)", fontSize: 13 }}>{repos.length} total</span>
      </div>
      {repos.length === 0 ? (
        <div className="card" style={{ textAlign: "center", padding: 32 }}>
          <div style={{ fontSize: 18, fontWeight: 600 }}>No repositories yet</div>
          <p style={{ color: "var(--text-muted)", margin: "8px 0 16px" }}>Add a Git repository via API: <code>POST /api/repositories</code></p>
          <div style={{ fontSize: 12, color: "var(--text-muted)" }}>Web add-form will be available when API supports creation via UI.</div>
        </div>
      ) : (
        <div className="grid" style={{ gap: 12 }}>
          {repos.map((r) => (
            <Link key={r.id} to={`/repositories/${r.id}`} style={{ textDecoration: "none", color: "inherit" }}>
              <div className="card" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div>
                  <div style={{ fontWeight: 600, display: "flex", alignItems: "center", gap: 8 }}>
                    {r.name} <span className={`badge ${r.status.toLowerCase()}`}>{r.status}</span> {!r.enabled && <span className="badge">disabled</span>}
                  </div>
                  <div style={{ fontSize: 13, color: "var(--text-muted)", marginTop: 4 }}>
                    {r.gitUrl} · {r.branch} · {r.currentCommit?.slice(0, 7) ?? "—"} → {r.indexedCommit?.slice(0, 7) ?? "—"}
                  </div>
                </div>
                <div style={{ textAlign: "right", fontSize: 12, color: "var(--text-muted)" }}>
                  <div>Last sync: {formatDate(r.lastSyncAt)}</div>
                  <div>Last index: {formatDate(r.lastIndexAt)}</div>
                </div>
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
