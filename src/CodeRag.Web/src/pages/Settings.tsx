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
  const [proxyForm, setProxyForm] = useState({ name: "", scheme: "http", host: "", port: 8080, username: "", password: "" });
  const [credForm, setCredForm] = useState({ kind: "https_token", username: "", secret: "" });
  const [proxyError, setProxyError] = useState<string | null>(null);
  const [credError, setCredError] = useState<string | null>(null);

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
          <label>Min semantic score <input type="number" step="0.01" min={0} max={1} value={form.search?.minSemanticScore ?? 0.25} onChange={(e) => setForm({ ...form, search: { ...form.search, minSemanticScore: Number(e.target.value) } })} /></label>
          <div style={{ fontSize: 12, color: "var(--text-muted)" }}>Vector matches below this cosine similarity are dropped, so weak/ambiguous queries return no "least-irrelevant" noise. Applied live per query — no rebuild or re-embedding. Tune per embedding model (0 disables the filter).</div>
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
          <h3 style={{ marginBottom: 8 }}>Proxies — add manually</h3>
          <div style={{ display: "flex", flexDirection: "column", gap: 8, marginBottom: 12, padding: 12, border: "1px solid var(--border)", borderRadius: 6 }}>
            <div style={{ display: "flex", gap: 8 }}>
              <input placeholder="Name" value={proxyForm.name} onChange={e => setProxyForm({ ...proxyForm, name: e.target.value })} style={{ flex: 1 }} />
              <select value={proxyForm.scheme} onChange={e => setProxyForm({ ...proxyForm, scheme: e.target.value })}>
                <option value="http">http</option><option value="https">https</option><option value="socks5">socks5</option><option value="socks5h">socks5h</option>
              </select>
            </div>
            <div style={{ display: "flex", gap: 8 }}>
              <input placeholder="Host" value={proxyForm.host} onChange={e => setProxyForm({ ...proxyForm, host: e.target.value })} style={{ flex: 1 }} />
              <input placeholder="Port" type="number" value={proxyForm.port} onChange={e => setProxyForm({ ...proxyForm, port: Number(e.target.value) })} style={{ width: 100 }} />
            </div>
            <div style={{ display: "flex", gap: 8 }}>
              <input placeholder="Username (optional)" value={proxyForm.username} onChange={e => setProxyForm({ ...proxyForm, username: e.target.value })} style={{ flex: 1 }} />
              <input placeholder="Password (optional)" type="password" value={proxyForm.password} onChange={e => setProxyForm({ ...proxyForm, password: e.target.value })} style={{ flex: 1 }} />
            </div>
            {proxyError && <div style={{ color: "var(--danger)", fontSize: 12 }}>{proxyError}</div>}
            <button className="primary" onClick={async () => {
              setProxyError(null);
              try {
                const r = await fetch("/api/proxies", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: proxyForm.name, scheme: proxyForm.scheme, host: proxyForm.host, port: proxyForm.port, username: proxyForm.username || undefined, password: proxyForm.password || undefined }) });
                if (!r.ok) throw new Error(await r.text());
                setProxyForm({ name: "", scheme: "http", host: "", port: 8080, username: "", password: "" });
                proxiesQ.refetch();
              } catch (e: any) { setProxyError(e.message); }
            }}>Add Proxy</button>
          </div>
          {proxiesQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : proxiesQ.data?.length ? (
            <table className="table">
              <thead><tr><th>Name</th><th>Scheme</th><th>Host</th><th>Port</th><th>Actions</th></tr></thead>
              <tbody>
                {(proxiesQ.data as any[]).map((p: any) => (
                  <tr key={p.id}>
                    <td>{p.name}</td><td>{p.scheme}</td><td>{p.host}</td><td>{p.port}</td>
                    <td style={{ display: "flex", gap: 4 }}><button style={{ fontSize: 12 }} onClick={() => fetch(`/api/proxies/${p.id}/test`, { method: "POST" }).then(async r => alert(await r.text())).catch(e => alert(e.message))}>Test</button><button style={{ fontSize: 12 }} onClick={async () => { if (confirm(`Delete proxy ${p.name}?`)) { await fetch(`/api/proxies/${p.id}`, { method: "DELETE" }); proxiesQ.refetch(); }}}>Delete</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No proxies configured.</div>}
        </div>
      )}
      {tab === "credentials" && (
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Credentials — add manually</h3>
          <div style={{ display: "flex", flexDirection: "column", gap: 8, marginBottom: 12, padding: 12, border: "1px solid var(--border)", borderRadius: 6 }}>
            <select value={credForm.kind} onChange={e => setCredForm({ ...credForm, kind: e.target.value })}>
              <option value="https_token">https_token (PAT)</option><option value="https_userpass">https_userpass</option><option value="ssh_key">ssh_key</option>
            </select>
            <input placeholder="Username (optional)" value={credForm.username} onChange={e => setCredForm({ ...credForm, username: e.target.value })} />
            <input placeholder="Secret / Token / Private Key" type="password" value={credForm.secret} onChange={e => setCredForm({ ...credForm, secret: e.target.value })} />
            {credError && <div style={{ color: "var(--danger)", fontSize: 12 }}>{credError}</div>}
            <button className="primary" onClick={async () => {
              setCredError(null);
              try {
                const r = await fetch("/api/credentials", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kind: credForm.kind, username: credForm.username || undefined, secret: credForm.secret }) });
                if (!r.ok) throw new Error(await r.text());
                setCredForm({ kind: "https_token", username: "", secret: "" });
                credsQ.refetch();
              } catch (e: any) { setCredError(e.message); }
            }}>Add Credential</button>
          </div>
          {credsQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : (credsQ.data as any[])?.length ? (
            <table className="table">
              <thead><tr><th>Kind</th><th>Username</th><th>Id</th><th>Actions</th></tr></thead>
              <tbody>
                {(credsQ.data as any[]).map((c: any) => (
                  <tr key={c.id}><td>{c.kind}</td><td>{c.username ?? "—"}</td><td style={{ fontFamily: "monospace", fontSize: 12 }}>{c.id.slice(0, 8)}…</td><td><button style={{ fontSize: 12 }} onClick={async () => { if (confirm(`Delete credential ${c.id.slice(0,8)}?`)) { await fetch(`/api/credentials/${c.id}`, { method: "DELETE" }); credsQ.refetch(); }}}>Delete</button></td></tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No credentials stored (secrets never displayed).</div>}
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
