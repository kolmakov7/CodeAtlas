import { useMemo, useState } from "react";
import { useRevisionStatus, useRevisionJobs, useRevisionJob, useRevisionActions } from "@/api/hooks";
import { formatDate } from "@/utils/format";
import type { ConsolidationJobStatus, ConsolidationJobType } from "@/api/types";

const JOB_STATUSES: ConsolidationJobStatus[] = [
  "Pending",
  "ReadyForAnalysis",
  "AwaitingLlm",
  "ReadyForReview",
  "Accepted",
  "Rejected",
  "Cancelled",
  "Error",
  "Postponed",
  "Undone",
];

const JOB_TYPES: ConsolidationJobType[] = ["Duplicates", "Contradiction", "MissingSource", "Stale", "Consolidate"];

function errText(e: any): string {
  return e?.response?.data?.detail || e?.response?.data?.title || e?.message || "Request failed";
}

function jobColor(status: ConsolidationJobStatus): string {
  if (status === "ReadyForReview") return "var(--accent)";
  if (status === "Accepted") return "var(--success)";
  if (status === "Error") return "var(--danger)";
  if (status === "AwaitingLlm" || status === "Postponed") return "var(--warning)";
  if (status === "Rejected" || status === "Cancelled" || status === "Undone") return "var(--text-muted)";
  return "var(--text)";
}

