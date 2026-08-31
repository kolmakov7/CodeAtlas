import { Link } from "react-router-dom";
import { useGlobalStatistics, useRepositories, useJobs, useActivity, useDiagnostics, useRepositoryActions } from "@/api/hooks";
import { StatCard, SkeletonCard } from "@/components/StatCard";
import { formatBytes, formatDate } from "@/utils/format";

export default function Dashboard() {
  const statsQ = useGlobalStatistics();
  const reposQ = useRepositories();
  const jobsQ = useJobs();
  const activityQ = useActivity(10);
  const diagQ = useDiagnostics();
  const { rebuildAll } = useRepositoryActions();
  const staleIndex = diagQ.data?.vectorIndex?.stale === true;

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
  const sRaw: any = statsQ.data!;
  const repoStatsArray = Array.isArray(sRaw.repositories) ? sRaw.repositories : [];
  const repoCount = typeof sRaw.repositories === 'number' ? sRaw.repositories : repoStatsArray.length;
  // Per-domain embedding stats (code + text) from the dual-embedding API.
  const embStatsRaw: any[] = Array.isArray(sRaw.embeddingStats) ? sRaw.embeddingStats : [];
  const embByDomain = (d: string) => embStatsRaw.find((x) => String(x.domain).toLowerCase() === d);
  const embCode = embByDomain("code");
  const embText = embByDomain("text");
  const embeddingsIndexed = embStatsRaw.reduce((a: number, d: any) => a + (Number(d.indexed) || 0), 0);
  const embeddingsPending = embStatsRaw.reduce((a: number, d: any) => a + (Number(d.pending) || 0), 0);
  // API returns RepositoryCount/FileCount... but Web expects repositories/files — handle both
  const s = {
    repositories: repoCount,
    files: sRaw.files ?? sRaw.fileCount ?? repoStatsArray.reduce((a: number, r: any) => a + (r.files ?? r.FileCount ?? 0), 0),
    symbols: sRaw.symbols ?? sRaw.symbolCount ?? repoStatsArray.reduce((a: number, r: any) => a + (r.symbols ?? r.SymbolCount ?? 0), 0),
    chunks: sRaw.chunks ?? sRaw.chunkCount ?? repoStatsArray.reduce((a: number, r: any) => a + (r.chunks ?? r.ChunkCount ?? 0), 0),
    relations: sRaw.relations ?? 0,
    embeddingsIndexed: embeddingsIndexed || (sRaw.embeddingsIndexed ?? sRaw.vectorCount ?? repoStatsArray.reduce((a: number, r: any) => a + (r.VectorCount ?? 0), 0)),
    embeddingsPending,
    embeddingsCode: embCode?.indexed ?? null,
    embeddingsText: embText?.indexed ?? null,
    embeddingsFailed: sRaw.embeddingsFailed ?? 0,
    indexingRunning: sRaw.indexingRunning ?? 0,
    indexingQueued: sRaw.indexingQueued ?? 0,
    indexingFailed: sRaw.indexingFailed ?? 0,
    storage: sRaw.storage ?? { sqliteSizeBytes: 0, vectorSizeBytes: 0, repositoriesSizeBytes: 0 },
    recentActivity: sRaw.recentActivity ?? [],
    recentErrors: sRaw.recentErrors ?? [],
  } as any;
  const running = jobsQ.data?.filter((j) => j.status === "Running") ?? [];

  return (
    <div>
      <h1 style={{ marginBottom: 4 }}>Global Dashboard</h1>
      <p style={{ color: "var(--text-muted)", marginBottom: 16 }}>Aggregated index state across all repositories</p>

      {staleIndex && (
        <div className="card" style={{ marginBottom: 16, borderColor: "var(--danger)", background: "color-mix(in srgb, var(--danger) 12%, transparent)" }}>
          <div style={{ fontWeight: 600, color: "var(--danger)", fontSize: 14 }}>Vector index is stale</div>
          <div style={{ fontSize: 13, marginTop: 4, color: "var(--text-muted)" }}>
            {diagQ.data?.vectorIndex?.reason ?? "Stored vectors don't match the current embedding model."}
          </div>
          <div style={{ marginTop: 10, display: "flex", gap: 8, alignItems: "center" }}>
            <button
              className="primary"
              disabled={rebuildAll.isPending}
              onClick={() => {
                if (window.confirm("Rebuild the vector index for all repositories? This re-embeds every chunk with the current model.")) {
                  rebuildAll.mutate();
                }
              }}
            >
              {rebuildAll.isPending ? "Rebuilding…" : "Rebuild all"}
            </button>
            {rebuildAll.isError && <span style={{ fontSize: 13, color: "var(--danger)" }}>Failed to start rebuild.</span>}
          </div>
        </div>
      )}

      <div className="grid grid-5">
        <StatCard label="Repositories" value={s.repositories} />
        <StatCard label="Files" value={s.files} />
        <StatCard label="Symbols" value={s.symbols} />
        <StatCard label="Chunks" value={s.chunks} />
        <StatCard label="Relations" value={s.relations} />
      </div>

      <div className="grid grid-3" style={{ marginTop: 16 }}>
        <StatCard label="Embeddings Indexed" value={s.embeddingsIndexed} sub={`code ${s.embeddingsCode ?? "—"} · text ${s.embeddingsText ?? "—"} · ${s.embeddingsPending} pending · ${s.embeddingsFailed} failed`} />
        <StatCard label="Indexing Running" value={s.indexingRunning} sub={`${s.indexingQueued} queued · ${s.indexingFailed} failed`} />
        <div className="card">
          <div style={{ fontSize: 12, color: "var(--text-muted)", textTransform: "uppercase" }}>Storage</div>
          <div style={{ fontSize: 13, marginTop: 6, lineHeight: 1.6 }}>
            SQLite: <strong>{formatBytes(s.storage?.sqliteSizeBytes ?? 0)}</strong><br />
            Vector: <strong>{formatBytes(s.storage?.vectorSizeBytes ?? 0)}</strong><br />
            Repos: <strong>{formatBytes(s.storage?.repositoriesSizeBytes ?? 0)}</strong>
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
            <div style={{ color: "var(--text-muted)", fontSize: 14 }}>{s.recentActivity?.length ? (s.recentActivity as any[]).map((a: any) => a.action).join(", ") : "No activity yet"}</div>
          )}
        </div>
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Recent errors</h3>
          {s.recentErrors?.length ? (
            <table className="table">
              <thead><tr><th>At</th><th>File</th><th>Message</th></tr></thead>
              <tbody>
                {(s.recentErrors as any[]).slice(0, 8).map((e: any) => (
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
