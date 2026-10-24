import { useMemo, useState } from "react";
import {
  useProjects,
  useRepositories,
  useTasks,
  useTask,
  useTaskTree,
  useTaskDependencies,
  useTaskActions,
} from "@/api/hooks";
import { formatDate } from "@/utils/format";
import type { TaskItem, TaskKind, TaskPriority, TaskState, TaskTreeNode } from "@/api/types";

const STATUSES: TaskState[] = ["Open", "InProgress", "Blocked", "Done", "Cancelled", "Archived"];
const KINDS: TaskKind[] = ["Work", "Temporary", "Research", "Note"];
const PRIORITIES: TaskPriority[] = ["Critical", "High", "Normal", "Low", "Backlog"];

function errText(e: any): string {
  return e?.response?.data?.detail || e?.response?.data?.title || e?.message || "Request failed";
}

function statusColor(status: TaskState): string {
  if (status === "Done") return "var(--success)";
  if (status === "Cancelled") return "var(--text-muted)";
  if (status === "Blocked") return "var(--danger)";
  if (status === "InProgress") return "var(--accent)";
  if (status === "Archived") return "var(--warning)";
  return "var(--text-muted)";
}

function TreeBranch({
  nodes,
  selectedId,
  onSelect,
  depth = 0,
}: {
  nodes: TaskTreeNode[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  depth?: number;
}) {
  return (
    <div>
      {nodes.map((node) => (
        <div key={node.task.id}>
          <div
            onClick={() => onSelect(node.task.id)}
            style={{
              padding: "6px 8px",
              paddingLeft: 8 + depth * 18,
              cursor: "pointer",
              borderRadius: 6,
              background: selectedId === node.task.id ? "var(--bg-hover)" : undefined,
              borderBottom: depth === 0 ? "1px solid var(--border)" : undefined,
            }}
          >
            <span className="badge" style={{ color: statusColor(node.task.status), borderColor: statusColor(node.task.status) }}>
              {node.task.status}
            </span>{" "}
            <strong style={{ fontSize: 13 }}>{node.task.title}</strong>{" "}
            <span style={{ fontSize: 11, color: "var(--text-muted)" }}>{node.task.kind}</span>
          </div>
          {node.children.length > 0 && (
            <TreeBranch nodes={node.children} selectedId={selectedId} onSelect={onSelect} depth={depth + 1} />
          )}
        </div>
      ))}
    </div>
  );
}

export default function TasksPage() {
  const projectsQ = useProjects();
  const reposQ = useRepositories();

  const [projectId, setProjectId] = useState("");
  const [status, setStatus] = useState("");
  const [kind, setKind] = useState("");
  const [query, setQuery] = useState("");
  const [includeArchived, setIncludeArchived] = useState(false);
  const [treeView, setTreeView] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [newTitle, setNewTitle] = useState("");
  const [newDescription, setNewDescription] = useState("");
  const [newKind, setNewKind] = useState<TaskKind>("Work");
  const [newPriority, setNewPriority] = useState<TaskPriority>("Normal");
  const [newProjectId, setNewProjectId] = useState("");
  const [newRepositoryId, setNewRepositoryId] = useState("");

  const [commentBody, setCommentBody] = useState("");
  const [commentAuthor, setCommentAuthor] = useState("ui");
  const [dependsOnTaskId, setDependsOnTaskId] = useState("");

  const listParams = useMemo(
    () => ({
      query: query.trim() || undefined,
      status: (status || undefined) as TaskState | undefined,
      kind: (kind || undefined) as TaskKind | undefined,
      projectId: projectId || undefined,
      includeArchived,
      limit: 200,
    }),
    [query, status, kind, projectId, includeArchived],
  );

  const treeParams = useMemo(
    () => ({ projectId: projectId || undefined, includeArchived }),
    [projectId, includeArchived],
  );

  const listQ = useTasks(listParams);
  const treeQ = useTaskTree(treeParams, treeView);
  const detailQ = useTask(selectedId ?? undefined);
  const depsQ = useTaskDependencies(selectedId ?? undefined);
  const actions = useTaskActions();

  const projects = projectsQ.data ?? [];
  const repos = reposQ.data ?? [];
  const detail = detailQ.data;
  const deps = depsQ.data ?? [];

  const resetMessages = () => {
    setError(null);
    setNotice(null);
  };

  const reloadOnConflict = async (e: any, message: string) => {
    if (e?.response?.status === 409) {
      setNotice(message);
      await Promise.all([detailQ.refetch(), listQ.refetch(), treeQ.refetch()]);
      return true;
    }
    return false;
  };

  const submitCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    resetMessages();
    if (!newTitle.trim()) {
      setError("Title is required.");
      return;
    }
    try {
      const result = await actions.create.mutateAsync({
        title: newTitle.trim(),
        description: newDescription.trim() || undefined,
        kind: newKind,
        priority: newPriority,
        projectId: newProjectId || undefined,
        repositoryId: newRepositoryId || undefined,
        createdBy: "ui",
      });
      setNewTitle("");
      setNewDescription("");
      setSelectedId(result.task.id);
      setNotice(result.created ? "Task created." : "Existing task returned.");
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const changeStatus = async (next: TaskState) => {
    if (!detail) return;
    resetMessages();
    try {
      await actions.update.mutateAsync({
        id: detail.task.id,
        expectedVersion: detail.task.version,
        status: next,
        actor: "ui",
      });
      setNotice(`Status changed to ${next}.`);
    } catch (e: any) {
      const handled = await reloadOnConflict(
        e,
        "Task was changed elsewhere — the latest version was reloaded. Re-apply your status change.",
      );
      if (!handled) setError(errText(e));
    }
  };

  const archiveOrRestore = async () => {
    if (!detail) return;
    resetMessages();
    const archived = detail.task.status === "Archived";
    try {
      const payload = { id: detail.task.id, expectedVersion: detail.task.version, actor: "ui" };
      if (archived) await actions.restore.mutateAsync(payload);
      else await actions.archive.mutateAsync(payload);
      setNotice(archived ? "Task restored." : "Task archived.");
    } catch (e: any) {
      const handled = await reloadOnConflict(
        e,
        "Task was changed elsewhere — the latest version was reloaded. Try again.",
      );
      if (!handled) setError(errText(e));
    }
  };

  const submitComment = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!detail || !commentBody.trim()) return;
    resetMessages();
    try {
      await actions.addComment.mutateAsync({
        id: detail.task.id,
        body: commentBody.trim(),
        author: commentAuthor.trim() || undefined,
      });
      setCommentBody("");
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const addDependency = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!detail || !dependsOnTaskId) return;
    resetMessages();
    try {
      const result = await actions.addDependency.mutateAsync({
        id: detail.task.id,
        dependsOnTaskId,
        actor: "ui",
      });
      setDependsOnTaskId("");
      setNotice(result.created ? "Dependency added." : "Dependency already exists.");
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const removeDependency = async (dependsOn: string) => {
    if (!detail) return;
    resetMessages();
    try {
      await actions.removeDependency.mutateAsync({
        id: detail.task.id,
        dependsOnTaskId: dependsOn,
        actor: "ui",
      });
    } catch (e: any) {
      setError(errText(e));
    }
  };

  const projectName = (id?: string | null) =>
    id ? projects.find((p) => p.id === id)?.name ?? id.slice(0, 8) : "—";
  const taskTitle = (id: string) =>
    listQ.data?.find((t) => t.id === id)?.title ?? (detail?.task.id === id ? detail.task.title : id.slice(0, 8));

  const rows: TaskItem[] = listQ.data ?? [];
  const dependencyCandidates = rows.filter((t) => t.id !== selectedId);

  return (
    <div>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 12, flexWrap: "wrap" }}>
        <div>
          <h1 style={{ marginBottom: 4 }}>Tasks</h1>
          <div style={{ color: "var(--text-muted)", fontSize: 13 }}>
            Central task registry with dependencies, comments and versioned updates.
          </div>
        </div>
        <button onClick={() => setTreeView((v) => !v)} data-testid="toggle-tree">
          {treeView ? "List view" : "Tree view"}
        </button>
      </div>
      {treeView && (
        <div style={{ fontSize: 12, color: "var(--text-muted)", marginTop: 6 }}>
          Tree view uses the project and archived filters; status, kind and search apply to the list.
        </div>
      )}

      {notice && <div className="banner" style={{ marginTop: 12 }}>{notice}</div>}
      {error && <div className="banner" style={{ marginTop: 12 }}>⚠ {error}</div>}

      <div className="card" style={{ marginTop: 16, display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
        <label style={{ flex: 1, minWidth: 160 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Project</div>
          <select value={projectId} onChange={(e) => setProjectId(e.target.value)}>
            <option value="">All projects</option>
            {projects.map((p) => (
              <option key={p.id} value={p.id}>{p.name ?? p.id.slice(0, 8)}</option>
            ))}
          </select>
        </label>
        <label style={{ flex: 1, minWidth: 130 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Status</div>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option>
            {STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </label>
        <label style={{ flex: 1, minWidth: 130 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Kind</div>
          <select value={kind} onChange={(e) => setKind(e.target.value)}>
            <option value="">All</option>
            {KINDS.map((k) => <option key={k} value={k}>{k}</option>)}
          </select>
        </label>
        <label style={{ flex: 2, minWidth: 200 }}>
          <div style={{ fontSize: 12, color: "var(--text-muted)", marginBottom: 4 }}>Search</div>
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Title or description" />
        </label>
        <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, color: "var(--text-muted)", paddingBottom: 8 }}>
          <input
            type="checkbox"
            checked={includeArchived}
            onChange={(e) => setIncludeArchived(e.target.checked)}
            style={{ width: "auto" }}
          />
          Include archived
        </label>
      </div>

      <div className="grid grid-2" style={{ marginTop: 16, alignItems: "start" }}>
        <div className="card" style={{ padding: 0, overflow: "auto" }}>
          <div style={{ padding: "10px 14px", borderBottom: "1px solid var(--border)", fontSize: 13, color: "var(--text-muted)" }}>
            {treeView ? "Task tree" : `${rows.length} task${rows.length === 1 ? "" : "s"}`}
          </div>
          {treeView ? (
            treeQ.isLoading ? (
              <div className="skeleton" style={{ height: 160, margin: 12 }} />
            ) : treeQ.isError ? (
              <div style={{ padding: 16, color: "var(--danger)", fontSize: 13 }}>
                Failed: {(treeQ.error as Error).message}
              </div>
            ) : !treeQ.data?.roots.length ? (
              <div style={{ padding: 24, textAlign: "center", color: "var(--text-muted)", fontSize: 14 }}>No tasks.</div>
            ) : (
              <div style={{ maxHeight: 520, overflowY: "auto" }}>
                <TreeBranch nodes={treeQ.data.roots} selectedId={selectedId} onSelect={setSelectedId} />
              </div>
            )
          ) : listQ.isLoading ? (
            <div className="skeleton" style={{ height: 160, margin: 12 }} />
          ) : listQ.isError ? (
            <div style={{ padding: 16, color: "var(--danger)", fontSize: 13 }}>
              Failed: {(listQ.error as Error).message}
              <button onClick={() => listQ.refetch()} style={{ marginLeft: 8, fontSize: 12 }}>Retry</button>
            </div>
          ) : !rows.length ? (
            <div style={{ padding: 24, textAlign: "center", color: "var(--text-muted)", fontSize: 14 }}>
              No tasks matching the filters.
            </div>
          ) : (
            <table className="table">
              <thead>
                <tr><th>Title</th><th>Status</th><th>Kind</th><th>Priority</th><th>Project</th><th>Updated</th></tr>
              </thead>
              <tbody>
                {rows.map((t) => (
                  <tr
                    key={t.id}
                    onClick={() => setSelectedId(t.id)}
                    style={{ cursor: "pointer", background: selectedId === t.id ? "var(--bg-hover)" : undefined }}
                  >
                    <td style={{ maxWidth: 300 }}>
                      <div style={{ fontWeight: 500 }}>{t.title}</div>
                      <div style={{ fontFamily: "monospace", fontSize: 10, color: "var(--text-muted)" }}>
                        {t.id.slice(0, 8)} · v{t.version}
                      </div>
                    </td>
                    <td>
                      <span className="badge" style={{ color: statusColor(t.status), borderColor: statusColor(t.status) }}>
                        {t.status}
                      </span>
                    </td>
                    <td style={{ fontSize: 12 }}>{t.kind}</td>
                    <td style={{ fontSize: 12 }}>{t.priority}</td>
                    <td style={{ fontSize: 12 }}>{projectName(t.projectId)}</td>
                    <td style={{ fontSize: 12, whiteSpace: "nowrap" }}>{formatDate(t.updatedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div className="card">
            <h3 style={{ marginTop: 0, marginBottom: 10 }}>New task</h3>
            <form onSubmit={submitCreate} style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <input
                value={newTitle}
                onChange={(e) => setNewTitle(e.target.value)}
                placeholder="Title"
                required
                data-testid="task-title"
              />
              <textarea
                value={newDescription}
                onChange={(e) => setNewDescription(e.target.value)}
                placeholder="Description (optional)"
                rows={3}
              />
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                <select value={newKind} onChange={(e) => setNewKind(e.target.value as TaskKind)} style={{ flex: 1, minWidth: 110 }}>
                  {KINDS.map((k) => <option key={k} value={k}>{k}</option>)}
                </select>
                <select value={newPriority} onChange={(e) => setNewPriority(e.target.value as TaskPriority)} style={{ flex: 1, minWidth: 110 }}>
                  {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
                </select>
              </div>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                <select value={newProjectId} onChange={(e) => setNewProjectId(e.target.value)} style={{ flex: 1, minWidth: 140 }}>
                  <option value="">No project</option>
                  {projects.map((p) => <option key={p.id} value={p.id}>{p.name ?? p.id.slice(0, 8)}</option>)}
                </select>
                <select value={newRepositoryId} onChange={(e) => setNewRepositoryId(e.target.value)} style={{ flex: 1, minWidth: 140 }}>
                  <option value="">No repository</option>
                  {repos.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
                </select>
              </div>
              <button type="submit" className="primary" disabled={actions.create.isPending}>
                {actions.create.isPending ? "Creating…" : "Create task"}
              </button>
            </form>
          </div>

          {!selectedId ? (
            <div className="card" style={{ color: "var(--text-muted)", fontSize: 13 }}>
              Select a task to view its description, comments, history and dependencies.
            </div>
          ) : detailQ.isLoading ? (
            <div className="card"><div className="skeleton" style={{ height: 180 }} /></div>
          ) : detailQ.isError || !detail ? (
            <div className="card" style={{ color: "var(--danger)", fontSize: 13 }}>
              Could not load task. {(detailQ.error as Error)?.message}
            </div>
          ) : (
            <div className="card">
              <div style={{ display: "flex", justifyContent: "space-between", gap: 8, alignItems: "flex-start", flexWrap: "wrap" }}>
                <div>
                  <h3 style={{ margin: 0 }}>{detail.task.title}</h3>
                  <div style={{ fontSize: 11, color: "var(--text-muted)", marginTop: 4 }}>
                    {detail.task.id} · v{detail.task.version} · {detail.task.kind} · {detail.task.priority}
                  </div>
                </div>
                <button onClick={archiveOrRestore} disabled={actions.archive.isPending || actions.restore.isPending} style={{ fontSize: 12 }}>
                  {detail.task.status === "Archived" ? "Restore" : "Archive"}
                </button>
              </div>

              <div style={{ display: "flex", gap: 8, alignItems: "flex-end", marginTop: 10, flexWrap: "wrap" }}>
                <label style={{ flex: 1, minWidth: 150 }}>
                  <div style={{ fontSize: 11, color: "var(--text-muted)", marginBottom: 3 }}>Status</div>
                  <select
                    value={detail.task.status}
                    onChange={(e) => changeStatus(e.target.value as TaskState)}
                    disabled={actions.update.isPending}
                  >
                    {STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
                  </select>
                </label>
                <span style={{ fontSize: 11, color: "var(--text-muted)", paddingBottom: 8 }}>
                  optimistic version {detail.task.version}
                </span>
              </div>

              <div style={{ marginTop: 12, fontSize: 13, whiteSpace: "pre-wrap", lineHeight: 1.55 }}>
                {detail.task.description || <span style={{ color: "var(--text-muted)" }}>No description.</span>}
              </div>

              <h4 style={{ marginTop: 16, marginBottom: 6 }}>Dependencies</h4>
              {deps.length === 0 ? (
                <div style={{ fontSize: 12, color: "var(--text-muted)" }}>No dependencies.</div>
              ) : (
                <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                  {deps.map((link) => (
                    <div key={link.id} style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 12 }}>
                      <span className="badge">{link.relation}</span>
                      <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis" }}>{taskTitle(link.toId)}</span>
                      <button
                        style={{ fontSize: 11, padding: "2px 6px" }}
                        onClick={() => removeDependency(link.toId)}
                        disabled={actions.removeDependency.isPending}
                      >
                        Remove
                      </button>
                    </div>
                  ))}
                </div>
              )}
              <form onSubmit={addDependency} style={{ display: "flex", gap: 6, marginTop: 8 }}>
                <select value={dependsOnTaskId} onChange={(e) => setDependsOnTaskId(e.target.value)} style={{ flex: 1 }}>
                  <option value="">Add dependency…</option>
                  {dependencyCandidates.map((t) => <option key={t.id} value={t.id}>{t.title}</option>)}
                </select>
                <button type="submit" disabled={!dependsOnTaskId || actions.addDependency.isPending} style={{ fontSize: 12 }}>Add</button>
              </form>

              <h4 style={{ marginTop: 16, marginBottom: 6 }}>Comments ({detail.comments.length})</h4>
              <div style={{ display: "flex", flexDirection: "column", gap: 6, maxHeight: 180, overflowY: "auto" }}>
                {detail.comments.length === 0 && <div style={{ fontSize: 12, color: "var(--text-muted)" }}>No comments.</div>}
                {detail.comments.map((c) => (
                  <div key={c.id} style={{ borderLeft: "2px solid var(--border)", paddingLeft: 8, fontSize: 12 }}>
                    <div style={{ color: "var(--text-muted)" }}>{c.author} · {formatDate(c.createdAt)}</div>
                    <div style={{ whiteSpace: "pre-wrap" }}>{c.body}</div>
                  </div>
                ))}
              </div>
              <form onSubmit={submitComment} style={{ display: "flex", gap: 6, marginTop: 8 }}>
                <input
                  value={commentBody}
                  onChange={(e) => setCommentBody(e.target.value)}
                  placeholder="Add a comment…"
                  style={{ flex: 1 }}
                />
                <input
                  value={commentAuthor}
                  onChange={(e) => setCommentAuthor(e.target.value)}
                  placeholder="author"
                  style={{ width: 80 }}
                />
                <button type="submit" disabled={!commentBody.trim() || actions.addComment.isPending} style={{ fontSize: 12 }}>Post</button>
              </form>

              <h4 style={{ marginTop: 16, marginBottom: 6 }}>History</h4>
              <div style={{ display: "flex", flexDirection: "column", gap: 4, maxHeight: 180, overflowY: "auto" }}>
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
      </div>
    </div>
  );
}
