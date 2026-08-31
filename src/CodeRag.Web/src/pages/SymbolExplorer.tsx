import { useParams, useSearchParams, Link } from "react-router-dom";
import { useSymbolDefinition, useSymbolRelations, useSymbolImplementations, useSymbolCallers, useSymbolCallees, useFileContent } from "@/api/hooks";
import { SourceView } from "@/components/SourceView";

type Tab = "definition" | "references" | "implementations" | "callers" | "callees";
const tabs: { id: Tab; label: string }[] = [
  { id: "definition", label: "Definition" },
  { id: "references", label: "References" },
  { id: "implementations", label: "Implementations" },
  { id: "callers", label: "Callers" },
  { id: "callees", label: "Callees" },
];

export default function SymbolExplorer() {
  const { symbolId } = useParams<{ symbolId: string }>();
  const [sp, setSp] = useSearchParams();
  const tab = (sp.get("tab") as Tab) ?? "definition";

  const defQ = useSymbolDefinition(symbolId);
  const refsQ = useSymbolRelations(tab === "references" ? symbolId : undefined, "incoming");
  const implQ = useSymbolImplementations(tab === "implementations" ? symbolId : undefined);
  const callersQ = useSymbolCallers(tab === "callers" ? symbolId : undefined);
  const calleesQ = useSymbolCallees(tab === "callees" ? symbolId : undefined);

  const fileQ = useFileContent(
    tab === "definition" ? defQ.data?.repositoryId : undefined,
    tab === "definition" ? defQ.data?.filePath : undefined,
    tab === "definition" ? defQ.data?.startLine : undefined,
    tab === "definition" ? defQ.data?.endLine : undefined,
  );

  if (!symbolId) return <div>Missing symbol id</div>;
  if (defQ.isLoading) return <div className="skeleton" style={{ height: 200 }} />;
  if (defQ.isError || !defQ.data) return <div className="card">Symbol not found: {(defQ.error as Error)?.message ?? "—"} <Link to="/search">Back to search</Link></div>;

  const s = defQ.data;

  return (
    <div>
      <Link to="/search" style={{ fontSize: 13 }}>← Search</Link>
      <div className="card" style={{ marginTop: 12 }}>
        <h2 style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
          <span style={{ fontFamily: "ui-monospace, monospace" }}>{s.fqn ?? s.name}</span>
          <span className="badge">{s.kind}</span>
          <span style={{ fontSize: 12, color: "var(--text-muted)" }}>{s.language}</span>
        </h2>
        <div style={{ fontSize: 13, color: "var(--text-muted)", marginTop: 6, wordBreak: "break-all" }}>
          <div>File: <code>{s.filePath}:{s.startLine}-{s.endLine}</code> {s.namespace && <>· Namespace: <code>{s.namespace}</code></>}</div>
          {s.signature && <div style={{ marginTop: 4 }}>Signature: <code style={{ whiteSpace: "pre-wrap" }}>{s.signature}</code></div>}
        </div>
      </div>

      <div style={{ display: "flex", gap: 8, marginTop: 16, borderBottom: "1px solid var(--border)", paddingBottom: 8 }}>
        {tabs.map((t) => (
          <button
            key={t.id}
            onClick={() => setSp({ tab: t.id }, { replace: true })}
            className={tab === t.id ? "primary" : undefined}
            style={{ fontSize: 13 }}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div style={{ marginTop: 16 }}>
        {tab === "definition" && (
          fileQ.isLoading ? <div className="skeleton" style={{ height: 300 }} /> :
          fileQ.isError ? <div className="card" style={{ borderColor: "var(--danger)" }}>Failed: {(fileQ.error as Error).message}</div> :
          fileQ.data ? <SourceView content={fileQ.data.content} language={fileQ.data.language} startLine={fileQ.data.startLine} highlightLines={[s.startLine, s.endLine]} /> :
          <div className="card">No source available</div>
        )}
        {tab === "references" && <RelationList loading={refsQ.isLoading} error={refsQ.error as Error | null} items={refsQ.data?.map(r => r.fromSymbol ?? r.toSymbol).filter(Boolean) as never ?? []} empty="No references indexed for this symbol." />}
        {tab === "implementations" && <SymbolList loading={implQ.isLoading} error={implQ.error as Error | null} items={implQ.data ?? []} empty="No implementations — not an interface/abstract or none indexed." />}
        {tab === "callers" && <SymbolList loading={callersQ.isLoading} error={callersQ.error as Error | null} items={callersQ.data ?? []} empty="No callers indexed — not a method/function or none found." />}
        {tab === "callees" && <SymbolList loading={calleesQ.isLoading} error={calleesQ.error as Error | null} items={calleesQ.data ?? []} empty="No callees indexed." />}
      </div>

      <div style={{ marginTop: 16, fontSize: 12, color: "var(--text-muted)" }}>
        Diagnostics tool: shows what the analyzer actually indexed. If a relation is missing, check analyzer diagnostics / indexing errors.
      </div>
    </div>
  );
}

function SymbolList({ loading, error, items, empty }: { loading: boolean; error: Error | null; items: { id: string; name: string; fqn?: string | null; filePath: string; startLine: number; endLine: number; kind: string; language: string }[]; empty: string }) {
  if (loading) return <div className="skeleton" style={{ height: 120 }} />;
  if (error) return <div className="card" style={{ borderColor: "var(--danger)" }}>Error: {error.message}</div>;
  if (!items.length) return <div className="card" style={{ color: "var(--text-muted)" }}>{empty}</div>;
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      {items.map((sym) => (
        <Link key={sym.id} to={`/symbols/${sym.id}`} style={{ textDecoration: "none", color: "inherit" }}>
          <div className="card" style={{ display: "flex", justifyContent: "space-between", gap: 12 }}>
            <div>
              <div style={{ fontWeight: 600, fontFamily: "ui-monospace, monospace", fontSize: 13 }}>{sym.fqn ?? sym.name} <span className="badge" style={{ marginLeft: 6 }}>{sym.kind}</span></div>
              <div style={{ fontSize: 12, color: "var(--text-muted)" }}>{sym.filePath}:{sym.startLine}-{sym.endLine} · {sym.language}</div>
            </div>
          </div>
        </Link>
      ))}
    </div>
  );
}

function RelationList({ loading, error, items, empty }: { loading: boolean; error: Error | null; items: unknown[]; empty: string }) {
  if (loading) return <div className="skeleton" style={{ height: 120 }} />;
  if (error) return <div className="card" style={{ borderColor: "var(--danger)" }}>Error: {error.message}</div>;
  if (!items.length) return <div className="card" style={{ color: "var(--text-muted)" }}>{empty}</div>;
  return <SymbolList loading={false} error={null} items={items as never} empty={empty} />;
}
