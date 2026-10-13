import { useSearchParams, Link } from "react-router-dom";
import { Fragment, useEffect, useRef, useState } from "react";
import { useJobs, useJobActions, useRepositories } from "@/api/hooks";
import { ProgressBar } from "@/components/ProgressBar";
import { formatDate, formatDuration } from "@/utils/format";
import type { IndexingJob } from "@/api/types";

// Единица работы: для embedding-джобы — чанки (эмбеддинги), для файловых — файлы.
function jobUnit(j: IndexingJob): string {
  return j.type === "Embedding" ? "chunks" : "files";
}

function jobItemsLabel(j: IndexingJob): string {
  if (j.totalItems == null || j.totalItems <= 0) return "—";
  const done = j.itemsDone ?? 0;
  return `${done.toLocaleString()} / ${j.totalItems.toLocaleString()} ${jobUnit(j)}`;
}

// Средняя скорость с момента фактического старта выполнения (для ETA — стабильнее).
function cumulativeSpeed(j: IndexingJob): number | null {
  if (j.status !== "Running" || !j.startedAt || !j.itemsDone) return null;
  const elapsedSec = Math.max(1, (Date.now() - new Date(j.startedAt).getTime()) / 1000);
  return j.itemsDone / elapsedSec;
}

function jobEta(j: IndexingJob, speed: number | null): string {
  if (j.status !== "Running" || j.totalItems == null || j.totalItems <= 0) return "—";
  if (!speed || speed <= 0) return "—";
  const remainSec = Math.max(0, j.totalItems - (j.itemsDone ?? 0)) / speed;
  return `~${formatDuration(new Date().toISOString(), new Date(Date.now() + remainSec * 1000).toISOString())}`;
}

// Мгновенная скорость (единиц/с) по дельтам между опросами. DTO несёт только накопленный
// itemsDone, поэтому itemsDone/elapsed размывается временем ожидания в очереди и начальным
// чтением чанков из БД. Дельта между опросами показывает реальный поток обработки
// (для embedding — chunks/s, т.е. скорость обработки эмбеддингов, а не запросов).
function useJobSpeeds(jobs: IndexingJob[] | undefined): Record<string, number> {
  const prevRef = useRef<Record<string, { done: number; t: number }>>({});
  const [speeds, setSpeeds] = useState<Record<string, number>>({});

  useEffect(() => {
    if (!jobs) return;
    const now = Date.now();
    const prev = prevRef.current;
    const deltas: Record<string, number> = {};
    for (const j of jobs) {
      if (j.status !== "Running" || j.itemsDone == null) continue;
      const p = prev[j.id];
      if (p && now > p.t && j.itemsDone > p.done) {
        deltas[j.id] = (j.itemsDone - p.done) / ((now - p.t) / 1000);
      }
      prev[j.id] = { done: j.itemsDone, t: now };
    }
    setSpeeds((old) => {
      const merged: Record<string, number> = {};
      for (const j of jobs) if (j.status === "Running") merged[j.id] = deltas[j.id] ?? old[j.id] ?? 0;
      return merged;
    });
  }, [jobs]);

  return speeds;
}

function JobDetails({ j, rate }: { j: IndexingJob; rate?: number }) {
  const rows: Array<[string, React.ReactNode]> = [];
  rows.push(["Phase", j.phase ?? "—"]);
  rows.push(["Elapsed", formatDuration(j.startedAt, j.status === "Running" ? null : j.finishedAt)]);
  if (j.totalItems != null) {
    rows.push(["Items", jobItemsLabel(j)]);
    const speed = rate && rate > 0 ? rate : cumulativeSpeed(j);
    if (speed != null) rows.push(["Speed", `${speed.toFixed(1)} ${jobUnit(j)}/s`]);
    if (j.status === "Running") rows.push(["ETA", jobEta(j, speed)]);
  }
  if (j.currentFile) rows.push(["Current file", <code style={{ fontSize: 11, wordBreak: "break-all" }}>{j.currentFile}</code>]);
  if (j.fromCommit || j.toCommit) rows.push(["Range", <code style={{ fontSize: 11 }}>{(j.fromCommit ?? "∅").slice(0, 10)} → {(j.toCommit ?? "∅").slice(0, 10)}</code>]);
  if (j.error) rows.push(["Error", <span style={{ color: "var(--danger)", wordBreak: "break-word", fontSize: 12 }}>{j.error}</span>]);

  return (
    <div style={{ display: "grid", gridTemplateColumns: "160px 1fr", gap: "4px 12px", fontSize: 12, padding: "4px 8px 8px 8px" }}>
      {rows.map(([k, v]) => (
        <Fragment key={k}>
          <span style={{ color: "var(--text-muted)" }}>{k}</span>
          <span>{v}</span>
        </Fragment>
      ))}
    </div>
  );
}

export default function JobsPage() {
  const [sp, setSp] = useSearchParams();
  const repoFilter = sp.get("repositoryId") ?? "";
  const statusFilter = sp.get("status") ?? "";
  const [qRepo, setQRepo] = useState(repoFilter);
  const [qStatus, setQStatus] = useState(statusFilter);

  const jobsQ = useJobs(qRepo || null, qStatus || null);
  const reposQ = useRepositories();
  const actions = useJobActions();
  const [expanded, setExpanded] = useState<string | null>(null);
  const speeds = useJobSpeeds(jobsQ.data);

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
                <Fragment key={j.id}>
                <tr onClick={() => setExpanded(expanded === j.id ? null : j.id)} style={{ cursor: "pointer" }}>
                  <td>
                    {j.repositoryName ? <Link to={`/repositories/${j.repositoryId}`} onClick={(e) => e.stopPropagation()}>{j.repositoryName}</Link> : <span style={{ fontFamily: "monospace", fontSize: 12 }}>{j.repositoryId.slice(0, 8)}</span>}
                  </td>
                  <td>{j.type}</td>
                  <td><span className="badge">{j.status}</span></td>
                  <td style={{ whiteSpace: "nowrap", fontSize: 12 }}>{formatDate(j.startedAt)}</td>
                  <td style={{ fontSize: 12 }}>{formatDuration(j.startedAt, j.status === "Running" ? null : j.finishedAt)}</td>
                  <td style={{ minWidth: 160 }}>
                    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                      <div style={{ flex: 1 }}><ProgressBar value={j.progress} /></div>
                      <span style={{ fontSize: 12, whiteSpace: "nowrap" }}>{((j.progress ?? 0) * 100).toFixed(0)}%</span>
                      <span style={{ fontSize: 12, color: "var(--text-muted)", whiteSpace: "nowrap", fontVariantNumeric: "tabular-nums" }}>{jobItemsLabel(j)}</span>
                    </div>
                    {j.phase && (j.status === "Running" || j.status === "Queued") && (
                      <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 2 }}>phase: {j.phase}</div>
                    )}
                  </td>
                  <td style={{ maxWidth: 200, overflow: "hidden", textOverflow: "ellipsis", fontSize: 12, color: j.error ? "var(--danger)" : "var(--text-muted)" }} title={j.error ?? ""}>{j.error ?? "—"}</td>
                  <td onClick={(e) => e.stopPropagation()}>
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
                {expanded === j.id && (
                  <tr>
                    <td colSpan={8} style={{ background: "var(--bg-subtle, rgba(127,127,127,0.06))", padding: 0 }}>
                      <JobDetails j={j} rate={speeds[j.id]} />
                    </td>
                  </tr>
                )}
                </Fragment>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
