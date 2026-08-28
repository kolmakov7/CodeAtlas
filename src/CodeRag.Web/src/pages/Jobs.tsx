import { useSearchParams, Link } from "react-router-dom";
import { useState } from "react";
import { useJobs, useJobActions, useRepositories } from "@/api/hooks";
import { ProgressBar } from "@/components/ProgressBar";
import { formatDate, formatDuration } from "@/utils/format";

export default function JobsPage() {
  const [sp, setSp] = useSearchParams();
  const repoFilter = sp.get("repositoryId") ?? "";
  const statusFilter = sp.get("status") ?? "";
  const [qRepo, setQRepo] = useState(repoFilter);
  const [qStatus, setQStatus] = useState(statusFilter);

  const jobsQ = useJobs(qRepo || null, qStatus || null);
  const reposQ = useRepositories();
  const actions = useJobActions();

  const apply = () => {
    const p = new URLSearchParams();
    if (qRepo) p.set("repositoryId", qRepo);
    if (qStatus) p.set("status", qStatus);
    setSp(p);
  };

  return (
    <div>
      <h1 style={{ marginBottom: 12 }}>Jobs</h1>
      <div className="card" style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
        <label style={{ flex: 1, minWidth: 180 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Repository</div>
          <select value={qRepo} onChange={(e) => setQRepo(e.target.value)}>
            <option value="">All repositories</option>
            {reposQ.data?.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
        </label>
        <label style={{ flex: 1, minWidth: 160 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Status</div>
          <select value={qStatus} onChange={(e) => setQStatus(e.target.value)}>
            <option value="">All</option>
            {["Queued", "Running", "Completed", "Failed", "Canceled"].map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </label>
        <button className="primary" onClick={apply} style={{ height: 36 }}>Filter</button>
        <span style={{ fontSize: 12, color: "var(--text-muted)", alignSelf: "center" }}>polling every 2s</span>
      </div>

      <div className="card" style={{ marginTop: 16, padding: 0, overflow: "auto" }}>
        {jobsQ.isLoading ? <div className="skeleton" style={{ height: 200, margin: 16 }} /> : jobsQ.isError ? (
          <div style={{ padding: 16, color: "var(--danger)" }}>Failed: {(jobsQ.error as Error).message} <button onClick={() => jobsQ.refetch()} style={{ marginLeft: 8 }}>Retry</button></div>
        ) : !jobsQ.data?.length ? (
          <div style={{ padding: 24, textAlign: "center", color: "var(--text-muted)" }}>No jobs matching filter.</div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>Repository</th>
                <th>Type</th>
                <th>Status</th>
                <th>Start</th>
                <th>Duration</th>
                <th>Progress</th>
                <th>Error</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {jobsQ.data.map((j) => (
                <tr key={j.id}>
                  <td>
                    {j.repositoryName ? <Link to={`/repositories/${j.repositoryId}`}>{j.repositoryName}</Link> : <span style={{ fontFamily: "monospace", fontSize: 12 }}>{j.repositoryId.slice(0, 8)}</span>}
                  </td>
                  <td>{j.type}</td>
                  <td><span className="badge">{j.status}</span></td>
                  <td style={{ whiteSpace: "nowrap", fontSize: 12 }}>{formatDate(j.startedAt)}</td>
                  <td style={{ fontSize: 12 }}>{formatDuration(j.startedAt, j.finishedAt)}</td>
                  <td style={{ minWidth: 120 }}><ProgressBar value={j.progress} /></td>
                  <td style={{ maxWidth: 200, overflow: "hidden", textOverflow: "ellipsis", fontSize: 12, color: j.error ? "var(--danger)" : "var(--text-muted)" }} title={j.error ?? ""}>{j.error ?? "—"}</td>
                  <td>
                    <div style={{ display: "flex", gap: 6 }}>
                      {(j.status === "Running" || j.status === "Queued") && (
                        <button onClick={() => actions.cancel.mutate(j.id)} disabled={actions.cancel.isPending} style={{ fontSize: 12, padding: "4px 8px" }}>Cancel</button>
                      )}
                      {j.status === "Failed" && (
                        <button onClick={() => actions.retry.mutate(j.id)} disabled={actions.retry.isPending} className="primary" style={{ fontSize: 12, padding: "4px 8px" }}>Retry</button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