function prettyJson(value?: string | null): string {
  if (!value) return "";
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

export default function ReviewPage() {
  const statusQ = useRevisionStatus();
  const actions = useRevisionActions();

  const [statusFilter, setStatusFilter] = useState("");
  const [typeFilter, setTypeFilter] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [dryRun, setDryRun] = useState(true);
  const [actor, setActor] = useState("ui");
  const [rejectReason, setRejectReason] = useState("");
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [acting, setActing] = useState(false);
  const [runResult, setRunResult] = useState<{ scanned: number; jobsCreated: number; jobsSkipped: number; warnings: string[] } | null>(null);

  const jobsParams = useMemo(
    () => ({
      status: (statusFilter || undefined) as ConsolidationJobStatus | undefined,
      type: (typeFilter || undefined) as ConsolidationJobType | undefined,
      limit: 200,
    }),
    [statusFilter, typeFilter],
  );

  const jobsQ = useRevisionJobs(jobsParams);
  const jobQ = useRevisionJob(selectedId ?? undefined);
  const detail = jobQ.data;
  const job = detail?.job;
  const llmConfigured = statusQ.data?.llm.configured ?? false;

  const resetMessages = () => {
    setError(null);
    setNotice(null);
  };

  const runRevision = async () => {
    resetMessages();
    setRunResult(null);
    setActing(true);
    try {
      const result = await actions.run.mutateAsync({ dryRun });
      setRunResult(result);
      setNotice(dryRun ? "Dry run completed." : "Revision run completed.");
    } catch (e: any) {
      setError(errText(e));
    } finally {
      setActing(false);
    }
  };

  const act = async (label: string, fn: () => Promise<any>) => {
    resetMessages();
    setActing(true);
    try {
      const result = await fn();
      if (result && typeof result.success === "boolean" && !result.success) {
        setError(`${label}: ${result.error ?? "failed"}`);
      } else {
        setNotice(`${label}: ${result?.status ?? "ok"}`);
      }
    } catch (e: any) {
      setError(errText(e));
    } finally {
      setActing(false);
    }
  };

  const analyze = async () => {
    if (!job) return;
    resetMessages();
    setActing(true);
    try {
      const result = await actions.analyze.mutateAsync({ id: job.id, actor: actor.trim() || undefined });
      if (!result.llmConfigured) {
        setNotice("Dependency: LLM not configured — analysis is unavailable, job status is " + result.status + ".");
      } else if (result.error) {
        setError(result.error);
      } else {
        setNotice(`Analysis: ${result.status}`);
      }
    } catch (e: any) {
      setError(errText(e));
    } finally {
      setActing(false);
    }
  };

  const runLabel = (label: string) => (acting ? "Working…" : label);

  return (
    <div>
      <h1 style={{ marginBottom: 4 }}>Review</h1>
      <div style={{ color: "var(--text-muted)", fontSize: 13, marginBottom: 12 }}>
        Consolidation queue, LLM analysis and manual apply of knowledge revisions.
      </div>

      {notice && <div className="banner" style={{ marginBottom: 12 }}>{notice}</div>}
      {error && <div className="banner" style={{ marginBottom: 12 }}>⚠ {error}</div>}

      <div className="card">
        {statusQ.isLoading ? (
          <div className="skeleton" style={{ height: 120 }} />
        ) : statusQ.isError ? (
          <div style={{ color: "var(--danger)", fontSize: 13 }}>
            Failed to load revision status: {(statusQ.error as Error).message}
            <button onClick={() => statusQ.refetch()} style={{ marginLeft: 8, fontSize: 12 }}>Retry</button>
          </div>
        ) : (
          <div>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
              <h3 style={{ margin: 0 }}>Revision status</h3>
              <div style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 12 }}>
                <span style={{ color: "var(--text-muted)" }}>LLM</span>
                <span
                  className="badge"
                  style={{
                    color: llmConfigured ? "var(--success)" : "var(--warning)",
                    borderColor: llmConfigured ? "var(--success)" : "var(--warning)",
                  }}
                >
                  {llmConfigured ? "configured" : "not configured"}
                </span>
                <span style={{ color: "var(--text-muted)" }}>({statusQ.data?.llm.status})</span>
              </div>
            </div>
            {!llmConfigured && (
              <div
                style={{
                  marginTop: 8,
                  padding: "6px 10px",
                  border: "1px dashed var(--warning)",
                  borderRadius: 6,
                  fontSize: 12,
                  color: "var(--text-muted)",
                }}
              >
                Dependency: <strong style={{ color: "var(--warning)" }}>LLM not configured</strong> — analysis is
                disabled until an LLM endpoint is set. Queue, review and apply stay available.
              </div>
            )}
            <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 10, marginTop: 12 }}>
              {[
                ["Ready for analysis", statusQ.data?.queue.readyForAnalysis ?? 0],
                ["Awaiting LLM", statusQ.data?.queue.awaitingLlm ?? 0],
                ["Ready for review", statusQ.data?.queue.readyForReview ?? 0],
                ["Errors", statusQ.data?.queue.errors ?? 0],
              ].map(([label, value]) => (
                <div key={String(label)} style={{ background: "var(--bg-hover)", borderRadius: 6, padding: "8px 10px" }}>
                  <div style={{ fontSize: 11, color: "var(--text-muted)", textTransform: "uppercase" }}>{label}</div>
                  <div style={{ fontSize: 22, fontWeight: 700 }}>{value}</div>
                </div>
              ))}
            </div>
            <div style={{ fontSize: 12, color: "var(--text-muted)", marginTop: 10 }}>
              {statusQ.data?.lastRun ? (
                <>
                  Last run <strong>{statusQ.data.lastRun.status}</strong> · trigger {statusQ.data.lastRun.trigger} ·{" "}
                  {formatDate(statusQ.data.lastRun.startedAt)}
                  {statusQ.data.lastRun.finishedAt ? ` → ${formatDate(statusQ.data.lastRun.finishedAt)}` : ""}
                  {statusQ.data.lastRun.stats ? ` · ${statusQ.data.lastRun.stats}` : ""}
                  {statusQ.data.lastRun.error ? (
                    <span style={{ color: "var(--danger)" }}> · {statusQ.data.lastRun.error}</span>
                  ) : null}
                </>
              ) : (
                "No revision runs yet."
              )}
            </div>
          </div>
        )}
      </div>

      <div className="card" style={{ marginTop: 16, display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
        <div style={{ flex: 1, minWidth: 180 }}>
          <h3 style={{ margin: 0 }}>Run revision</h3>
          <div style={{ fontSize: 12, color: "var(--text-muted)" }}>
            Deterministic inventory of duplicates, contradictions, missing sources and stale entries.
          </div>
        </div>
        <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, color: "var(--text-muted)" }}>
          <input type="checkbox" checked={dryRun} onChange={(e) => setDryRun(e.target.checked)} style={{ width: "auto" }} />
          Dry run
        </label>
        <button className="primary" onClick={runRevision} disabled={acting} data-testid="run-revision">
          {runLabel("Run revision")}
        </button>
      </div>

      {runResult && (
        <div className="card" style={{ marginTop: 12, fontSize: 13 }}>
          Scanned <strong>{runResult.scanned}</strong> · created <strong>{runResult.jobsCreated}</strong> · skipped{" "}
          <strong>{runResult.jobsSkipped}</strong>
          {runResult.warnings.length > 0 && (
            <div style={{ marginTop: 6, color: "var(--warning)", fontSize: 12 }}>{runResult.warnings.join(" · ")}</div>
          )}
        </div>
      )}

      <div className="card" style={{ marginTop: 16, display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
        <label style={{ flex: 1, minWidth: 160 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Status</div>
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">All statuses</option>
            {JOB_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </label>
        <label style={{ flex: 1, minWidth: 160 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Type</div>
          <select value={typeFilter} onChange={(e) => setTypeFilter(e.target.value)}>
            <option value="">All types</option>
            {JOB_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </label>
        <span style={{ fontSize: 12, color: "var(--text-muted)", paddingBottom: 8 }}>
          {jobsQ.data?.length ?? 0} job{jobsQ.data?.length === 1 ? "" : "s"} · polling every 5s
        </span>
      </div>

      <div className="card" style={{ marginTop: 12, padding: 0, overflow: "auto" }}>
        {jobsQ.isLoading ? (
          <div className="skeleton" style={{ height: 160, margin: 12 }} />
        ) : jobsQ.isError ? (
          <div style={{ padding: 16, color: "var(--danger)", fontSize: 13 }}>
            Failed: {(jobsQ.error as Error).message}
            <button onClick={() => jobsQ.refetch()} style={{ marginLeft: 8, fontSize: 12 }}>Retry</button>
          </div>
        ) : !jobsQ.data?.length ? (
          <div style={{ padding: 24, textAlign: "center", color: "var(--text-muted)", fontSize: 14 }}>
            No consolidation jobs matching the filters.
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr><th>Status</th><th>Type</th><th>Reason</th><th>Proposal</th><th>Attempts</th><th>Updated</th></tr>
            </thead>
            <tbody>
              {jobsQ.data.map((j) => (
                <tr
                  key={j.id}
                  onClick={() => setSelectedId(j.id)}
                  style={{ cursor: "pointer", background: selectedId === j.id ? "var(--bg-hover)" : undefined }}
                >
                  <td>
                    <span className="badge" style={{ color: jobColor(j.status), borderColor: jobColor(j.status) }}>
                      {j.status}
                    </span>
                  </td>
                  <td style={{ fontSize: 12 }}>{j.type}</td>
                  <td style={{ maxWidth: 320, fontSize: 12 }}>{j.reason}</td>
                  <td style={{ fontSize: 12 }}>{j.proposalVersion > 0 ? `v${j.proposalVersion}` : "—"}</td>
                  <td style={{ fontSize: 12 }}>{j.attempts}</td>
                  <td style={{ fontSize: 12, whiteSpace: "nowrap" }}>{formatDate(j.updatedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {selectedId && (
        <div className="card" style={{ marginTop: 16 }}>
          {jobQ.isLoading ? (
            <div className="skeleton" style={{ height: 260 }} />
          ) : jobQ.isError || !job || !detail ? (
            <div style={{ color: "var(--danger)", fontSize: 13 }}>
              Could not load job. {(jobQ.error as Error)?.message}
            </div>
          ) : (
            <div>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: 12, flexWrap: "wrap" }}>
                <div>
                  <h3 style={{ margin: 0 }}>{job.type} · {job.reason}</h3>
                  <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 4 }}>
                    {job.id} · v{job.version} · input {job.inputVersion} · proposal v{job.proposalVersion} · attempts{" "}
                    {job.attempts}
                  </div>
                </div>
                <span className="badge" style={{ color: jobColor(job.status), borderColor: jobColor(job.status) }}>
                  {job.status}
                </span>
              </div>

              {job.lastError && (
                <div className="banner" style={{ marginTop: 10, fontSize: 12 }}>⚠ {job.lastError}</div>
              )}

              <div style={{ display: "flex", gap: 6, flexWrap: "wrap", alignItems: "center", marginTop: 12 }}>
                <input
                  value={actor}
                  onChange={(e) => setActor(e.target.value)}
                  placeholder="actor"
                  style={{ width: 100 }}
                />
                <button
                  className="primary"
                  onClick={analyze}
                  disabled={acting || !llmConfigured || ["Accepted", "Rejected", "Cancelled"].includes(job.status)}
                  title={llmConfigured ? "Run LLM analysis" : "LLM not configured"}
                >
                  {runLabel("Analyze")}
                </button>
                <button
                  className="primary"
                  onClick={() =>
                    act("Accept", () =>
                      actions.accept.mutateAsync({
                        id: job.id,
                        expectedProposalVersion: job.proposalVersion,
                        actor: actor.trim() || undefined,
                      }),
                    )
                  }
                  disabled={acting || job.proposalVersion <= 0}
                >
                  Accept
                </button>
                <button
                  onClick={() =>
                    act("Reject", () =>
                      actions.reject.mutateAsync({
                        id: job.id,
                        actor: actor.trim() || undefined,
                        reason: rejectReason.trim() || undefined,
                      }),
                    )
                  }
                  disabled={acting}
                >
                  Reject
                </button>
                <button onClick={() => act("Postpone", () => actions.postpone.mutateAsync({ id: job.id, actor: actor.trim() || undefined }))} disabled={acting}>
                  Postpone
                </button>
                <button onClick={() => act("Retry", () => actions.retry.mutateAsync({ id: job.id, actor: actor.trim() || undefined }))} disabled={acting}>
                  Retry
                </button>
                <button onClick={() => act("Cancel", () => actions.cancel.mutateAsync({ id: job.id, actor: actor.trim() || undefined }))} disabled={acting}>
                  Cancel
                </button>
                <button onClick={() => act("Undo", () => actions.undo.mutateAsync({ id: job.id, actor: actor.trim() || undefined }))} disabled={acting}>
                  Undo
                </button>
                <input
                  value={rejectReason}
                  onChange={(e) => setRejectReason(e.target.value)}
                  placeholder="reject reason"
                  style={{ flex: 1, minWidth: 150 }}
                />
              </div>

              <h4 style={{ marginTop: 16, marginBottom: 6 }}>Source entries ({detail.items.length})</h4>
              {detail.items.length === 0 ? (
                <div style={{ fontSize: 12, color: "var(--text-muted)" }}>No source entries.</div>
              ) : (
                <table className="table">
                  <thead>
                    <tr><th>Title</th><th>Type</th><th>Status</th><th>Version</th><th>Hash</th></tr>
                  </thead>
                  <tbody>
                    {detail.items.map((item) => (
                      <tr key={item.id}>
                        <td style={{ maxWidth: 320 }}>
                          <div style={{ fontWeight: 500 }}>{item.title}</div>
                          <div style={{ fontFamily: "monospace", fontSize: 10, color: "var(--text-muted)" }}>{item.id}</div>
                        </td>
                        <td style={{ fontSize: 12 }}>{item.type}</td>
                        <td style={{ fontSize: 12 }}>{item.status}</td>
                        <td style={{ fontSize: 12 }}>v{item.version}</td>
                        <td style={{ fontFamily: "monospace", fontSize: 10 }}>{item.contentHash.slice(0, 16)}…</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}

              <h4 style={{ marginTop: 16, marginBottom: 6 }}>Proposal</h4>
              {job.proposal ? (
                <pre
                  style={{
                    whiteSpace: "pre-wrap",
                    wordBreak: "break-word",
                    fontFamily: "ui-monospace, SFMono-Regular, Menlo, monospace",
                    fontSize: 12,
                    lineHeight: 1.5,
                    background: "var(--bg-hover)",
                    border: "1px solid var(--border)",
                    borderRadius: 6,
                    padding: 12,
                    margin: 0,
                    maxHeight: 420,
                    overflowY: "auto",
                  }}
                >
                  {prettyJson(job.proposal)}
                </pre>
              ) : (
                <div style={{ fontSize: 12, color: "var(--text-muted)" }}>
                  No proposal yet.{!llmConfigured ? " LLM not configured — analysis dependency is not satisfied." : " Run Analyze to generate one."}
                </div>
              )}

              {job.analysis && (
                <>
                  <h4 style={{ marginTop: 14, marginBottom: 6 }}>Analysis</h4>
                  <pre style={{ whiteSpace: "pre-wrap", fontSize: 12, margin: 0, color: "var(--text-muted)" }}>
                    {prettyJson(job.analysis)}
                  </pre>
                </>
              )}

              {detail.history.length > 0 && (
                <>
                  <h4 style={{ marginTop: 14, marginBottom: 6 }}>History</h4>
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, maxHeight: 160, overflowY: "auto" }}>
                    {detail.history.map((h) => (
                      <div key={h.id} style={{ fontSize: 12, display: "flex", gap: 8, flexWrap: "wrap" }}>
                        <span className="badge" style={{ fontSize: 10 }}>{h.action}</span>
                        <span>{h.actor ?? "system"}</span>
                        <span style={{ color: "var(--text-muted)", marginLeft: "auto" }}>{formatDate(h.at)}</span>
                      </div>
                    ))}
                  </div>
                </>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
