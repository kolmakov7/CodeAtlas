import { useEffect, useState } from "react";
import { useSettings, useUpdateSettings, useProxies, useCredentials, useRepositoryGroups } from "@/api/hooks";

type Tab = "repositories" | "indexing" | "embeddings" | "llm" | "revision" | "search" | "storage" | "system" | "proxies" | "credentials" | "groups";
const tabs: { id: Tab; label: string }[] = [
  { id: "repositories", label: "Repositories" },
  { id: "indexing", label: "Indexing" },
  { id: "embeddings", label: "Embeddings" },
  { id: "llm", label: "Documentation (LLM)" },
  { id: "revision", label: "Knowledge revision" },
  { id: "search", label: "Search" },
  { id: "storage", label: "Storage" },
  { id: "system", label: "System" },
  { id: "proxies", label: "Proxies" },
  { id: "credentials", label: "Credentials" },
  { id: "groups", label: "Groups" },
];

export default function SettingsPage() {
  const [tab, setTab] = useState<Tab>("repositories");
  const settingsQ = useSettings();
  const proxiesQ = useProxies();
  const credsQ = useCredentials();
  const groupsQ = useRepositoryGroups();
  const update = useUpdateSettings();
  const [proxyForm, setProxyForm] = useState({ name: "", scheme: "http", host: "", port: 8080, username: "", password: "" });
  const [credForm, setCredForm] = useState({ kind: "https_token", username: "", secret: "" });
  const [groupForm, setGroupForm] = useState({ name: "", proxyId: "", credentialId: "" });
  const [editingGroup, setEditingGroup] = useState<string | null>(null);
  const [editingProxy, setEditingProxy] = useState<string | null>(null);
  const [proxyError, setProxyError] = useState<string | null>(null);
  const [credError, setCredError] = useState<string | null>(null);
  const [groupError, setGroupError] = useState<string | null>(null);

  const [form, setForm] = useState<NonNullable<typeof settingsQ.data>>({});
  const [endpointError, setEndpointError] = useState<string | null>(null);
  const [detecting, setDetecting] = useState<string | null>(null);
  const [detectInfo, setDetectInfo] = useState<Record<string, string>>({});
  const [availableModels, setAvailableModels] = useState<Record<string, string[]>>({});

  useEffect(() => {
    if (settingsQ.data) setForm(structuredClone(settingsQ.data));
  }, [settingsQ.data]);

  if (settingsQ.isLoading) return <div className="skeleton" style={{ height: 300 }} />;
  if (settingsQ.isError) return <div className="card" style={{ borderColor: "var(--danger)" }}>Failed: {(settingsQ.error as Error).message} <button onClick={() => settingsQ.refetch()} style={{ marginLeft: 8 }}>Retry</button></div>;

  const isValidHost = (h: string) => {
    let s = h.trim();
    if (!s) return false;
    if (s.includes("://")) {
      try { const u = new URL(s); const host = u.hostname; if (!host) return false; s = host; } catch { return false; }
    }
    // IP or DNS
    if (/^\d{1,3}(\.\d{1,3}){3}$/.test(s)) {
      const parts = s.split(".").map(Number);
      return parts.every(p => p >= 0 && p <= 255);
    }
    // DNS: labels 1-63, alphanum + hyphen
    const labels = s.split(".");
    return labels.every(l => l.length > 0 && l.length <= 63 && /^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$/.test(l)) && s.length <= 253;
  };
  const isValidEndpoint = (e: string) => {
    try { const u = new URL(e.trim()); return u.protocol === "http:" || u.protocol === "https:"; } catch { return false; }
  };
  const save = () => {
    // Validate embedding endpoints before saving
    const codeEp = form.embeddings?.code?.endpoint;
    const textEp = form.embeddings?.text?.endpoint;
    if (codeEp && !isValidEndpoint(codeEp)) { setEndpointError(`Code endpoint must be http:// or https:// URL with valid host (e.g. http://192.168.1.10:8080/v1/embeddings) — got '${codeEp}'`); return; }
    if (textEp && !isValidEndpoint(textEp)) { setEndpointError(`Text endpoint must be http:// or https:// URL — got '${textEp}'`); return; }
    const llmEp = form.llm?.endpoint;
    if (llmEp && !isValidEndpoint(llmEp)) { setEndpointError(`Documentation LLM endpoint must be http:// or https:// URL — got '${llmEp}'`); return; }
    setEndpointError(null);
    update.mutate(form);
  };

  type ProfileField = "endpoint" | "model" | "batchSize" | "timeoutSeconds";
  const codeModels = ["bge-m3", "bge-m3-Q8_0", "jina-embeddings-v2-base-code", "all-MiniLM-L6-v2", "e5-mistral-7b-instruct"];
  const textModels = ["jina-embeddings-v5-text-small", "all-MiniLM-L6-v2", "bge-m3", "nomic-embed-text", "e5-base-v2"];
  const renderProfile = (domain: "code" | "text") => {
    const p = form.embeddings?.[domain];
    const baseModels = domain === "code" ? codeModels : textModels;
    // Merge static + auto-detected available models for this endpoint (supports several models per one address)
    const detectedForDomain = availableModels[domain] ?? [];
    const detectedForEndpoint = p?.endpoint ? (availableModels[p.endpoint] ?? []) : [];
    const models = Array.from(new Set([...baseModels, ...detectedForDomain, ...detectedForEndpoint]));
    const set = (field: ProfileField, v: string | number) =>
      setForm({ ...form, embeddings: { ...form.embeddings, [domain]: { ...form.embeddings?.[domain], [field]: v } } });
    const handleDetect = async () => {
      const ep = (p?.endpoint ?? "").trim();
      if (!ep || !isValidEndpoint(ep)) { setEndpointError(`Cannot auto-detect: endpoint must be a valid http(s) URL first`); return; }
      setDetecting(domain);
      setEndpointError(null);
      try {
        const res = await fetch("/api/embeddings/detect", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ endpoint: ep, model: p?.model || undefined }) });
        const data: any = await res.json();
        // Store all available models for this endpoint so both Code and Text can pick from same address
        if (data.availableModels && Array.isArray(data.availableModels) && data.availableModels.length) {
          setAvailableModels(prev => ({
            ...prev,
            [domain]: Array.from(new Set([...(prev[domain] ?? []), ...data.availableModels])),
            [ep]: data.availableModels
          }));
        }
        if (data.success && data.detectedModel) {
          set("model", data.detectedModel);
          const avail = data.availableModels?.length ? ` — доступно: ${data.availableModels.join(", ")}` : "";
          const info = `Определено: ${data.detectedModel}${data.detectedDimensions ? ` (${data.detectedDimensions} dim)` : ""}${data.detectedContextLength ? `, ctx ${data.detectedContextLength}` : ""}${avail}`;
          setDetectInfo(prev => ({ ...prev, [domain]: info }));
        } else if (data.success) {
          const avail = data.availableModels?.length ? ` Доступные модели: ${data.availableModels.join(", ")}` : "";
          setDetectInfo(prev => ({ ...prev, [domain]: (data.detectedDimensions ? `Размерность: ${data.detectedDimensions}` : "Авто-определение успешно, модель не указана") + avail }));
        } else {
          setDetectInfo(prev => ({ ...prev, [domain]: data.error || "Не удалось определить" }));
        }
      } catch (e: any) {
        setDetectInfo(prev => ({ ...prev, [domain]: e.message || "Ошибка" }));
      } finally { setDetecting(null); }
    };
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 10, padding: 12, border: "1px solid var(--border)", borderRadius: 6 }}>
        <label>Endpoint <input value={p?.endpoint ?? ""} onChange={(e) => set("endpoint", e.target.value)} placeholder={domain === "code" ? "http://code-embeddings:8080/v1/embeddings" : "http://text-embeddings:8080/v1/embeddings"} />
          <span style={{ fontSize: 11, color: "var(--text-muted)" }}>OpenAI-compatible endpoint URL (dimensions auto-detected)</span>
        </label>
        <label>{domain === "code" ? "Code embedding model" : "Text embedding model"}
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <input list={`${domain}-models`} value={p?.model ?? ""} onChange={(e) => set("model", e.target.value)} placeholder={domain === "code" ? "bge-m3" : "jina-embeddings-v5-text-small"} style={{ flex: 1 }} />
            <datalist id={`${domain}-models`}>
              {models.map(m => <option key={m} value={m} />)}
            </datalist>
            <button type="button" onClick={handleDetect} disabled={detecting === domain || !p?.endpoint} style={{ whiteSpace: "nowrap", fontSize: 12 }} title="Запросить у endpoint список моделей и размерность">
              {detecting === domain ? "Определение…" : "Определить автоматически"}
            </button>
          </div>
          <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{domain === "code" ? "Embeds source code (comments stay code)" : "Embeds Markdown / docs prose"} — можно ввести любое значение (custom) или выбрать из списка. Нажмите «Определить автоматически» чтобы запросить модель у endpoint.</span>
          {detectInfo[domain] && <span style={{ fontSize: 11, color: "var(--success)" }}>{detectInfo[domain]}</span>}
        </label>
        <label>Batch size <input type="number" value={p?.batchSize ?? 32} onChange={(e) => set("batchSize", Number(e.target.value))} />
          <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Texts per HTTP request (default 32)</span>
        </label>
        <label>Timeout (seconds) <input type="number" value={p?.timeoutSeconds ?? 30} onChange={(e) => set("timeoutSeconds", Number(e.target.value))} />
          <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Per-batch request timeout</span>
        </label>
      </div>
    );
  };

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
          <p style={{ fontSize: 12, color: "var(--text-muted)" }}>Default settings for newly created repositories. Proxy and credentials are now managed via Repository Groups.</p>
          <label>Default branch <input value={form.repositories?.defaultBranch ?? ""} onChange={(e) => setForm({ ...form, repositories: { ...form.repositories, defaultBranch: e.target.value } })} placeholder="main" /></label>
          <label>Sync interval (seconds) <input type="number" value={form.repositories?.defaultSyncIntervalSeconds ?? ""} onChange={(e) => setForm({ ...form, repositories: { ...form.repositories, defaultSyncIntervalSeconds: e.target.value ? Number(e.target.value) : null } })} />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Polling interval for git fetch (seconds), default 300</span>
          </label>
          <label style={{ display: "flex", gap: 8, alignItems: "center" }}><input type="checkbox" checked={form.repositories?.defaultEnabled ?? true} onChange={(e) => setForm({ ...form, repositories: { ...form.repositories, defaultEnabled: e.target.checked } })} style={{ width: "auto" }} /> Enabled by default</label>
        </div>
      )}
      {tab === "indexing" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <h3>Indexing — блок <code>Indexer</code> из <code>appsettings.json</code></h3>
          <p style={{ fontSize: 12, color: "var(--text-muted)" }}>Настройки из секции <code>Indexer</code> (<code>appsettings.json</code>). Переопределяются через таблицу <code>settings</code> и применяются без пересборки образа — <code>FireReload</code> после сохранения.</p>

          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
            <label>Sync interval (seconds) <input type="number" value={form.indexing?.syncIntervalSeconds ?? ""} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, syncIntervalSeconds: e.target.value ? Number(e.target.value) : null } })} placeholder="300" />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:DefaultSyncIntervalSeconds</code> — polling git fetch, default 300</span>
            </label>
            <label>Max concurrent indexers <input type="number" value={form.indexing?.maxConcurrentIndexers ?? 2} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, maxConcurrentIndexers: Number(e.target.value) } })} placeholder="2" />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:MaxConcurrentIndexers</code> — parallel jobs (SemaphoreSlim), из <code>appsettings.json</code></span>
            </label>
            <label>Parallel file indexers <input type="number" value={form.indexing?.parallelFileIndexers ?? 8} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, parallelFileIndexers: Number(e.target.value) } })} placeholder="8" />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:ParallelFileIndexers</code> — параллельность внутри джобы (Parallel.ForEachAsync), default 8, из <code>appsettings.json</code></span>
            </label>
            <label>File watcher debounce (ms) <input type="number" value={form.indexing?.fileWatcherDebounceMs ?? 1500} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, fileWatcherDebounceMs: Number(e.target.value) } })} placeholder="1500" />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:FileWatcherDebounceMs</code> — debounce FS watcher</span>
            </label>
          </div>

          <label style={{ display: "flex", gap: 8, alignItems: "center" }}><input type="checkbox" checked={form.indexing?.enableFileWatcher ?? false} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, enableFileWatcher: e.target.checked } })} style={{ width: "auto" }} /> Enable file watcher (dev only)
            <span style={{ fontSize: 11, color: "var(--text-muted)", marginLeft: 8 }}><code>Indexer:EnableFileWatcher</code> — только для локальной разработки</span>
          </label>

          <div style={{ borderTop: "1px solid var(--border)", paddingTop: 12, display: "flex", flexDirection: "column", gap: 12 }}>
            <h4 style={{ fontSize: 13, fontWeight: 600 }}>Chunk budget (RAG)</h4>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
              <label>Max chunk chars <input type="number" value={form.indexing?.maxChunkChars ?? 1500} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, maxChunkChars: Number(e.target.value) } })} placeholder="1500" />
                <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:MaxChunkChars</code> — target max chars, default 1500</span>
              </label>
              <label>Min embedding chars <input type="number" value={form.indexing?.minEmbeddingChars ?? 32} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, minEmbeddingChars: Number(e.target.value) } })} placeholder="32" />
                <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:MinEmbeddingChars</code> — skip embedding for tiny chunks</span>
              </label>
              <label>Chunk overlap fraction <input type="number" step="0.01" min={0} max={0.5} value={form.indexing?.chunkOverlapFraction ?? 0.15} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, chunkOverlapFraction: Number(e.target.value) } })} placeholder="0.15" />
                <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:ChunkOverlapFraction</code> — overlap 0.15</span>
              </label>
            </div>
          </div>

          <div style={{ borderTop: "1px solid var(--border)", paddingTop: 12, display: "flex", flexDirection: "column", gap: 12 }}>
            <h4 style={{ fontSize: 13, fontWeight: 600 }}>Фильтры</h4>
            <label>Exclusions (glob, comma separated) <input value={(form.indexing?.exclusions ?? []).join(", ")} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, exclusions: e.target.value.split(",").map(s => s.trim()).filter(Boolean) } })} placeholder="**/bin/**, **/obj/**, **/node_modules/**" />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:Exclusions</code></span>
            </label>
            <label>Language exclusions <input value={(form.indexing?.languageExclusions ?? []).join(", ")} onChange={(e) => setForm({ ...form, indexing: { ...form.indexing, languageExclusions: e.target.value.split(",").map(s => s.trim()).filter(Boolean) } })} placeholder="VBS, ..." />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}><code>Indexer:LanguageExclusions</code></span>
            </label>
          </div>
        </div>
      )}
      {tab === "embeddings" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Embeddings</h3>
          <div style={{ fontSize: 12, color: "var(--text-muted)" }}>Two independent OpenAI-compatible endpoints, one per vector space. Code embeddings embed source code (comments stay code); text embeddings embed Markdown, README and docs prose.</div>
          <div>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 6 }}>Code</div>
            {renderProfile("code")}
          </div>
          <div>
            <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 6 }}>Text</div>
            {renderProfile("text")}
          </div>
          {endpointError && <div style={{ color: "var(--danger)", fontSize: 12 }}>{endpointError}</div>}
          <div style={{ fontSize: 11, color: "var(--text-muted)" }}>Endpoint must be a valid http:// or https:// URL with a DNS name or IP (e.g. http://192.168.1.10:8080/v1/embeddings or https://api.example.com/v1/embeddings).</div>
        </div>
      )}
      {tab === "llm" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Documentation (LLM)</h3>
          <p style={{ fontSize: 12, color: "var(--text-muted)" }}>
            OpenAI-compatible chat endpoint used to generate AI documentation. Leave the endpoint empty to disable documentation generation.
            Default is the local gateway: <code>{form.llm?.endpoint || "—"}</code> with model <code>{form.llm?.model || "—"}</code>.
          </p>
          <div style={{ fontSize: 12, color: form.llm?.configured ? "var(--success)" : "var(--danger)" }}>
            {form.llm?.configured ? "Configured — generation enabled." : "Not configured — generation will fail."}
          </div>
          <label>Endpoint
            <input value={form.llm?.endpoint ?? ""} onChange={(e) => setForm({ ...form, llm: { ...form.llm, endpoint: e.target.value } })} placeholder="http://192.168.137.25:4000/v1" />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Base URL or .../chat/completions. Empty = documentation disabled.</span>
          </label>
          <label>Model
            <input value={form.llm?.model ?? ""} onChange={(e) => setForm({ ...form, llm: { ...form.llm, model: e.target.value } })} placeholder="qwen3.8:27b" />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Sent as <code>model</code> in the request body.</span>
          </label>
          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr 1fr", gap: 12 }}>
            <label>Temperature <input type="number" step="0.1" min={0} max={2} value={form.llm?.temperature ?? 0.2} onChange={(e) => setForm({ ...form, llm: { ...form.llm, temperature: Number(e.target.value) } })} />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Sampling temperature</span>
            </label>
            <label>Max tokens <input type="number" value={form.llm?.maxTokens ?? 4096} onChange={(e) => setForm({ ...form, llm: { ...form.llm, maxTokens: Number(e.target.value) } })} />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}>0 = server decides</span>
            </label>
            <label>Timeout (seconds) <input type="number" value={form.llm?.timeoutSeconds ?? 300} onChange={(e) => setForm({ ...form, llm: { ...form.llm, timeoutSeconds: Number(e.target.value) } })} />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Per request</span>
            </label>
            <label>Max retries <input type="number" value={form.llm?.maxRetries ?? 2} onChange={(e) => setForm({ ...form, llm: { ...form.llm, maxRetries: Number(e.target.value) } })} />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Retries on transient failure</span>
            </label>
            <label>Max context chars <input type="number" value={form.llm?.maxContextChars ?? 60000} onChange={(e) => setForm({ ...form, llm: { ...form.llm, maxContextChars: Number(e.target.value) } })} />
              <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Cap on prompt context (0 = unlimited)</span>
            </label>
          </div>
          {endpointError && <div style={{ color: "var(--danger)", fontSize: 12 }}>{endpointError}</div>}
        </div>
      )}
      {tab === "revision" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <h3>Knowledge revision</h3>
          <label style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <input type="checkbox" checked={form.revision?.enabled ?? false} onChange={(e) => setForm({ ...form, revision: { ...form.revision, enabled: e.target.checked } })} />
            Enable periodic revision (never calls the LLM automatically)
          </label>
          <label>Interval days <input type="number" value={form.revision?.intervalDays ?? 7} onChange={(e) => setForm({ ...form, revision: { ...form.revision, intervalDays: Number(e.target.value) } })} /></label>
          <label>Check every N minutes <input type="number" value={form.revision?.checkMinutes ?? 15} onChange={(e) => setForm({ ...form, revision: { ...form.revision, checkMinutes: Number(e.target.value) } })} /></label>
          <label>Max entries per run <input type="number" value={form.revision?.maxEntriesPerRun ?? 500} onChange={(e) => setForm({ ...form, revision: { ...form.revision, maxEntriesPerRun: Number(e.target.value) } })} /></label>
          <label>Similarity threshold <input type="number" step="0.01" value={form.revision?.similarityThreshold ?? 0.8} onChange={(e) => setForm({ ...form, revision: { ...form.revision, similarityThreshold: Number(e.target.value) } })} /></label>
          <label>Stale after days <input type="number" value={form.revision?.staleDays ?? 180} onChange={(e) => setForm({ ...form, revision: { ...form.revision, staleDays: Number(e.target.value) } })} /></label>
          <label style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <input type="checkbox" checked={form.revision?.toolsEnabled ?? false} onChange={(e) => setForm({ ...form, revision: { ...form.revision, toolsEnabled: e.target.checked } })} />
            Expose revision MCP tools (hides them from the agent context by default)
          </label>
        </div>
      )}
      {tab === "search" && (
        <div className="card" style={{ display: "flex", flexDirection: "column", gap: 14 }}>
          <h3>Search weights</h3>
          <p style={{ fontSize: 12, color: "var(--text-muted)" }}>Hybrid search fuses three signals. Adjust weights to bias toward keyword matching, semantic similarity, or graph structure. Sum need not be 1.0.</p>
          <label>Lexical weight (FTS5 trigram, 0–1, default 0.4) <input type="number" step="0.1" min={0} max={1} value={form.search?.lexicalWeight ?? 0.4} onChange={(e) => setForm({ ...form, search: { ...form.search, lexicalWeight: Number(e.target.value) } })} />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Weight of full-text trigram matching (chunks_fts). Higher = exact keyword matches rank higher. Applied after per-query normalization.</span>
          </label>
          <label>Semantic weight (vector cosine, 0–1, default 0.4) <input type="number" step="0.1" min={0} max={1} value={form.search?.semanticWeight ?? 0.4} onChange={(e) => setForm({ ...form, search: { ...form.search, semanticWeight: Number(e.target.value) } })} />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Weight of embedding cosine similarity (code/text vector stores). Higher = semantic similarity dominates. Text domain is down-weighted internally (×0.6) to avoid doc noise.</span>
          </label>
          <label>Structural weight (graph, 0–1, default 0.2) <input type="number" step="0.1" min={0} max={1} value={form.search?.structuralWeight ?? 0.2} onChange={(e) => setForm({ ...form, search: { ...form.search, structuralWeight: Number(e.target.value) } })} />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Weight of graph expansion (relations: Calls/Implements etc). Adds chunks related via symbol graph to exact matches. Only active in Hybrid mode.</span>
          </label>
          <label>Default result count (integer, default 20) <input type="number" value={form.search?.defaultResultCount ?? 20} onChange={(e) => setForm({ ...form, search: { ...form.search, defaultResultCount: Number(e.target.value) } })} />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Number of hits returned when client omits limit. Capped by MaxResultCount (100) on backend.</span>
          </label>
          <label>Min semantic score (cosine 0–1, default 0.30) <input type="number" step="0.01" min={0} max={1} value={form.search?.minSemanticScore ?? 0.30} onChange={(e) => setForm({ ...form, search: { ...form.search, minSemanticScore: Number(e.target.value) } })} />
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>Absolute cosine floor for vector candidates (code 0.30, text 0.55). Below this, matches are dropped so weak/ambiguous queries return no results instead of “least-irrelevant”. Applied live per query — no rebuild. Set 0 to disable filter.</span>
          </label>
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
              <input placeholder="Name" value={proxyForm.name} onChange={e => setProxyForm({ ...proxyForm, name: e.target.value })} style={{ flex: "3 1 0%", width: "75%" }} />
              <select value={proxyForm.scheme} onChange={e => setProxyForm({ ...proxyForm, scheme: e.target.value })} style={{ flex: "1 1 0%", width: "25%" }}>
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
            <div style={{ display: "flex", gap: 8 }}>
              <button className="primary" onClick={async () => {
                setProxyError(null);
                if (!proxyForm.host || !isValidHost(proxyForm.host)) { setProxyError("Host must be a valid DNS name (e.g. proxy.example.com) or IP (e.g. 192.168.1.10), optionally with http:// or https:// prefix"); return; }
                try {
                  if (editingProxy) {
                    const r = await fetch(`/api/proxies/${editingProxy}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: proxyForm.name, scheme: proxyForm.scheme, host: proxyForm.host, port: proxyForm.port, username: proxyForm.username || undefined, password: proxyForm.password || undefined }) });
                    if (!r.ok) throw new Error(await r.text());
                    setEditingProxy(null);
                  } else {
                    const r = await fetch("/api/proxies", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: proxyForm.name, scheme: proxyForm.scheme, host: proxyForm.host, port: proxyForm.port, username: proxyForm.username || undefined, password: proxyForm.password || undefined }) });
                    if (!r.ok) throw new Error(await r.text());
                  }
                  setProxyForm({ name: "", scheme: "http", host: "", port: 8080, username: "", password: "" });
                  proxiesQ.refetch();
                } catch (e: any) { setProxyError(e.message); }
              }}>{editingProxy ? "Update Proxy" : "Add Proxy"}</button>
              {editingProxy && <button onClick={() => { setEditingProxy(null); setProxyForm({ name: "", scheme: "http", host: "", port: 8080, username: "", password: "" }); setProxyError(null); }}>Cancel</button>}
            </div>
          </div>
          {proxiesQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : proxiesQ.data?.length ? (
            <div style={{ overflowX: "auto" }}>
              <table className="table" style={{ width: "100%", minWidth: 520 }}>
                <colgroup>
                  <col style={{ width: "28%" }} />
                  <col style={{ width: "12%" }} />
                  <col style={{ width: "30%" }} />
                  <col style={{ width: "10%" }} />
                  <col style={{ width: "20%" }} />
                </colgroup>
                <thead><tr><th>Name</th><th>Type</th><th>Host</th><th>Port</th><th>Actions</th></tr></thead>
                <tbody>
                  {(proxiesQ.data as any[]).map((p: any) => (
                    <tr key={p.id}>
                      <td style={{ wordBreak: "break-all", whiteSpace: "normal" }} title={p.name}>{p.name}</td>
                      <td style={{ fontSize: 12, color: "var(--text-muted)", whiteSpace: "nowrap" }}>{p.scheme}</td>
                      <td style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={p.host}>{p.host}</td>
                      <td>{p.port}</td>
                      <td>
                        <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
                          <button style={{ fontSize: 12, whiteSpace: "nowrap" }} onClick={() => { setEditingProxy(p.id); setProxyForm({ name: p.name, scheme: (p.scheme || "http").toLowerCase(), host: p.host, port: p.port, username: p.username || "", password: "" }); setProxyError(null); window.scrollTo({ top: 0, behavior: "smooth" }); }}>Edit</button>
                          <button style={{ fontSize: 12, whiteSpace: "nowrap" }} onClick={() => fetch(`/api/proxies/${p.id}/test`, { method: "POST" }).then(async r => alert(await r.text())).catch(e => alert(e.message))}>Test</button>
                          <button style={{ fontSize: 12, whiteSpace: "nowrap" }} onClick={async () => { if (confirm(`Delete proxy ${p.name}?`)) { const r = await fetch(`/api/proxies/${p.id}`, { method: "DELETE" }); if (!r.ok) { const t = await r.text(); alert(t); } proxiesQ.refetch(); }}}>Delete</button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No proxies configured.</div>}
        </div>
      )}
      {tab === "credentials" && (
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Credentials — GitHub / Git login</h3>
          <p style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Supports PAT (https_token) and login + password (https_userpass). For GitHub, https_userpass uses login as Username and password (or PAT as password) as Secret. The credential is then assigned to a Repository Group.</p>
          <div style={{ display: "flex", flexDirection: "column", gap: 8, marginBottom: 12, padding: 12, border: "1px solid var(--border)", borderRadius: 6 }}>
            <label>Kind
              <select value={credForm.kind} onChange={e => setCredForm({ ...credForm, kind: e.target.value })}>
                <option value="https_token">https_token — PAT (GitHub personal access token)</option>
                <option value="https_userpass">https_userpass — Login + Password (GitHub login / password)</option>
                <option value="ssh_key">ssh_key — SSH private key</option>
              </select>
            </label>
            <label>{credForm.kind === "https_userpass" ? "GitHub login (Username)" : credForm.kind === "ssh_key" ? "Username (optional, for ssh)" : "Username (optional, for https_token)"}
              <input placeholder={credForm.kind === "https_userpass" ? "your-github-login" : "username (optional)"} value={credForm.username} onChange={e => setCredForm({ ...credForm, username: e.target.value })} />
              {credForm.kind === "https_userpass" && <span style={{ fontSize: 11, color: "var(--text-muted)" }}>GitHub login — required for https_userpass</span>}
            </label>
            <label>{credForm.kind === "https_userpass" ? "Password" : credForm.kind === "https_token" ? "Token / PAT" : "Private Key or path"}
              <input placeholder={credForm.kind === "https_userpass" ? "password" : credForm.kind === "https_token" ? "ghp_... or PAT" : "-----BEGIN PRIVATE KEY----- or /path/to/key"} type="password" value={credForm.secret} onChange={e => setCredForm({ ...credForm, secret: e.target.value })} />
              {credForm.kind === "https_userpass" && <span style={{ fontSize: 11, color: "var(--text-muted)" }}>GitHub password — for GitHub, can also be a PAT used as password with your login</span>}
            </label>
            {credError && <div style={{ color: "var(--danger)", fontSize: 12 }}>{credError}</div>}
            <button className="primary" onClick={async () => {
              setCredError(null);
              try {
                if (credForm.kind === "https_userpass" && (!credForm.username || !credForm.secret)) throw new Error("For https_userpass, both login and password are required for GitHub");
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
                  <tr key={c.id}><td>{c.kind}</td><td>{c.username ?? "—"}</td><td style={{ fontFamily: "monospace", fontSize: 12 }}>{c.id.slice(0, 8)}…</td><td><button style={{ fontSize: 12 }} onClick={async () => { if (confirm(`Delete credential ${c.id.slice(0,8)}?`)) { const r = await fetch(`/api/credentials/${c.id}`, { method: "DELETE" }); if (!r.ok) { const t = await r.text(); alert(t); } credsQ.refetch(); }}}>Delete</button></td></tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No credentials stored (secrets never displayed).</div>}
        </div>
      )}
      {tab === "groups" && (
        <div className="card">
          <h3 style={{ marginBottom: 8 }}>Repository Groups</h3>
          <p style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 12 }}>Each repository belongs to one group. The group defines which Proxy and Credentials the repository uses. Default group exists automatically.</p>
          <div style={{ display: "flex", flexDirection: "column", gap: 8, marginBottom: 12, padding: 12, border: "1px solid var(--border)", borderRadius: 6 }}>
            <input placeholder="Group name" value={groupForm.name} onChange={e => setGroupForm({ ...groupForm, name: e.target.value })} />
            <div style={{ display: "flex", gap: 8 }}>
              <select value={groupForm.proxyId} onChange={e => setGroupForm({ ...groupForm, proxyId: e.target.value })} style={{ flex: 1 }}>
                <option value="">No proxy</option>
                {(proxiesQ.data as any[] ?? []).map((p: any) => <option key={p.id} value={p.id}>{p.name} ({p.scheme})</option>)}
              </select>
              <select value={groupForm.credentialId} onChange={e => setGroupForm({ ...groupForm, credentialId: e.target.value })} style={{ flex: 1 }}>
                <option value="">No credentials</option>
                {(credsQ.data as any[] ?? []).map((c: any) => <option key={c.id} value={c.id}>{c.kind} {c.username ? `(${c.username})` : ""} {c.id.slice(0, 6)}</option>)}
              </select>
            </div>
            {groupError && <div style={{ color: "var(--danger)", fontSize: 12 }}>{groupError}</div>}
            <div style={{ display: "flex", gap: 8 }}>
              <button className="primary" onClick={async () => {
                setGroupError(null);
                try {
                  if (editingGroup) {
                    const r = await fetch(`/api/repository-groups/${editingGroup}`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: groupForm.name || undefined, proxyId: groupForm.proxyId ? groupForm.proxyId : (groupForm.proxyId === "" ? "00000000-0000-0000-0000-000000000000" : undefined), credentialId: groupForm.credentialId ? groupForm.credentialId : (groupForm.credentialId === "" ? "00000000-0000-0000-0000-000000000000" : undefined) }) });
                    // Use explicit null clearing: send empty GUID to clear
                    if (!r.ok) throw new Error(await r.text());
                    setEditingGroup(null);
                  } else {
                    const r = await fetch("/api/repository-groups", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: groupForm.name, proxyId: groupForm.proxyId || undefined, credentialId: groupForm.credentialId || undefined }) });
                    if (!r.ok) throw new Error(await r.text());
                  }
                  setGroupForm({ name: "", proxyId: "", credentialId: "" });
                  groupsQ.refetch();
                } catch (e: any) { setGroupError(e.message); }
              }}>{editingGroup ? "Update Group" : "Add Group"}</button>
              {editingGroup && <button onClick={() => { setEditingGroup(null); setGroupForm({ name: "", proxyId: "", credentialId: "" }); }}>Cancel</button>}
            </div>
          </div>
          {groupsQ.isLoading ? <div className="skeleton" style={{ height: 80 }} /> : (groupsQ.data as any[])?.length ? (
            <table className="table">
              <thead><tr><th>Name</th><th>Proxy</th><th>Credentials</th><th>Actions</th></tr></thead>
              <tbody>
                {(groupsQ.data as any[]).map((g: any) => (
                  <tr key={g.id}>
                    <td>{g.name}</td>
                    <td style={{ fontSize: 12 }}>{g.proxyId ? ((proxiesQ.data as any[] ?? []).find((p: any) => p.id === g.proxyId)?.name ?? g.proxyId.slice(0, 6)) : "—"}</td>
                    <td style={{ fontSize: 12 }}>{g.credentialId ? ((credsQ.data as any[] ?? []).find((c: any) => c.id === g.credentialId)?.kind ?? g.credentialId.slice(0, 6)) : "—"}</td>
                    <td style={{ display: "flex", gap: 4 }}>
                      <button style={{ fontSize: 12 }} onClick={() => { setEditingGroup(g.id); setGroupForm({ name: g.name, proxyId: g.proxyId ?? "", credentialId: g.credentialId ?? "" }); }}>Edit</button>
                      <button style={{ fontSize: 12 }} disabled={g.name === "Default"} title={g.name === "Default" ? "Cannot delete Default" : ""} onClick={async () => { if (confirm(`Delete group ${g.name}?`)) { const r = await fetch(`/api/repository-groups/${g.id}`, { method: "DELETE" }); if (!r.ok) { const t = await r.text(); alert(t); } else groupsQ.refetch(); }}}>Delete</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <div style={{ color: "var(--text-muted)", fontSize: 14 }}>No groups — Default will be created automatically.</div>}
        </div>
      )}

      {(tab !== "proxies" && tab !== "credentials" && tab !== "groups") && (
        <div style={{ marginTop: 16, display: "flex", gap: 8 }}>
          <button className="primary" onClick={save} disabled={update.isPending}>{update.isPending ? "Saving…" : "Save"}</button>
          {update.isSuccess && <span style={{ color: "var(--success)", fontSize: 13, alignSelf: "center" }}>Saved</span>}
          {update.isError && <span style={{ color: "var(--danger)", fontSize: 13, alignSelf: "center" }}>{(update.error as Error).message}</span>}
        </div>
      )}
    </div>
  );
}
