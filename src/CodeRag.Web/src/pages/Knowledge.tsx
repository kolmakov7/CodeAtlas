import { useMemo, useState } from "react";
import {
  useProjects,
  useRepositories,
  useKnowledgeList,
  useKnowledgeSearch,
  useKnowledge,
  useKnowledgeActions,
} from "@/api/hooks";
import { formatDate } from "@/utils/format";
import type {
  KnowledgeSearchHit,
  KnowledgeSearchMode,
  KnowledgeStatus,
  KnowledgeType,
} from "@/api/types";

const TYPES: KnowledgeType[] = ["Fact", "Decision", "Constraint", "Research", "Hypothesis", "Issue", "Observation"];
const STATUSES: KnowledgeStatus[] = ["Active", "Stale", "Superseded", "Archived"];
const MODES: KnowledgeSearchMode[] = ["hybrid", "semantic", "lexical"];
const TARGET_TYPES = ["task", "knowledge", "symbol", "file", "repository", "project"];
const RELATIONS = [
  "relates",
  "depends_on",
  "blocks",
  "derived_from",
  "supersedes",
  "contradicts",
  "implements",
  "source",
  "mentions",
];

function errText(e: any): string {
  return e?.response?.data?.detail || e?.response?.data?.title || e?.message || "Request failed";
}

function statusColor(status: KnowledgeStatus): string {
  if (status === "Active") return "var(--success)";
  if (status === "Stale") return "var(--warning)";
  if (status === "Superseded") return "var(--accent)";
  return "var(--text-muted)";
}

