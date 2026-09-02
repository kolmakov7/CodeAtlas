import { Link } from "react-router-dom";
import { useState, useEffect } from "react";
import { useRepositories, useRepositoryGroups } from "@/api/hooks";
import { formatDate } from "@/utils/format";
import apiClient from "@/api/client";
import { useQueryClient } from "@tanstack/react-query";

type WizardStep = 1 | 2;

export default function RepositoryList() {
  const q = useRepositories();
  const groupsQ = useRepositoryGroups();
  const qc = useQueryClient();
  const [showForm, setShowForm] = useState(false);
  const [step, setStep] = useState<WizardStep>(1);
  const [form, setForm] = useState({ name: "", gitUrl: "", localPath: "", groupId: "" });
  const [error, setError] = useState<string | null>(null);
  const [validating, setValidating] = useState(false);
  const [validated, setValidated] = useState(false);

  // Stage 2
  const [branches, setBranches] = useState<string[]>([]);
  const [branchesLoading, setBranchesLoading] = useState(false);
  const [branchesError, setBranchesError] = useState<string | null>(null);
  const [selectedBranch, setSelectedBranch] = useState("main");
  const [filterEnabled, setFilterEnabled] = useState(false);
  const [directories, setDirectories] = useState<string[]>([]);
  const [dirsLoading, setDirsLoading] = useState(false);
  const [dirsError, setDirsError] = useState<string | null>(null);
  const [selectedDirs, setSelectedDirs] = useState<Set<string>>(new Set());
  const [saving, setSaving] = useState(false);

  const resetWizard = () => {
    setStep(1); setValidated(false); setError(null); setBranches([]); setBranchesError(null); setSelectedBranch("main");
    setFilterEnabled(false); setDirectories([]); setDirsError(null); setSelectedDirs(new Set());
  };

  const handleCancel = () => {
    setShowForm(false); resetWizard(); setForm({ name: "", gitUrl: "", localPath: "", groupId: "" });
  };

  const validateStage1 = async () => {
    setError(null); setValidating(true);
    try {
      if (!form.name.trim()) throw new Error("Name required");
      if (!form.gitUrl.trim()) throw new Error("Git URL required");
      const payload: any = { gitUrl: form.gitUrl.trim() };
      if (form.groupId) payload.groupId = form.groupId;
      const resp = await apiClient.post("/repositories/validate", payload);
      const data = resp.data as { available: boolean; error?: string };
      if (!data.available) {
        setError(data.error || "Repository not accessible");
        return;
      }
      setValidated(true);
      setStep(2);
      // load branches
      setBranchesLoading(true); setBranchesError(null);
      try {
        const br = await apiClient.get("/repositories/branches", { params: { gitUrl: form.gitUrl.trim(), groupId: form.groupId || undefined } });
        const b = (br.data as any).branches as string[] ?? (br.data as any).Branches as string[] ?? [];
        const list = Array.isArray(b) ? b : [];
        setBranches(list);
        if (list.length > 0 && !list.includes(selectedBranch)) setSelectedBranch(list[0]);
        else if (list.length === 0) setSelectedBranch("main");
      } catch (e: any) {
        setBranchesError(e.response?.data?.title || e.message);
      } finally { setBranchesLoading(false); }
    } catch (e: any) {
      setError(e.response?.data?.title || e.response?.data?.detail || e.message);
    } finally { setValidating(false); }
  };

  // Load directories when branch changes and filter enabled
  useEffect(() => {
    if (step !== 2 || !filterEnabled) return;
    if (!form.gitUrl || !selectedBranch) return;
    let cancelled = false;
    const load = async () => {
      setDirsLoading(true); setDirsError(null);
      try {
        const resp = await apiClient.get("/repositories/root-directories", { params: { gitUrl: form.gitUrl.trim(), branch: selectedBranch, groupId: form.groupId || undefined } });
        const d = (resp.data as any).directories as string[] ?? (resp.data as any).Directories as string[] ?? [];
        if (!cancelled) {
          const list = Array.isArray(d) ? d : [];
          setDirectories(list);
          // keep selectedDirs subset
          setSelectedDirs(prev => {
            const next = new Set<string>();
            prev.forEach(v => { if (list.includes(v)) next.add(v); });
            return next;
          });
        }
      } catch (e: any) {
        if (!cancelled) setDirsError(e.response?.data?.title || e.message);
      } finally { if (!cancelled) setDirsLoading(false); }
    };
    load();
    return () => { cancelled = true; };
  }, [selectedBranch, filterEnabled, step, form.gitUrl, form.groupId]);

  const toggleDir = (dir: string) => {
    setSelectedDirs(prev => {
      const next = new Set(prev);
      if (next.has(dir)) next.delete(dir); else next.add(dir);
      return next;
    });
  };

  const submitFinal = async () => {
    setError(null); setSaving(true);
    try {
      const payload: any = {
        name: form.name.trim(),
        gitUrl: form.gitUrl.trim(),
        branch: selectedBranch || "main",
        localPath: form.localPath.trim() || undefined,
        directoryFilterEnabled: filterEnabled,
        allowedDirectories: filterEnabled ? Array.from(selectedDirs) : undefined,
      };
      if (form.groupId) payload.groupId = form.groupId;
      if (filterEnabled && selectedDirs.size === 0) {
        setError("Select at least one directory or disable filter");
        setSaving(false);
        return;
      }
      await apiClient.post("/repositories", payload);
      setForm({ name: "", gitUrl: "", localPath: "", groupId: "" });
      resetWizard(); setShowForm(false);
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
          <button className="primary" onClick={() => { if (showForm) handleCancel(); else setShowForm(true); }}>{showForm ? "Cancel" : "Add Repository"}</button>
        </div>
      </div>
      {showForm && (
        <div className="card" style={{ marginBottom: 16, display: "flex", flexDirection: "column", gap: 12 }}>
          <h3 style={{ marginBottom: 4 }}>Добавление репозитория</h3>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <div style={{ padding: "6px 12px", borderRadius: 999, background: step === 1 ? "var(--accent)" : "var(--bg-hover)", border: "1px solid var(--border)", color: step === 1 ? "white" : "var(--text-muted)", fontSize: 13, fontWeight: 600 }}>
              1. Репозиторий
            </div>
            <div style={{ color: "var(--text-muted)" }}>→</div>
            <div style={{ padding: "6px 12px", borderRadius: 999, background: step === 2 ? "var(--accent)" : "var(--bg-card)", border: "1px solid var(--border)", color: step === 2 ? "white" : "var(--text-muted)", fontSize: 13, fontWeight: 600, opacity: validated ? 1 : 0.5 }}>
              2. Индексация
            </div>
          </div>

          {step === 1 && (
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <label>Name <input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="my-repo" /></label>
              <label>Git URL <input value={form.gitUrl} onChange={e => setForm({ ...form, gitUrl: e.target.value })} placeholder="https://github.com/org/repo.git or file:///tmp/repo.git" /></label>
              <label>Local path (optional) <input value={form.localPath} onChange={e => setForm({ ...form, localPath: e.target.value })} placeholder="/data/repos/my-repo (auto if empty)" /></label>
              <label>Group <select value={form.groupId} onChange={e => setForm({ ...form, groupId: e.target.value })}>
                <option value="">Default</option>
                {(groupsQ.data as any[] ?? []).map((g: any) => <option key={g.id} value={g.id}>{g.name}</option>)}
              </select>
                <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Repository inherits Proxy & Credentials from its group</span>
              </label>
              {error && <div style={{ color: "var(--danger)", fontSize: 13 }}>{error}</div>}
              <button className="primary" onClick={validateStage1} disabled={validating || !form.name.trim() || !form.gitUrl.trim()}>
                {validating ? "Проверяем…" : "Далее → Проверка доступности"}
              </button>
            </div>
          )}

          {step === 2 && (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <div style={{ fontSize: 13, color: "var(--text-muted)", background: "var(--bg-hover)", padding: 8, borderRadius: 6 }}>
                Репозиторий: <strong style={{ color: "var(--text)" }}>{form.name}</strong> · <code>{form.gitUrl}</code> · Group: {form.groupId ? (groupsQ.data as any[])?.find((g:any)=>g.id===form.groupId)?.name ?? form.groupId : "Default"}
              </div>

              <label>Ветка
                {branchesLoading ? <div className="skeleton" style={{ height: 32, marginTop: 4 }} /> : (
                  <select value={selectedBranch} onChange={e => setSelectedBranch(e.target.value)} disabled={branchesLoading}>
                    {branches.length === 0 ? <option value={selectedBranch}>{selectedBranch}</option> : branches.map(b => <option key={b} value={b}>{b}</option>)}
                  </select>
                )}
                {branchesError && <div style={{ color: "var(--danger)", fontSize: 12 }}>{branchesError}</div>}
                <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Выберите ветку для индексации</span>
              </label>

              <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
                <input type="checkbox" checked={filterEnabled} onChange={e => setFilterEnabled(e.target.checked)} style={{ width: 16, height: 16 }} />
                <span>Ограничить индексируемые директории</span>
              </label>

              {filterEnabled && (
                <div style={{ border: "1px solid var(--border)", borderRadius: 8, padding: 12, background: "var(--bg-hover)" }}>
                  <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 8 }}>Корневые директории репозитория (ветка {selectedBranch})</div>
                  {dirsLoading ? <div className="skeleton" style={{ height: 80 }} /> : dirsError ? (
                    <div style={{ color: "var(--danger)", fontSize: 12 }}>{dirsError}</div>
                  ) : directories.length === 0 ? (
                    <div style={{ color: "var(--text-muted)", fontSize: 13 }}>Нет директорий (плоская структура) или не удалось загрузить</div>
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
                  <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 8 }}>Если checkbox выключен — индексируется весь репозиторий. При включении — только отмеченные директории и их вложенные.</div>
                </div>
              )}

              {error && <div style={{ color: "var(--danger)", fontSize: 13 }}>{error}</div>}
              <div style={{ display: "flex", gap: 8 }}>
                <button onClick={() => setStep(1)}>← Назад</button>
                <button className="primary" onClick={submitFinal} disabled={saving || (filterEnabled && selectedDirs.size === 0)}>
                  {saving ? "Создаём…" : "Создать и запустить индексацию"}
                </button>
              </div>
            </div>
          )}
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
                    {(r as any).directoryFilterEnabled && <span className="badge" style={{ background: "#1e3a5f", color: "#93c5fd" }}>filter</span>}
                  </div>
                  <div style={{ fontSize: 13, color: "var(--text-muted)", marginTop: 4 }}>
                    {r.gitUrl} · {r.branch} · {r.currentCommit?.slice(0, 7) ?? "—"} → {r.indexedCommit?.slice(0, 7) ?? "—"}
                    {(r as any).directoryFilterEnabled && (r as any).allowedDirectories?.length ? ` · dirs: ${(r as any).allowedDirectories.join(",")}` : ""}
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
