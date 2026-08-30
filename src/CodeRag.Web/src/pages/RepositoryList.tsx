import { Link } from "react-router-dom";
import { useState } from "react";
import { useRepositories } from "@/api/hooks";
import { formatDate } from "@/utils/format";
import apiClient from "@/api/client";
import { useQueryClient } from "@tanstack/react-query";

export default function RepositoryList() {
  const q = useRepositories();
  const qc = useQueryClient();
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState({ name: "", gitUrl: "", branch: "main", localPath: "" });
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const submit = async () => {
    setError(null); setSaving(true);
    try {
      await apiClient.post("/repositories", { name: form.name, gitUrl: form.gitUrl, branch: form.branch || "main", localPath: form.localPath || undefined });
      setForm({ name: "", gitUrl: "", branch: "main", localPath: "" }); setShowForm(false);
      qc.invalidateQueries({ queryKey: ["repositories"] }); qc.invalidateQueries({ queryKey: ["statistics"] });
    } catch (e: any) { setError(e.response?.data?.title || e.response?.data?.detail || e.message); } finally { setSaving(false); }
  };
  if (q.isLoading) return <div className="skeleton" style={{ height: 200 }} />;
  if (q.isError) return <div className="card" style={{ borderColor: "var(--danger)" }}>Error: {(q.error as Error).message} <button onClick={() => q.refetch()} style={{ marginLeft: 8 }}>Retry</button></div>;
  const repos = q.data ?? [];
  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 16 }}>
        <h1>Repositories</h1>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <span style={{ color: "var(--text-muted)", fontSize: 13 }}>{repos.length} total</span>
          <button className="primary" onClick={() => setShowForm(!showForm)}>{showForm ? "Cancel" : "Add Repository"}</button>
        </div>
      </div>
      {showForm && (
        <div className="card" style={{ marginBottom: 16, display: "flex", flexDirection: "column", gap: 8 }}>
          <h3 style={{ marginBottom: 4 }}>Add Repository manually</h3>
          <label>Name <input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="my-repo" /></label>
          <label>Git URL <input value={form.gitUrl} onChange={e => setForm({ ...form, gitUrl: e.target.value })} placeholder="https://github.com/org/repo.git or file:///tmp/repo.git or git@github.com:org/repo.git" /></label>
          <label>Branch <input value={form.branch} onChange={e => setForm({ ...form, branch: e.target.value })} placeholder="main" /></label>
          <label>Local path (optional) <input value={form.localPath} onChange={e => setForm({ ...form, localPath: e.target.value })} placeholder="/data/repos/my-repo (auto if empty)" /></label>
          {error && <div style={{ color: "var(--danger)", fontSize: 13 }}>{error}</div>}
          <button className="primary" onClick={submit} disabled={saving || !form.name || !form.gitUrl}>{saving ? "Saving…" : "Create"}</button>
        </div>
      )}
      {repos.length === 0 && !showForm ? (
        <div className="card" style={{ textAlign: "center", padding: 32 }}>
          <div style={{ fontSize: 18, fontWeight: 600 }}>No repositories yet</div>
          <p style={{ color: "var(--text-muted)", margin: "8px 0 16px" }}>Add a Git repository manually above or via <code>POST /api/repositories</code></p>
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
