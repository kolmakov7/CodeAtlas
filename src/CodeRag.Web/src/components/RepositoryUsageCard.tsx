import { useRepositoryUsage } from "@/api/hooks";
import { formatDate } from "@/utils/format";

/**
 * Top-used objects in a repository, based on recorded usage (search/lookup hits and
 * cross-repo references). This is the data that drives usage-prioritised documentation.
 */
export default function RepositoryUsageCard({ repositoryId }: { repositoryId: string }) {
  const usageQ = useRepositoryUsage(repositoryId, 30);
  const rows = usageQ.data ?? [];

  return (
    <div className="card" data-testid="usage-card">
      <h3 style={{ marginTop: 0, marginBottom: 4 }}>Top used objects</h3>
      <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 12 }}>
        Recorded usage over the last 30 days (search/lookup + cross-repo references). Used to
        prioritise documentation generation.
      </div>
      {usageQ.isLoading ? <div className="skeleton" style={{ height: 60 }} /> : rows.length === 0 ? (
        <div style={{ color: "var(--text-muted)", fontSize: 13 }} data-testid="usage-empty">
          No usage recorded yet. Search for symbols or generate documentation to populate this.
        </div>
      ) : (
        <table className="table">
          <thead><tr><th>Object</th><th>Kind</th><th>Uses</th><th>Last used</th></tr></thead>
          <tbody>
            {rows.map((u) => (
              <tr key={u.symbolId} data-testid="usage-row">
                <td style={{ wordBreak: "break-all" }}>
                  <div>{u.name}</div>
                  {u.fqn && <div style={{ fontFamily: "monospace", fontSize: 11, color: "var(--text-muted)" }}>{u.fqn}</div>}
                </td>
                <td><span className="badge">{u.kind}</span></td>
                <td data-testid="usage-count">{u.usageCount}</td>
                <td style={{ fontSize: 12, color: "var(--text-muted)", whiteSpace: "nowrap" }}>{formatDate(u.lastUsedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
