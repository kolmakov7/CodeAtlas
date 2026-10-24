import { useState } from "react";
import { useRepositories, useRepositoryLinks, useRepositoryLinkActions } from "@/api/hooks";

const LINK_TYPES = ["depends_on", "calls", "references", "related"];

/**
 * Manage cross-repository links for one repository (human-in-the-loop declaration of
 * which projects are related). Links enable cross-repo relation resolution.
 */
export default function RepositoryLinksCard({ repositoryId }: { repositoryId: string }) {
  const reposQ = useRepositories();
  const linksQ = useRepositoryLinks(repositoryId);
  const actions = useRepositoryLinkActions();
  const [target, setTarget] = useState("");
  const [type, setType] = useState("depends_on");
  const [error, setError] = useState<string | null>(null);

  const repos = (reposQ.data ?? []).filter((r) => r.id !== repositoryId);
  const links = linksQ.data ?? [];

  const add = async () => {
    if (!target) return;
    setError(null);
    try {
      await actions.create.mutateAsync({ sourceRepositoryId: repositoryId, targetRepositoryId: target, linkType: type });
      setTarget("");
    } catch (e: any) {
      setError(e.response?.data?.title || e.message);
    }
  };

  return (
    <div className="card" data-testid="repo-links-card">
      <h3 style={{ marginTop: 0, marginBottom: 4 }}>Related repositories</h3>
      <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 12 }}>
        Declare which repositories this one depends on. Enables cross-repository relations
        (e.g. an app repo calling SQL objects in a DB repo) and better documentation callers.
      </div>

      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center", marginBottom: 12 }}>
        <span style={{ fontSize: 13, color: "var(--text-muted)" }}>This repo depends on</span>
        <select data-testid="link-target-select" value={target} onChange={(e) => setTarget(e.target.value)}>
          <option value="">Select repository…</option>
          {repos.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
        <select data-testid="link-type-select" value={type} onChange={(e) => setType(e.target.value)}>
          {LINK_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <button className="primary" data-testid="add-link" onClick={add} disabled={!target || actions.create.isPending}>
          {actions.create.isPending ? "Adding…" : "Add link"}
        </button>
      </div>
      {error && <div className="banner" style={{ marginBottom: 8 }}>⚠ {error}</div>}

      {linksQ.isLoading ? <div className="skeleton" style={{ height: 60 }} /> : links.length === 0 ? (
        <div style={{ color: "var(--text-muted)", fontSize: 13 }}>No links yet.</div>
      ) : (
        <table className="table">
          <thead><tr><th>From</th><th>Type</th><th>To</th><th /></tr></thead>
          <tbody>
            {links.map((l) => (
              <tr key={l.id} data-testid="link-row">
                <td>{l.sourceRepositoryName}</td>
                <td><span className="badge">{l.linkType}</span></td>
                <td>{l.targetRepositoryName}</td>
                <td style={{ textAlign: "right" }}>
                  <button className="danger" style={{ fontSize: 12 }} disabled={actions.remove.isPending}
                    onClick={() => actions.remove.mutate(l.id)}>Delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
