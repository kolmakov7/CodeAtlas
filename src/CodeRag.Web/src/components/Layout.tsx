import { NavLink, Outlet, useLocation } from "react-router-dom";
import { useDiagnostics } from "@/api/hooks";

const nav = [
  { to: "/", label: "Dashboard", icon: "▣" },
  { to: "/repositories", label: "Repositories", icon: "📁" },
  { to: "/search", label: "Search", icon: "🔍" },
  { to: "/jobs", label: "Jobs", icon: "⚙" },
  { to: "/settings", label: "Settings", icon: "⚙" },
  { to: "/diagnostics", label: "Diagnostics", icon: "♥" },
];

export default function Layout() {
  const { data: diag } = useDiagnostics();
  const location = useLocation();
  const embeddingFail = diag?.embeddingApi.status === "FAIL";
  return (
    <div className="layout">
      <nav className="sidebar">
        <div style={{ fontWeight: 800, fontSize: 18, marginBottom: 12 }}>
          CodeRAG <span style={{ color: "var(--text-muted)", fontWeight: 400, fontSize: 12 }}>Atlas</span>
        </div>
        {nav.map((n) => (
          <NavLink
            key={n.to}
            to={n.to}
            className={({ isActive }) => (isActive && (location.pathname === n.to || (n.to !== "/" && location.pathname.startsWith(n.to))) ? "active" : "")}
          >
            <span style={{ marginRight: 8 }}>{n.icon}</span>
            <span className="label">{n.label}</span>
          </NavLink>
        ))}
        <div style={{ marginTop: "auto", fontSize: 12, color: "var(--text-muted)" }}>
          <div style={{ borderTop: "1px solid var(--border)", paddingTop: 12 }}>
            API: <code>/api</code>
          </div>
        </div>
      </nav>
      <div style={{ flex: 1, display: "flex", flexDirection: "column", minWidth: 0 }}>
        {embeddingFail && (
          <div className="banner" style={{ margin: 16, marginBottom: 0 }}>
            ⚠ Semantic search unavailable — Embedding API <strong>FAIL</strong> ({diag?.embeddingApi.detail ?? "check Settings → Embeddings"}). FTS / structural search continues.
          </div>
        )}
        <main className="main">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