export default function KnowledgePage() {
  const projectsQ = useProjects();
  const reposQ = useRepositories();
  const actions = useKnowledgeActions();

  const [draftQuery, setDraftQuery] = useState("");
  const [appliedQuery, setAppliedQuery] = useState("");
  const [mode, setMode] = useState<KnowledgeSearchMode>("hybrid");
  const [projectId, setProjectId] = useState("");
  const [types, setTypes] = useState<KnowledgeType[]>([]);
  const [statuses, setStatuses] = useState<KnowledgeStatus[]>([]);
  const [globalOnly, setGlobalOnly] = useState(false);
  const [includeArchived, setIncludeArchived] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [newTitle, setNewTitle] = useState("");
  const [newContent, setNewContent] = useState("");
  const [newType, setNewType] = useState<KnowledgeType>("Fact");
  const [newProjectId, setNewProjectId] = useState("");
  const [newRepositoryId, setNewRepositoryId] = useState("");
  const [newSourceRef, setNewSourceRef] = useState("");
  const [newExternalKey, setNewExternalKey] = useState("");
  const [newCreatedBy, setNewCreatedBy] = useState("ui");

  const [staleReason, setStaleReason] = useState("");
  const [linkTargetType, setLinkTargetType] = useState("task");
  const [linkTargetId, setLinkTargetId] = useState("");
  const [linkRelation, setLinkRelation] = useState("relates");
  const [linkNote, setLinkNote] = useState("");

  const listParams = useMemo(
    () => ({
      types: types.length ? types : undefined,
      statuses: statuses.length ? statuses : undefined,
      projectId: projectId || undefined,
      globalOnly,
      includeArchived,
      limit: 100,
    }),
    [types, statuses, projectId, globalOnly, includeArchived],
  );

  const searchReq = useMemo(
    () => ({
      query: appliedQuery,
      mode,
      types: types.length ? types : undefined,
      statuses: statuses.length ? statuses : undefined,
      projectId: projectId || undefined,
      globalOnly,
      includeArchived,
      limit: 50,
    }),
    [appliedQuery, mode, types, statuses, projectId, globalOnly, includeArchived],
  );

  const listQ = useKnowledgeList(listParams, !appliedQuery);
  const searchQ = useKnowledgeSearch(searchReq, !!appliedQuery);
  const detailQ = useKnowledge(selectedId ?? undefined);
  const detail = detailQ.data;

  const projects = projectsQ.data ?? [];
  const repos = reposQ.data ?? [];
  const hits: KnowledgeSearchHit[] = appliedQuery
    ? searchQ.data?.hits ?? []
    : (listQ.data ?? []).map((entry) => ({ entry, score: 0, matchedBy: "list" }));
  const warnings = appliedQuery ? searchQ.data?.warnings ?? [] : [];
  const activeQ = appliedQuery ? searchQ : listQ;
  const projectName = (id?: string | null) =>
    id ? projects.find((p) => p.id === id)?.name ?? id.slice(0, 8) : "global";

  const resetMessages = () => {
    setError(null);
    setNotice(null);
  };

  const refreshResults = () => Promise.all([listQ.refetch(), searchQ.refetch(), detailQ.refetch()]);

  const reloadOnConflict = async (e: any, message: string) => {
    if (e?.response?.status === 409) {
      setNotice(message);
      await refreshResults();
      return true;
    }
    return false;
  };

  const toggleType = (t: KnowledgeType) =>
    setTypes((prev) => (prev.includes(t) ? prev.filter((x) => x !== t) : [...prev, t]));
  const toggleStatus = (s: KnowledgeStatus) =>
    setStatuses((prev) => (prev.includes(s) ? prev.filter((x) => x !== s) : [...prev, s]));

  const submitSearch = (e: React.FormEvent) => {
    e.preventDefault();
    resetMessages();
    setAppliedQuery(draftQuery.trim());
  };

  const submitCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    resetMessages();
    if (!newTitle.trim() || !newContent.trim()) {
      setError("Title and content are required.");
      return;
    }
    try {
      const result = await actions.create.mutateAsync({
        title: newTitle.trim(),
        content: newContent.trim(),
        type: newType,
        projectId: newProjectId || undefined,
        repositoryId: newRepositoryId || undefined,
        sourceRef: newSourceRef.trim() || undefined,
        externalKey: newExternalKey.trim() || undefined,
        createdBy: newCreatedBy.trim() || undefined,
      });
      setNewTitle("");
      setNewContent("");
      setNewSourceRef("");
      setNewExternalKey("");
      setSelectedId(result.entry.id);
      setNotice(result.created ? "Knowledge entry created." : "Existing entry returned by external key.");
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const markStale = async () => {
    if (!detail) return;
    resetMessages();
    try {
      await actions.markStale.mutateAsync({
        id: detail.entry.id,
        expectedVersion: detail.entry.version,
        reason: staleReason.trim() || undefined,
        actor: "ui",
      });
      setStaleReason("");
      setNotice("Entry marked stale.");
    } catch (e: any) {
      const handled = await reloadOnConflict(
        e,
        "Entry was changed elsewhere — the latest version was reloaded. Re-apply the stale mark.",
      );
      if (!handled) setError(errText(e));
    }
  };

  const addLink = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!detail || !linkTargetId.trim()) return;
    resetMessages();
    try {
      const result = await actions.addLink.mutateAsync({
        id: detail.entry.id,
        targetType: linkTargetType,
        targetId: linkTargetId.trim(),
        relation: linkRelation,
        note: linkNote.trim() || undefined,
        actor: "ui",
      });
      setLinkTargetId("");
      setLinkNote("");
      setNotice(result.created ? "Link added." : "Link already exists.");
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const removeLink = async (link: { fromType: string; fromId: string; toType: string; toId: string; relation: string }) => {
    if (!detail) return;
    resetMessages();
    const outgoing = link.fromType === "knowledge" && link.fromId === detail.entry.id;
    try {
      await actions.removeLink.mutateAsync({
        id: detail.entry.id,
        targetType: outgoing ? link.toType : link.fromType,
        targetId: outgoing ? link.toId : link.fromId,
        relation: link.relation,
        actor: "ui",
      });
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const reembed = async () => {
    resetMessages();
    try {
      const result = await actions.reembed.mutateAsync(100);
      setNotice(`Re-embedded ${result.embedded} knowledge entr${result.embedded === 1 ? "y" : "ies"}.`);
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const linkOtherSide = (link: { fromType: string; fromId: string; toType: string; toId: string }) => {
    const outgoing = link.fromType === "knowledge" && link.fromId === detail?.entry.id;
    return outgoing
      ? { type: link.toType, id: link.toId }
      : { type: link.fromType, id: link.fromId };
  };

  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 12, flexWrap: "wrap" }}>
        <div>
          <h1 style={{ marginBottom: 4 }}>Knowledge</h1>
          <div style={{ color: "var(--text-muted)", fontSize: 13 }}>
            Durable project memory with semantic, lexical and hybrid search.
          </div>
        </div>
        <button onClick={reembed} disabled={actions.reembed.isPending} data-testid="reembed">
          {actions.reembed.isPending ? "Re-embedding…" : "Re-embed pending"}
        </button>
      </div>

      {notice && <div className="banner" style={{ marginTop: 12 }}>{notice}</div>}
      {error && <div className="banner" style={{ marginTop: 12 }}>⚠ {error}</div>}
      {warnings.length > 0 && (
        <div className="banner" style={{ marginTop: 12 }}>
          ⚠ {warnings.join(" · ")}
        </div>
      )}

      <form onSubmit={submitSearch} className="card" style={{ marginTop: 16, display: "flex", flexDirection: "column", gap: 10 }}>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <input
            value={draftQuery}
            onChange={(e) => setDraftQuery(e.target.value)}
            placeholder="Search titles and content… (empty query lists entries)"
            style={{ flex: 3, minWidth: 220 }}
            data-testid="knowledge-query"
          />
          <select value={mode} onChange={(e) => setMode(e.target.value as KnowledgeSearchMode)} style={{ flex: 1, minWidth: 130 }}>
            {MODES.map((m) => <option key={m} value={m}>{m}</option>)}
          </select>
          <button type="submit" className="primary">Search</button>
        </div>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <select value={projectId} onChange={(e) => setProjectId(e.target.value)} style={{ flex: 1, minWidth: 160 }}>
            <option value="">All projects</option>
            {projects.map((p) => <option key={p.id} value={p.id}>{p.name ?? p.id.slice(0, 8)}</option>)}
          </select>
          <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, color: "var(--text-muted)", whiteSpace: "nowrap" }}>
            <input type="checkbox" checked={globalOnly} onChange={(e) => setGlobalOnly(e.target.checked)} style={{ width: "auto" }} />
            Global only
          </label>
          <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, color: "var(--text-muted)", whiteSpace: "nowrap" }}>
            <input type="checkbox" checked={includeArchived} onChange={(e) => setIncludeArchived(e.target.checked)} style={{ width: "auto" }} />
            Include archived
          </label>
        </div>
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap", alignItems: "center" }}>
          <span style={{ fontSize: 12, color: "var(--text-muted)" }}>Types</span>
          {TYPES.map((t) => (
            <button
              key={t}
              type="button"
              className={types.includes(t) ? "primary" : undefined}
              onClick={() => toggleType(t)}
              style={{ fontSize: 11, padding: "2px 8px" }}
            >
              {t}
            </button>
          ))}
        </div>
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap", alignItems: "center" }}>
          <span style={{ fontSize: 12, color: "var(--text-muted)" }}>Statuses</span>
          {STATUSES.map((s) => (
            <button
              key={s}
              type="button"
              className={statuses.includes(s) ? "primary" : undefined}
              onClick={() => toggleStatus(s)}
              style={{ fontSize: 11, padding: "2px 8px" }}
            >
              {s}
            </button>
          ))}
        </div>
      </form>

      <div className="grid grid-2" style={{ marginTop: 16, alignItems: "start" }}>
        <div className="card" style={{ padding: 0, overflow: "auto" }}>
          <div style={{ padding: "10px 14px", borderBottom: "1px solid var(--border)", fontSize: 13, color: "var(--text-muted)" }}>
            {activeQ.isFetching ? "Searching…" : `${hits.length} result${hits.length === 1 ? "" : "s"}`}
            {searchQ.data?.mode && appliedQuery ? ` · mode ${searchQ.data.mode}` : ""}
          </div>
          {activeQ.isLoading ? (
            <div className="skeleton" style={{ height: 160, margin: 12 }} />
          ) : activeQ.isError ? (
            <div style={{ padding: 16, color: "var(--danger)", fontSize: 13 }}>
              Failed: {(activeQ.error as Error).message}
              <button onClick={() => activeQ.refetch()} style={{ marginLeft: 8, fontSize: 12 }}>Retry</button>
            </div>
          ) : hits.length === 0 ? (
            <div style={{ padding: 24, textAlign: "center", color: "var(--text-muted)", fontSize: 14 }}>
              No knowledge entries found. Create one below or adjust the filters.
            </div>
          ) : (
            <table className="table">
              <thead>
                <tr><th>Title</th><th>Type</th><th>Status</th><th>Scope</th><th>Score</th><th>Updated</th></tr>
              </thead>
              <tbody>
                {hits.map((hit) => (
                  <tr
                    key={hit.entry.id}
                    onClick={() => setSelectedId(hit.entry.id)}
                    style={{ cursor: "pointer", background: selectedId === hit.entry.id ? "var(--bg-hover)" : undefined }}
                  >
                    <td style={{ maxWidth: 260 }}>
                      <div style={{ fontWeight: 500 }}>{hit.entry.title}</div>
                      <div style={{ fontFamily: "monospace", fontSize: 10, color: "var(--text-muted)" }}>
                        {hit.entry.id.slice(0, 8)} · v{hit.entry.version} · {hit.matchedBy}
                      </div>
                    </td>
                    <td><span className="badge">{hit.entry.type}</span></td>
                    <td>
                      <span className="badge" style={{ color: statusColor(hit.entry.status), borderColor: statusColor(hit.entry.status) }}>
                        {hit.entry.status}
                      </span>
                    </td>
                    <td style={{ fontSize: 12 }}>{projectName(hit.entry.projectId)}</td>
                    <td style={{ fontSize: 12, fontVariantNumeric: "tabular-nums" }}>{hit.score ? hit.score.toFixed(3) : "—"}</td>
                    <td style={{ fontSize: 12, whiteSpace: "nowrap" }}>{formatDate(hit.entry.updatedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          {selectedId && (
            <div className="card">
              {detailQ.isLoading ? (
                <div className="skeleton" style={{ height: 200 }} />
              ) : detailQ.isError || !detail ? (
                <div style={{ color: "var(--danger)", fontSize: 13 }}>Could not load knowledge entry.</div>
              ) : (
                <div>
                  <div style={{ display: "flex", justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
                    <div>
                      <h3 style={{ margin: 0 }}>{detail.entry.title}</h3>
                      <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 4 }}>
                        {detail.entry.id} · v{detail.entry.version} · {projectName(detail.entry.projectId)}
                        {detail.entry.repositoryId ? ` · repo ${detail.entry.repositoryId.slice(0, 8)}` : ""}
                      </div>
                    </div>
                    <div style={{ display: "flex", gap: 6, alignItems: "flex-start" }}>
                      <span className="badge">{detail.entry.type}</span>
                      <span className="badge" style={{ color: statusColor(detail.entry.status), borderColor: statusColor(detail.entry.status) }}>
                        {detail.entry.status}
                      </span>
                      <span className="badge" title={detail.embedded ? "embedded" : "not embedded"}>
                        {detail.embedded ? "embedded" : "no vector"}
                      </span>
                    </div>
                  </div>

                  <pre
                    style={{
                      whiteSpace: "pre-wrap",
                      wordBreak: "break-word",
                      fontFamily: "inherit",
                      fontSize: 13,
                      lineHeight: 1.55,
                      background: "var(--bg-hover)",
                      border: "1px solid var(--border)",
                      borderRadius: 6,
                      padding: 10,
                      marginTop: 10,
                      maxHeight: 260,
                      overflowY: "auto",
                    }}
                  >
                    {detail.entry.content}
                  </pre>

                  <div style={{ display: "grid", gridTemplateColumns: "110px 1fr", gap: "3px 10px", fontSize: 12, marginTop: 10 }}>
                    <span style={{ color: "var(--text-muted)" }}>Source</span>
                    <span>{detail.entry.source}{detail.entry.sourceRef ? ` · ${detail.entry.sourceRef}` : ""}</span>
                    <span style={{ color: "var(--text-muted)" }}>Created by</span>
                    <span>{detail.entry.createdBy ?? "—"}</span>
                    <span style={{ color: "var(--text-muted)" }}>External key</span>
                    <span style={{ fontFamily: "monospace", fontSize: 11 }}>{detail.entry.externalKey ?? "—"}</span>
                    <span style={{ color: "var(--text-muted)" }}>Content hash</span>
                    <span style={{ fontFamily: "monospace", fontSize: 10 }}>{detail.entry.contentHash.slice(0, 20)}…</span>
                    <span style={{ color: "var(--text-muted)" }}>Updated</span>
                    <span>{formatDate(detail.entry.updatedAt)}</span>
                  </div>

                  <h4 style={{ marginTop: 14, marginBottom: 6 }}>Links</h4>
                  {detail.links.length === 0 ? (
                    <div style={{ fontSize: 12, color: "var(--text-muted)" }}>No links.</div>
                  ) : (
                    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      {detail.links.map((link) => {
                        const other = linkOtherSide(link);
                        return (
                          <div key={link.id} style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 12 }}>
                            <span className="badge">{link.relation}</span>
                            <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis" }}>
                              {other.type} · <code style={{ fontSize: 10 }}>{other.id.slice(0, 8)}</code>
                              {link.note ? ` — ${link.note}` : ""}
                            </span>
                            <button
                              style={{ fontSize: 11, padding: "2px 6px" }}
                              onClick={() => removeLink(link)}
                              disabled={actions.removeLink.isPending}
                            >
                              Remove
                            </button>
                          </div>
                        );
                      })}
                    </div>
                  )}
                  <form onSubmit={addLink} style={{ display: "flex", gap: 6, marginTop: 8, flexWrap: "wrap" }}>
                    <select value={linkTargetType} onChange={(e) => setLinkTargetType(e.target.value)} style={{ width: 100 }}>
                      {TARGET_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                    </select>
                    <select value={linkRelation} onChange={(e) => setLinkRelation(e.target.value)} style={{ width: 110 }}>
                      {RELATIONS.map((r) => <option key={r} value={r}>{r}</option>)}
                    </select>
                    <input
                      value={linkTargetId}
                      onChange={(e) => setLinkTargetId(e.target.value)}
                      placeholder="target id (GUID)"
                      style={{ flex: 1, minWidth: 170 }}
                    />
                    <input
                      value={linkNote}
                      onChange={(e) => setLinkNote(e.target.value)}
                      placeholder="note"
                      style={{ flex: 1, minWidth: 100 }}
                    />
                    <button type="submit" disabled={!linkTargetId.trim() || actions.addLink.isPending} style={{ fontSize: 12 }}>Link</button>
                  </form>

                  <h4 style={{ marginTop: 14, marginBottom: 6 }}>Mark stale</h4>
                  <div style={{ display: "flex", gap: 6 }}>
                    <input value={staleReason} onChange={(e) => setStaleReason(e.target.value)} placeholder="Reason (optional)" style={{ flex: 1 }} />
                    <button onClick={markStale} disabled={actions.markStale.isPending} style={{ fontSize: 12 }}>
                      {actions.markStale.isPending ? "Marking…" : "Mark stale"}
                    </button>
                  </div>

                  <h4 style={{ marginTop: 14, marginBottom: 6 }}>History</h4>
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, maxHeight: 160, overflowY: "auto" }}>
                    {detail.history.length === 0 && <div style={{ fontSize: 12, color: "var(--text-muted)" }}>No history.</div>}
                    {detail.history.map((h) => (
                      <div key={h.id} style={{ fontSize: 12, display: "flex", gap: 8, flexWrap: "wrap" }}>
                        <span className="badge" style={{ fontSize: 10 }}>{h.action}</span>
                        <span>{h.actor ?? "system"}</span>
                        <span style={{ color: "var(--text-muted)" }}>
                          {h.versionFrom != null ? `v${h.versionFrom} → ` : ""}{h.versionTo != null ? `v${h.versionTo}` : ""}
                        </span>
                        <span style={{ color: "var(--text-muted)", marginLeft: "auto" }}>{formatDate(h.at)}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}

          <div className="card">
            <h3 style={{ marginTop: 0, marginBottom: 10 }}>New knowledge entry</h3>
            <form onSubmit={submitCreate} style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <input value={newTitle} onChange={(e) => setNewTitle(e.target.value)} placeholder="Title" required data-testid="knowledge-title" />
              <textarea value={newContent} onChange={(e) => setNewContent(e.target.value)} placeholder="Content" rows={4} required />
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                <select value={newType} onChange={(e) => setNewType(e.target.value as KnowledgeType)} style={{ flex: 1, minWidth: 120 }}>
                  {TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                </select>
                <select value={newProjectId} onChange={(e) => setNewProjectId(e.target.value)} style={{ flex: 1, minWidth: 140 }}>
                  <option value="">No project</option>
                  {projects.map((p) => <option key={p.id} value={p.id}>{p.name ?? p.id.slice(0, 8)}</option>)}
                </select>
                <select value={newRepositoryId} onChange={(e) => setNewRepositoryId(e.target.value)} style={{ flex: 1, minWidth: 140 }}>
                  <option value="">No repository</option>
                  {repos.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
                </select>
              </div>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                <input value={newSourceRef} onChange={(e) => setNewSourceRef(e.target.value)} placeholder="source ref" style={{ flex: 1, minWidth: 120 }} />
                <input value={newExternalKey} onChange={(e) => setNewExternalKey(e.target.value)} placeholder="external key" style={{ flex: 1, minWidth: 120 }} />
                <input value={newCreatedBy} onChange={(e) => setNewCreatedBy(e.target.value)} placeholder="created by" style={{ width: 110 }} />
              </div>
              <button type="submit" className="primary" disabled={actions.create.isPending}>
                {actions.create.isPending ? "Creating…" : "Create entry"}
              </button>
            </form>
          </div>
        </div>
      </div>
    </div>
  );
}
