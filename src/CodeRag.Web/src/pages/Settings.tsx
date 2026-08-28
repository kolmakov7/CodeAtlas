import { useEffect, useState } from "react";
import { useSettings, useUpdateSettings, useProxies, useCredentials } from "@/api/hooks";

type Tab = "repositories" | "indexing" | "embeddings" | "search" | "storage" | "system" | "proxies" | "credentials";
const tabs: { id: Tab; label: string }[] = [
  { id: "repositories", label: "Repositories" },
  { id: "indexing", label: "Indexing" },
  { id: "embeddings", label: "Embeddings" },
  { id: "search", label: "Search" },
  { id: "storage", label: "Storage" },
  { id: "system", label: "System" },
  { id: "proxies", label: "Proxies" },
  { id: "credentials", label: "Credentials" },
];

export default function SettingsPage() {
  const [tab, setTab] = useState<Tab>("repositories");
  const settingsQ = useSettings();
  const proxiesQ = useProxies();
  const credsQ = useCredentials();
  const update = useUpdateSettings();

  const [form, setForm] = useState<NonNullable<typeof settingsQ.data>>({});

  useEffect(() => {
    if (settingsQ.data) setForm(structuredClone(settingsQ.data));
  }, [settingsQ.data]);

  if (settingsQ.isLoading) return <div className="skeleton" style={{ height: 300 }} />;
  if (settingsQ.isError) return <div className="card" style={{ borderColor: "var(--danger)" }}>Failed: {(settingsQ.error as Error).message} <button onClick={() => settingsQ.refetch()} style={{ marginLeft: 8 }}>Retry</button></div>;

  const save = () => update.mutate(form);

  return (
    <div>
      <h1 style={{ marginBottom: 12 }}>Settings</h1>
      <p style={{ color: "var(--text-muted)", fontSize: 13, marginBottom: 12 }}>Changes are persisted to <code>settings</code> table — no image rebuild required (§34).</p>

      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", borderBottom: "1px solid var(--border)", paddingBottom: 8, marginBottom: 16 }}>
        {tabs.map((t) => (
          <button key={t.id} onClick={() => setTab(t.id)} className={tab === t.id ? "primary" : undefined} style={{ fontSize: 13 }}>{t.label}</button>
        ))}
      </div>

      {tab === "repositories" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Repositories defaults</h3>
          <label>Default branch <input value={form.repositories?.defaultBranch ?? ""} onChange={(e) => setForm({ ...form, repositories: { ...form.repositories, defaultBranch: e.target.value } })} placeholder="main" /></label>
          <label>Sync interval (seconds) <input type="number" value={form.repositories?.defaultSyncIntervalSeconds ?? ""} onChange={(e) => setForm({ ...form, repositories: { ...form.repositories, defaultSyncIntervalSeconds: e.target.value ? Number(e.target.value) : null } })} /></label>
          <label>Default proxy id <input value={form.repositories?.defaultProxyId ?? ""} onChange={(e) => setForm({ ...form, repositories: { ...form.repositories, defaultProxyId: e.target.value || null } })} placeholder="proxy uuid or empty" /></label>
          <label style={{ display: "flex", gap: 8, alignItems: "center" }}><input type="checkbox" checked={form.repositories?.defaultEnabled ?? true} onChange={(e) => setForm({ ...form, repositories: { ...form.repositories, defaultEnabled: e.target.checked } })} style={{ width: "auto" }} /> Enabled by default</label>
        </div>
      )}
      {tab === "indexing" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Indexing</h3>
          <label>Sync interval (seconds) <input type="number" value={form.indexing?.syncIntervalSeconds ?? ""} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, syncIntervalSeconds: e.target.value ? Number(e.target.value) : null } })} /></label>
          <label>Max concurrent indexers <input type="number" value={form.indexing?.maxConcurrentIndexers ?? 2} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, maxConcurrentIndexers: Number(e.target.value) } })} /></label>
          <label>Exclusions (glob, comma separated) <input value={(form.indexing?.exclusions ?? []).join(", ")} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, exclusions: e.target.value.split(",").map(s => s.trim()).filter(Boolean) } })} placeholder="**/bin/**, **/obj/**, **/node_modules/**" /></label>
          <label>Language exclusions <input value={(form.indexing?.languageExclusions ?? []).join(", ")} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, languageExclusions: e.target.value.split(",").map(s => s.trim()).filter(Boolean) } })} placeholder="VBS, ..." /></label>
        </div>
      )}
      {tab === "embeddings" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Embeddings</h3>
          <label>Endpoint <input value={form.embeddings?.endpoint ?? ""} onChange={(e) => setForm({ ...form, embeddings: { ...form.embeddings, endpoint: e.target.value } })} placeholder="http://llama-embeddings:8080/v1/embeddings" /></label>
          <label>Model <input value={form.embeddings?.model ?? ""} onChange={(e) => setForm({ ...form, embeddings: { ...form.embeddings, model: e.target.value } })} placeholder="bge-m3" /></label>
          <label>Dimensions <input type="number" value={form.embeddings?.dimensions ?? 384} onChange={(e) => setForm({ ...form, embeddings: { ...form.embeddings, dimensions: Number(e.target.value) } })} /></label>
          <label>Batch size <input type="number" value={form.embeddings?.batchSize ?? 32} onChange={(e) => setForm({ ...form, embeddings: { ...form.embeddings, batchSize: Number(e.target.value) } })} /></label>
          <label>Timeout (seconds) <input type="number" value={form.embeddings?.timeoutSeconds ?? 30} onChange={(e) => setForm({ ...form, embeddings: { ...form.embeddings, timeoutSeconds: Number(e.target.value) } })} /></label>
        </div>
      )}
      {tab === "search" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Search weights</h3>
          <label>Lexical weight <input type="number" step="0.1" min={0} max={1} value={form.search?.lexicalWeight ?? 0.4} onChange={(e) => setForm({ ...form, search: { ...form.search, lexicalWeight: Number(e.target.value) } })} /></label>
          <label>Semantic weight <input type="number" step="0.1" min={0} max={1} value={form.search?.semanticWeight ?? 0.4} onChange={(e) => setForm({ ...form, search: { ...form.search, semanticWeight: Number(e.target.value) } })} /></label>
          <label>Structural weight <input type="number" step="0.1" min={0} max={1} value={form.search?.structuralWeight ?? 0.2} onChange={(e) => setForm({ ...form, search: { ...form.search, structuralWeight: Number(e.target.value) } })} /></label>
          <label>Default result count <input type="number" value={form.search?.defaultResultCount ?? 20} onChange={(e) => setForm({ ...form, search: { ...form.search, defaultResultCount: Number(e.target.value) } })} /></label>
        </div>
      )}
      {tab === "storage" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Storage</h3>
          <label>Data directory (read-only) <input value={form.storage?.dataDir ?? "/data"} disabled title="configured via CODERAG__DataDir env" /></label>
          <label>Vector config <input value={form.storage?.vectorConfig ?? ""} onChange={(e) => setForm({ ...form, storage: { ...form.storage, vectorConfig: e.target.value } })} placeholder="sqlite-vec / usearch" /></label>
          <div style={{ fontSize: 12, color: "var(--text-muted)" }}>Storage sizes are shown on Dashboard. Data dir is not editable via UI — set via environment.</div>
        </div>
      )}
      {tab === "system" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>System</h3>
          <label>Log level
            <select value={form.system?.logLevel ?? "Information"} onChange={(e) => setForm({ ...form, system: { ...form.system, logLevel: e.target.value } })}>
              {["Trace", "Debug", "Information", "Warning", "Error", "Critical"].map((l) => <option key={l} value={l}>{l}</option>)}
            </select>
          </label>
        </div>
      )}
      {tab === "proxies" && (
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Proxies</h3>
          {proxiesQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : proxiesQ.data?.length ? (
            <table className="table">
              <thead><tr><th>Name</th><th>Scheme</th><th>Host</th><th>Port</th><th>Actions</th></tr></thead>
              <tbody>
                {proxiesQ.data.map((p) => (
                  <tr key={p.id}>
                    <td>{p.name}</td><td>{p.scheme}</td><td>{p.host}</td><td>{p.port}</td>
                    <td><button style={{ fontSize: 12 }} onClick={() => fetch(`/api/proxies/${p.id}/test`, { method: "POST" }).then(() => alert("Proxy test requested"))}>Test</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No proxies configured. Add via <code>POST /api/proxies</code>.</div>}
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginTop: 8 }}>See docs/git-proxy-auth.md for details.</div>
        </div>
      )}
      {tab === "credentials" && (
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Credentials</h3>
          {credsQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : credsQ.data?.length ? (
            <table className="table">
              <thead><tr><th>Kind</th><th>Username</th><th>Id</th></tr></thead>
              <tbody>
                {credsQ.data.map((c) => (
                  <tr key={c.id}><td>{c.kind}</td><td>{c.username ?? "—"}</td><td style={{ fontFamily: "monospace", fontSize: 12 }}>{c.id.slice(0, 8)}…</td></tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No credentials stored (secrets never displayed).</div>}
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginTop: 8 }}>Credentials are write-only via <code>POST /api/credentials</code> — secrets are encrypted.</div>
        </div>
      )}

      {(tab !== "proxies" && tab !== "credentials") && (
        <div style={{ marginTop: 16, display: "flex", gap: 8 }}>
          <button className="primary" onClick={save} disabled={update.isPending}>{update.isPending ? "Saving…" : "Save"}</button>
          {update.isSuccess && <span style={{ color: "var(--success)", fontSize: 13, alignSelf: "center" }}>Saved</span>}
          {update.isError && <span style={{ color: "var(--danger)", fontSize: 13, alignSelf: "center" }}>{(update.error as Error).message}</span>}
        </div>
      )}
    </div>
  );
}
