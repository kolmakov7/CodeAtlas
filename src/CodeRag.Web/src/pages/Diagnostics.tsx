import { useDiagnostics } from "@/api/hooks";

function StatusBadge({ status }: { status: string }) {
  const color = status === "OK" ? "var(--success)" : status === "FAIL" ? "var(--danger)" : "var(--warning)";
  return <span className="badge" style={{ background: status === "OK" ? "#14532d" : status === "FAIL" ? "#4c1111" : "#422006", color, borderColor: color }}>{status}</span>;
}

export default function DiagnosticsPage() {
  const q = useDiagnostics();
  if (q.isLoading) return <div className="skeleton" style={{ height: 200 }} />;
  if (q.isError) return <div className="card" style={{ borderColor: "var(--danger)" }}>Failed: {(q.error as Error).message} <button onClick={() => q.refetch()} style={{ marginLeft: 8 }}>Retry</button></div>;
  const d = q.data! as any;
  const vi = d.vectorIndex;
  const viMeta: string[] = [];
  if (vi.count != null) viMeta.push(`${vi.count} vectors`);
  if (vi.currentDim != null) viMeta.push(`current model ${vi.currentModel ?? "?"} · ${vi.currentDim} dim`);
  if (vi.stored?.length) viMeta.push(`stored: ${vi.stored.map((m: any) => `${m.model ?? "?"}@${m.dim} (${m.chunkCount})`).join(", ")}`);
  const stale = vi.stale === true;
  const items: { name: string; status: string; detail?: string; meta?: string }[] = [
    { name: "Database", status: d.database.status, detail: d.database.detail },
    { name: "Vector index (aggregate)", status: stale ? "FAIL" : vi.status, detail: stale ? (vi.reason ?? undefined) : vi.detail, meta: viMeta.length ? viMeta.join(" · ") : undefined },
    { name: "Embedding API (aggregate)", status: d.embeddingApi.status, detail: d.embeddingApi.detail, meta: d.embeddingApi.latencyMs != null ? `${d.embeddingApi.latencyMs}ms · ${d.embeddingApi.model ?? ""}` : undefined },
    { name: "Indexer", status: d.indexer.status, detail: d.indexer.detail, meta: d.indexer.queueDepth != null ? `queue depth ${d.indexer.queueDepth}` : undefined },
    { name: "Storage", status: d.storage.status, detail: d.storage.detail, meta: d.storage.freeBytes != null ? `${(d.storage.freeBytes / 1024 / 1024 / 1024).toFixed(1)} GB free` : undefined },
  ];
  // Per-domain details (Task 8): show Text and Code models separately
  const vectorDomains: any[] = d.vectorDomains ?? [];
  const embeddingDomains: any[] = d.embeddingDomains ?? [];
  const embeddingFail = d.embeddingApi.status === "FAIL";
  return (
    <div>
      <h1 style={{ marginBottom: 12 }}>Diagnostics</h1>
      {embeddingFail && (
        <div className="banner" style={{ marginBottom: 16 }}>
          Semantic search unavailable, FTS/structural continue — Embedding API is FAIL. Check Settings → Embeddings endpoint.
        </div>
      )}
      {stale && (
        <div className="banner" style={{ marginBottom: 16 }}>
          Vector index is stale — {vi.reason ?? "stored vectors don't match the current embedding model."} Rebuild from the Dashboard to re-embed with the current model.
        </div>
      )}
      <div className="grid" style={{ gap: 12 }}>
        {items.map((it) => (
          <div key={it.name} className="card" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
            <div>
              <div style={{ fontWeight: 600 }}>{it.name}</div>
              {it.meta && <div style={{ fontSize: 12, color: "var(--text-muted)" }}>{it.meta}</div>}
              {it.detail && <div style={{ fontSize: 12, color: "var(--text-muted)", marginTop: 4 }}>{it.detail}</div>}
            </div>
            <StatusBadge status={it.status} />
          </div>
        ))}
      </div>

      {(vectorDomains.length > 0 || embeddingDomains.length > 0) && (
        <div style={{ marginTop: 16 }}>
          <h3 style={{ marginBottom: 8 }}>Per-domain details</h3>
          <div className="grid grid-2" style={{ gap: 12 }}>
            {vectorDomains.map((vd: any) => (
              <div key={"vec-" + vd.domain} className="card" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div>
                  <div style={{ fontWeight: 600 }}>Vector: {vd.domain}</div>
                  {vd.stale && <div style={{ fontSize: 12, color: "var(--danger)" }}>Stale: {vd.reason}</div>}
                  {vd.detail && <div style={{ fontSize: 12, color: "var(--text-muted)" }}>{vd.detail}</div>}
                  {vd.stored?.length ? <div style={{ fontSize: 11, color: "var(--text-muted)" }}>Stored: {vd.stored.map((m: any) => `${m.model ?? "?"}@${m.dim} (${m.chunkCount})`).join(", ")} current {vd.currentModel ?? "?"}@{vd.currentDim ?? "?"}</div> : null}
                  {vd.latencyMs != null && <div style={{ fontSize: 11, color: "var(--text-muted)" }}>{vd.latencyMs.toFixed(0)} ms</div>}
                </div>
                <StatusBadge status={vd.status} />
              </div>
            ))}
            {embeddingDomains.map((ed: any) => (
              <div key={"emb-" + ed.domain} className="card" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <div>
                  <div style={{ fontWeight: 600 }}>Embedding: {ed.domain}</div>
                  <div style={{ fontSize: 12, color: "var(--text-muted)" }}>{ed.model ?? "?"} · {ed.dimensions ?? "?"} dim {ed.latencyMs != null ? `· ${ed.latencyMs.toFixed(0)}ms` : ""}</div>
                  {ed.detail && <div style={{ fontSize: 12, color: "var(--text-muted)" }}>{ed.detail}</div>}
                </div>
                <StatusBadge status={ed.status} />
              </div>
            ))}
          </div>
        </div>
      )}
      <div style={{ marginTop: 16, fontSize: 12, color: "var(--text-muted)" }}>
        Polling every 10s. Health checks: SQLite open + vector count + embedding latency + indexer queue + disk free.
      </div>
    </div>
  );
}
