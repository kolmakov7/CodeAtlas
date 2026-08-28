import { useDiagnostics } from "@/api/hooks";

function StatusBadge({ status }: { status: string }) {
  const color = status === "OK" ? "var(--success)" : status === "FAIL" ? "var(--danger)" : "var(--warning)";
  return <span className="badge" style={{ background: status === "OK" ? "#14532d" : status === "FAIL" ? "#4c1111" : "#422006", color, borderColor: color }}>{status}</span>;
}

export default function DiagnosticsPage() {
  const q = useDiagnostics();
  if (q.isLoading) return <div className="skeleton" style={{ height: 200 }} />;
  if (q.isError) return <div className="card" style={{ borderColor: "var(--danger)" }}>Failed: {(q.error as Error).message} <button onClick={() => q.refetch()} style={{ marginLeft: 8 }}>Retry</button></div>;
  const d = q.data!;
  const items: { name: string; status: string; detail?: string; meta?: string }[] = [
    { name: "Database", status: d.database.status, detail: d.database.detail },
    { name: "Vector index", status: d.vectorIndex.status, detail: d.vectorIndex.detail, meta: d.vectorIndex.count != null ? `${d.vectorIndex.count} vectors · ${d.vectorIndex.sizeBytes ? (d.vectorIndex.sizeBytes / 1024 / 1024).toFixed(1) + " MB" : ""}` : undefined },
    { name: "Embedding API", status: d.embeddingApi.status, detail: d.embeddingApi.detail, meta: d.embeddingApi.latencyMs != null ? `${d.embeddingApi.latencyMs}ms · ${d.embeddingApi.model ?? ""}` : undefined },
    { name: "Indexer", status: d.indexer.status, detail: d.indexer.detail, meta: d.indexer.queueDepth != null ? `queue depth ${d.indexer.queueDepth}` : undefined },
    { name: "Storage", status: d.storage.status, detail: d.storage.detail, meta: d.storage.freeBytes != null ? `${(d.storage.freeBytes / 1024 / 1024 / 1024).toFixed(1)} GB free` : undefined },
  ];
  const embeddingFail = d.embeddingApi.status === "FAIL";
  return (
    <div>
      <h1 style={{ marginBottom: 12 }}>Diagnostics</h1>
      {embeddingFail && (
        <div className="banner" style={{ marginBottom: 16 }}>
          Semantic search unavailable, FTS/structural continue — Embedding API is FAIL. Check Settings → Embeddings endpoint.
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
      <div style={{ marginTop: 16, fontSize: 12, color: "var(--text-muted)" }}>
        Polling every 10s. Health checks: SQLite open + vector count + embedding latency + indexer queue + disk free.
      </div>
    </div>
  );
}
