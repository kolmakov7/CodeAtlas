import { Link } from "react-router-dom";
import { useGlobalStatistics, useRepositories, useJobs, useActivity } from "@/api/hooks";
import { StatCard, SkeletonCard } from "@/components/StatCard";
import { formatBytes, formatDate } from "@/utils/format";

export default function Dashboard() {
  const statsQ = useGlobalStatistics();
  const reposQ = useRepositories();
  const jobsQ = useJobs();
  const activityQ = useActivity(10);

  if (statsQ.isLoading) {
    return (
      <div>
        <h1>Dashboard</h1>
        <div className="grid grid-5" style={{ marginTop: 16 }}>
          {Array.from({ length: 5 }).map((_, i) => <SkeletonCard key={i} />)}
        </div>
      </div>
    );
  }
  if (statsQ.isError) {
    return (
      <div>
        <h1>Dashboard</h1>
        <div className="card" style={{ marginTop: 16, borderColor: "var(--danger)" }}>
          Failed to load statistics: {(statsQ.error as Error).message}
          <div style={{ marginTop: 8 }}>
            <button onClick={() => statsQ.refetch()}>Retry</button>
          </div>
        </div>
      </div>
    );
  }
  const s = statsQ.data!;
  const running = jobsQ.data?.filter((j) => j.status === "Running") ?? [];

  return (
    <div>
      <h1 style={{ marginBottom: 4 }}>Global Dashboard</h1>
      <p style={{ color: "var(--text-muted)", marginBottom: 16 }}>Aggregated index state across all repositories</p>

      <div className="grid grid-5">
        <StatCard label="Repositories" value={s.repositories} />
        <StatCard label="Files" value={s.files} />
        <StatCard label="Symbols" value={s.symbols} />
        <StatCard label="Chunks" value={s.chunks} />
        <StatCard label="Relations" value={s.relations} />
      </div>

      <div className="grid grid-3" style={{ marginTop: 16 }}>
        <StatCard label="Embeddings Indexed" value={s.embeddingsIndexed} sub={`${s.embeddingsPending} pending · ${s.embeddingsFailed} failed`} />
        <StatCard label="Indexing Running" value={s.indexingRunning} sub={`${s.indexingQueued} queued · ${s.indexingFailed} failed`} />
        <div className="card">
          <div style={{ fontSize: 12, color: "var(--text-muted)", textTransform: "uppercase" }}>Storage</div>
          <div style={{ fontSize: 13, marginTop: 6, lineHeight: 1.6 }}>
            SQLite: <strong>{formatBytes(s.storage.sqliteSizeBytes)}</strong><br />
            Vector: <strong>{formatBytes(s.storage.vectorSizeBytes)}</strong><br />
            Repos: <strong>{formatBytes(s.storage.repositoriesSizeBytes)}</strong>
          </div>
        </div>
      </div>

      {reposQ.data?.length === 0 && (
        <div className="card" style={{ marginTop: 16, textAlign: "center", padding: 24 }}>
          <div style={{ fontSize: 16, fontWeight: 600 }}>No repositories yet → Add Repository</div>
          <p style={{ color: "var(--text-muted)", margin: "8px 0 12px" }}>Start by adding a Git repository to index.</p>
          <Link to="/repositories"><button className="primary">Go to Repositories</button></Link>
        </div>
      )}

      <div className="grid grid-2" style={{ marginTop: 16 }}>
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Recent activity</h3>
          {activityQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : activityQ.data?.length ? (
            <table className="table">
              <thead><tr><th>At</th><th>Action</th><th>Repository</th></tr></thead>
              <tbody>
                {activityQ.data.slice(0, 8).map((a) => (
                  <tr key={a.id}>
                    <td style={{ whiteSpace: "nowrap" }}>{formatDate(a.at)}</td>
                    <td>{a.action}</td>
                    <td>{a.repositoryName ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div style={{ color: "var(--text-muted)", fontSize: 14 }}>{s.recentActivity?.length ? s.recentActivity.map(a => a.action).join(", ") : "No activity yet"}</div>
          )}
        </div>
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Recent errors</h3>
          {s.recentErrors?.length ? (
            <table className="table">
              <thead><tr><th>At</th><th>File</th><th>Message</th></tr></thead>
              <tbody>
                {s.recentErrors.slice(0, 8).map((e) => (
                  <tr key={e.id}>
                    <td style={{ whiteSpace: "nowrap" }}>{formatDate(e.at)}</td>
                    <td style={{ maxWidth: 180, overflow: "hidden", textOverflow: "ellipsis" }}>{e.filePath ?? "—"}</td>
                    <td style={{ maxWidth: 220, overflow: "hidden", textOverflow: "ellipsis" }} title={e.message}>{e.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No errors — all clear.</div>}
        </div>
      </div>

      {running.length > 0 && (
        <div className="card" style={{ marginTop: 16 }}>
          <h3>Running jobs</h3>
          <table className="table" style={{ marginTop: 8 }}>
            <thead><tr><th>Repository</th><th>Type</th><th>Progress</th></tr></thead>
            <tbody>
              {running.map((j) => (
                <tr key={j.id}>
                  <td>{j.repositoryName ?? j.repositoryId.slice(0, 8)}</td>
                  <td>{j.type}</td>
                  <td style={{ minWidth: 140 }}><div style={{ display: "flex", alignItems: "center", gap: 8 }}><div className="progress" style={{ flex: 1 }}><div style={{ width: `${(j.progress ?? 0) * 100}%` }} /></div><span style={{ fontSize: 12 }}>{((j.progress ?? 0) * 100).toFixed(0)}%</span></div></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
