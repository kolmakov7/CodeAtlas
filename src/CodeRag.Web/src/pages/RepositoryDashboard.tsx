import { useParams, Link } from "react-router-dom";
import { useState, useEffect } from "react";
import { useRepository, useRepositoryStatistics, useRepositoryActions, useJobs, useRepositoryGroups } from "@/api/hooks";
import { StatCard } from "@/components/StatCard";
import { ConfirmModal } from "@/components/ConfirmModal";
import { ProgressBar } from "@/components/ProgressBar";
import RepositoryLinksCard from "@/components/RepositoryLinksCard";
import RepositoryUsageCard from "@/components/RepositoryUsageCard";
import { formatDate, formatDuration } from "@/utils/format";
import apiClient from "@/api/client";
import { useQueryClient } from "@tanstack/react-query";

export default function RepositoryDashboard() {
  const { id } = useParams<{ id: string }>();
  const repoQ = useRepository(id);
  const statsQ = useRepositoryStatistics(id);
  const jobsQ = useJobs(id);
  const groupsQ = useRepositoryGroups();
  const qc = useQueryClient();
  const actions = useRepositoryActions();
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [groupError, setGroupError] = useState<string | null>(null);

  // Edit indexing settings
  const [editOpen, setEditOpen] = useState(false);
  const [editBranch, setEditBranch] = useState("");
  const [editFilterEnabled, setEditFilterEnabled] = useState(false);
  const [branches, setBranches] = useState<string[]>([]);
  const [branchesLoading, setBranchesLoading] = useState(false);
  const [directories, setDirectories] = useState<string[]>([]);
  const [dirsLoading, setDirsLoading] = useState(false);
  const [selectedDirs, setSelectedDirs] = useState<Set<string>>(new Set());
  const [editError, setEditError] = useState<string | null>(null);
  const [editSaving, setEditSaving] = useState(false);
  const [dirsError, setDirsError] = useState<string | null>(null);

  const r = repoQ.data;

  // Init edit state when opened
  useEffect(() => {
    if (editOpen && r) {
      setEditBranch((r as any).branch || r.branch || "main");
      setEditFilterEnabled((r as any).directoryFilterEnabled ?? false);
      const dirs = (r as any).allowedDirectories as string[] | undefined;
      setSelectedDirs(new Set(dirs ?? []));
      setEditError(null); setDirsError(null);
    }
  }, [editOpen, r]);

  // Load branches when edit opens
  useEffect(() => {
    if (!editOpen || !id) return;
    let cancelled = false;
    const load = async () => {
      setBranchesLoading(true);
      try {
        const resp = await apiClient.get(`/repositories/${id}/branches`);
        const b = (resp.data as any).branches ?? (resp.data as any).Branches ?? [];
        if (!cancelled) setBranches(Array.isArray(b) ? b : []);
      } catch (e: any) { if (!cancelled) setBranches([]); }
      finally { if (!cancelled) setBranchesLoading(false); }
    };
    load();
    return () => { cancelled = true; };
  }, [editOpen, id]);

  // Load directories when branch changes and filter enabled
  useEffect(() => {
    if (!editOpen || !editFilterEnabled || !id || !editBranch) {
      if (!editFilterEnabled) { setDirectories([]); setDirsLoading(false); }
      return;
    }
    let cancelled = false;
    const loadDirs = async () => {
      setDirsLoading(true); setDirsError(null);
      try {
        const resp = await apiClient.get(`/repositories/${id}/root-directories`, { params: { branch: editBranch } });
        const d = (resp.data as any).directories ?? (resp.data as any).Directories ?? [];
        if (!cancelled) {
          const list = Array.isArray(d) ? d : [];
          setDirectories(list);
          setSelectedDirs(prev => {
            const next = new Set<string>();
            prev.forEach(v => { if (list.includes(v)) next.add(v); });
            // keep existing if not filtered? Or keep all? We keep subset only
            return next;
          });
        }
      } catch (e: any) {
        if (!cancelled) setDirsError(e.response?.data?.title || e.message);
      } finally { if (!cancelled) setDirsLoading(false); }
    };
    loadDirs();
    return () => { cancelled = true; };
  }, [editBranch, editFilterEnabled, editOpen, id]);

  const toggleDir = (d: string) => {
    setSelectedDirs(prev => { const n = new Set(prev); if (n.has(d)) n.delete(d); else n.add(d); return n; });
  };

  const saveEdit = async () => {
    if (!id || !r) return;
    setEditError(null); setEditSaving(true);
    try {
      const payload: any = {};
      if (editBranch && editBranch !== r.branch) payload.branch = editBranch;
      payload.directoryFilterEnabled = editFilterEnabled;
      payload.allowedDirectories = editFilterEnabled ? Array.from(selectedDirs) : [];
      if (editFilterEnabled && selectedDirs.size === 0) {
        setEditError("Select at least one directory or disable filter");
        setEditSaving(false); return;
      }
      await apiClient.put(`/repositories/${id}`, payload);
      qc.invalidateQueries({ queryKey: ["repositories", id] });
      qc.invalidateQueries({ queryKey: ["jobs"] });
      qc.invalidateQueries({ queryKey: ["repositories"] });
      repoQ.refetch();
      setEditOpen(false);
    } catch (e: any) {
      setEditError(e.response?.data?.title || e.response?.data?.detail || e.message);
    } finally { setEditSaving(false); }
  };

  if (repoQ.isLoading) return <div className="skeleton" style={{ height: 200 }} />;
  if (repoQ.isError || !repoQ.data) return <div className="card">Repository not found. <Link to="/repositories">Back</Link></div>;
  // re-assign after hooks to satisfy ts
  const repo = repoQ.data!;
  const s = statsQ.data;

  const busy = repo.status === "Syncing" || repo.status === "Indexing";
  const filterEnabled = (repo as any).directoryFilterEnabled as boolean | undefined;
  const allowedDirs = (repo as any).allowedDirectories as string[] | undefined;
  return (
    <div>
      <Link to="/repositories" style={{ fontSize: 13 }}>← All repositories</Link>
      <div className="card" style={{ marginTop: 12 }}>
        <div style={{ display: "flex", justifyContent: "space-between", gap: 16, flexWrap: "wrap" }}>
          <div>
            <h1 style={{ display: "flex", alignItems: "center", gap: 8 }}>{repo.name} <span className={`badge ${repo.status.toLowerCase()}`}>{repo.status}</span></h1>
            <div style={{ color: "var(--text-muted)", fontSize: 13, marginTop: 4, wordBreak: "break-all" }}>
              <div>Git: <code>{repo.gitUrl}</code> · Branch: <code>{repo.branch}</code></div>
              <div>HEAD: <code>{repo.currentCommit ?? "—"}</code> · Indexed: <code>{repo.indexedCommit ?? "—"}</code></div>
              <div>LastSync: {formatDate(repo.lastSyncAt)} · LastIndex: {formatDate(repo.lastIndexAt)}</div>
              <div>Индексация: {filterEnabled ? `filter: ${allowedDirs?.length ? allowedDirs.join(", ") : "(empty)"}` : "весь репозиторий"}</div>
              <div style={{ marginTop: 6, display: "flex", gap: 8, alignItems: "center" }}>
                <span>Group:</span>
                <select value={(repo as any).groupId ?? ""} onChange={async (e) => {
                  setGroupError(null);
                  try {
                    const gid = e.target.value || null;
                    await apiClient.put(`/repositories/${repo.id}`, { groupId: gid });
                    qc.invalidateQueries({ queryKey: ["repositories", repo.id] });
                    repoQ.refetch();
                  } catch (err: any) { setGroupError(err.response?.data?.title ?? err.message); }
                }}>
                  <option value="">Default</option>
                  {(groupsQ.data as any[] ?? []).map((g: any) => <option key={g.id} value={g.id}>{g.name}</option>)}
                </select>
                <button onClick={() => setEditOpen(true)} style={{ marginLeft: 8 }} disabled={busy}>Edit indexing</button>
              </div>
              {groupError && <div style={{ color: "var(--danger)", fontSize: 11 }}>{groupError}</div>}
              {repo.lastError && <div style={{ color: "var(--danger)", marginTop: 4 }}>Error: {repo.lastError}</div>}
            </div>
            {(() => {
              const active = (jobsQ.data ?? []).filter((j) => j.status === "Running" || j.status === "Queued");
              if (!active.length) return null;
              return (
                <div style={{ marginTop: 10, display: "flex", flexDirection: "column", gap: 6, padding: "8px 12px", border: "1px solid var(--border)", borderRadius: 8, background: "rgba(96,165,250,0.06)" }}>
                  {active.map((j) => (
                    <div key={j.id} style={{ display: "flex", alignItems: "center", gap: 10, fontSize: 13, flexWrap: "wrap" }}>
                      <span className="badge">{j.status}</span>
                      <strong>{j.type}</strong>
                      {j.phase && <span style={{ color: "var(--text-muted)" }}>· {j.phase}</span>}
                      <div style={{ flex: "0 1 220px", minWidth: 120 }}><ProgressBar value={j.progress} /></div>
                      <span style={{ fontSize: 12, fontVariantNumeric: "tabular-nums", whiteSpace: "nowrap" }}>
                        {j.totalItems != null && j.totalItems > 0
                          ? `${(j.itemsDone ?? 0).toLocaleString()} / ${j.totalItems.toLocaleString()} ${j.type === "Embedding" ? "chunks" : "files"} · `
                          : ""}{((j.progress ?? 0) * 100).toFixed(0)}%
                      </span>
                      <Link to={`/jobs?repositoryId=${repo.id}`} style={{ fontSize: 12 }}>details →</Link>
                    </div>
                  ))}
                </div>
              );
            })()}
          </div>
          <div style={{ display: "flex", gap: 8, alignItems: "flex-start", flexWrap: "wrap" }}>
            <button onClick={() => actions.sync.mutate(repo.id)} disabled={busy || actions.sync.isPending}>Sync</button>
            <button onClick={() => actions.reindex.mutate(repo.id)} disabled={busy || actions.reindex.isPending}>Re-index</button>
            <button onClick={() => actions.relations.mutate(repo.id)} disabled={busy || actions.relations.isPending}>Relations</button>
            {repo.status !== "Paused" ? (
              <button onClick={() => actions.pause.mutate(repo.id)} disabled={actions.pause.isPending}>Pause</button>
            ) : (
              <button onClick={() => actions.resume.mutate(repo.id)} disabled={actions.resume.isPending} className="primary">Resume</button>
            )}
            <button className="danger" onClick={() => setDeleteOpen(true)}>Delete</button>
          </div>
        </div>
      </div>

      {editOpen && (
        <div className="card" style={{ marginTop: 12, display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Редактирование индексации — аналогично этапу 2 добавления</h3>
          <label>Ветка
            {branchesLoading ? <div className="skeleton" style={{ height: 32 }} /> : (
              <select value={editBranch} onChange={e => setEditBranch(e.target.value)}>
                {branches.length === 0 ? <option value={editBranch}>{editBranch}</option> : branches.map(b => <option key={b} value={b}>{b}</option>)}
              </select>
            )}
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>При изменении ветки будет запущен Rebuild</span>
          </label>

          <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
            <input type="checkbox" checked={editFilterEnabled} onChange={e => setEditFilterEnabled(e.target.checked)} style={{ width: 16, height: 16 }} />
            <span>Ограничить индексируемые директории</span>
          </label>

          {editFilterEnabled && (
            <div style={{ border: "1px solid var(--border)", borderRadius: 8, padding: 12, background: "var(--bg-hover)" }}>
              <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>Корневые директории (ветка {editBranch})</div>
              {dirsLoading ? <div className="skeleton" style={{ height: 80 }} /> : dirsError ? (
                <div style={{ color: "var(--danger)", fontSize: 12 }}>{dirsError}</div>
              ) : directories.length === 0 ? (
                <div style={{ color: "var(--text-muted)", fontSize: 13 }}>Нет директорий или плоская структура</div>
              ) : (
                <div style={{ display: "flex", flexDirection: "column", gap: 6, maxHeight: 220, overflowY: "auto" }}>
                  {directories.map(d => (
                    <label key={d} style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 13, cursor: "pointer" }}>
                      <input type="checkbox" checked={selectedDirs.has(d)} onChange={() => toggleDir(d)} style={{ width: 16, height: 16 }} />
                      <span>{d}</span>
                    </label>
                  ))}
                </div>
              )}
              <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 8 }}>При отключении фильтра снова индексируется весь репозиторий.</div>
            </div>
          )}

          {editError && <div style={{ color: "var(--danger)", fontSize: 13 }}>{editError}</div>}
          <div style={{ display: "flex", gap: 8 }}>
            <button onClick={() => setEditOpen(false)}>Cancel</button>
            <button className="primary" onClick={saveEdit} disabled={editSaving || (editFilterEnabled && selectedDirs.size === 0)}>
              {editSaving ? "Сохранение…" : "Save and Reindex if changed"}
            </button>
          </div>
        </div>
      )}

      <div className="grid grid-5" style={{ marginTop: 16 }}>
        <StatCard label="Files" value={s?.fileCount ?? "—"} />
        <StatCard label="Symbols" value={s?.symbolCount ?? "—"} />
        <StatCard label="Chunks" value={s?.chunkCount ?? "—"} />
        <StatCard label="Relations" value={s?.relationCount ?? 0} sub={s?.relationsByType?.length ? s.relationsByType.map((r) => `${r.relationType} ${r.count}`).join(" · ") : undefined} />
        {(() => {
          const stats = s?.embeddingStats ?? [];
          const code = stats.find((d) => d.domain?.toLowerCase() === "code");
          const text = stats.find((d) => d.domain?.toLowerCase() === "text");
          const pending = stats.reduce((a, d) => a + (d.pending ?? 0), 0);
          return (
            <StatCard
              label="Embeddings"
              value={s ? `${s.vectorCount}/${s.chunkCount}` : "—"}
              sub={s ? `code ${code?.indexed ?? 0} · text ${text?.indexed ?? 0} · ${pending} pending` : "vectors / chunks"}
            />
          );
        })()}
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
                    <td style={{ minWidth: 120 }}>
                      <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                        <div style={{ flex: 1 }}><ProgressBar value={j.progress} /></div>
                        <span style={{ fontSize: 11, whiteSpace: "nowrap", fontVariantNumeric: "tabular-nums" }}>
                          {j.totalItems != null && j.totalItems > 0
                            ? `${(j.itemsDone ?? 0).toLocaleString()}/${j.totalItems.toLocaleString()} · `
                            : ""}{((j.progress ?? 0) * 100).toFixed(0)}%
                        </span>
                      </div>
                      {j.phase && (j.status === "Running" || j.status === "Queued") && (
                        <div style={{ fontSize: 11, color: "var(--text-muted)" }}>{j.phase}</div>
                      )}
                    </td>
                    <td style={{ fontSize: 12, color: "var(--text-muted)", whiteSpace: "nowrap" }}>{formatDuration(j.startedAt, j.status === "Running" ? null : j.finishedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No jobs yet</div>}
        </div>
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Shortcuts</h3>
          <div style={{ display: "flex", flexDirection: "column", gap: 8, fontSize: 14 }}>
            <Link to={`/search?repositoryId=${repo.id}`}>Search in this repository →</Link>
            <Link to={`/documentation?repositoryId=${repo.id}`}>AI documentation for this repo →</Link>
            <Link to={`/jobs?repositoryId=${repo.id}`}>View all jobs for this repo →</Link>
          </div>
        </div>
      </div>

      <div style={{ display: "grid", gap: 16, marginTop: 16 }}>
        <RepositoryLinksCard repositoryId={repo.id} />
        <RepositoryUsageCard repositoryId={repo.id} />
      </div>

      <ConfirmModal
        open={deleteOpen}
        title="Delete repository?"
        message={`This will delete repository "${repo.name}" and all its index data (files, symbols, chunks, relations, vectors, FTS). This action cannot be undone.`}
        confirmLabel="Delete"
        danger
        showCheckoutOption
        onClose={() => setDeleteOpen(false)}
        onConfirm={(deleteCheckout) => {
          actions.remove.mutate({ id: repo.id, deleteCheckout });
          setDeleteOpen(false);
        }}
      />
    </div>
  );
}
