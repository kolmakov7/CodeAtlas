import { useQuery, useMutation, useQueryClient, keepPreviousData } from "@tanstack/react-query";
import apiClient from "./client";
import type {
  GlobalStatistics,
  Repository,
  RepositoryStatistics,
  SearchRequest,
  SearchResult,
  SymbolInfo,
  RelationEntry,
  SourceSlice,
  IndexingJob,
  Diagnostics,
  Settings,
  ProxyConfig,
  CredentialInfo,
  ActivityEntry,
  McpUsageStats,
  Documentation,
  DocumentationDetail,
  GenerateDocumentationResponse,
  DocumentationTargetType,
  RepositoryLink,
  SymbolUsageInfo,
  Project,
  TaskItem,
  TaskDetail,
  TaskResult,
  TaskQueryParams,
  TaskTreeParams,
  TaskTreeResult,
  TaskDependencyResult,
  TaskComment,
  TaskHistoryResult,
  TodoImportResult,
  EntityLink,
  KnowledgeEntry,
  KnowledgeDetail,
  KnowledgeSearchRequest,
  KnowledgeSearchResult,
  KnowledgeQueryParams,
  KnowledgeLinkResult,
  ConsolidationJob,
  ConsolidationJobDetail,
  RevisionStatus,
  RevisionRunResult,
  ConsolidationAnalysisResult,
  ConsolidationApplyOutcome,
  RevisionJobQueryParams,
} from "./types";

// --- helpers ---
const unwrap = <T>(p: Promise<{ data: T }>) => p.then((r) => r.data);

// Global statistics
export function useGlobalStatistics() {
  return useQuery({
    queryKey: ["statistics", "global"],
    queryFn: () => unwrap<GlobalStatistics>(apiClient.get("/statistics")),
  });
}

// MCP tool-usage statistics
export function useMcpUsage(days = 30) {
  return useQuery({
    queryKey: ["statistics", "mcp", days],
    queryFn: () => unwrap<McpUsageStats>(apiClient.get("/statistics/mcp", { params: { days } })),
    refetchInterval: 30_000,
  });
}

export function useRepositories() {
  return useQuery({
    queryKey: ["repositories"],
    queryFn: () => unwrap<Repository[]>(apiClient.get("/repositories")),
  });
}

export function useRepository(id: string | undefined) {
  return useQuery({
    queryKey: ["repositories", id],
    queryFn: () => unwrap<Repository>(apiClient.get(`/repositories/${id}`)),
    enabled: !!id,
  });
}

export function useRepositoryStatistics(id: string | undefined) {
  return useQuery({
    queryKey: ["repositories", id, "statistics"],
    queryFn: () => unwrap<RepositoryStatistics>(apiClient.get(`/repositories/${id}/statistics`)),
    enabled: !!id,
  });
}

// Mutations: Sync / Re-index / Pause / Resume / Delete
export function useRepositoryActions() {
  const qc = useQueryClient();
  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["repositories"] });
    qc.invalidateQueries({ queryKey: ["statistics"] });
    qc.invalidateQueries({ queryKey: ["jobs"] });
  };
  const sync = useMutation({
    mutationFn: (id: string) => apiClient.post(`/repositories/${id}/sync`),
    onSuccess: invalidate,
  });
  const reindex = useMutation({
    mutationFn: (id: string) => apiClient.post(`/repositories/${id}/reindex`),
    onSuccess: invalidate,
  });
  const relations = useMutation({
    mutationFn: (id: string) => apiClient.post(`/repositories/${id}/relations`),
    onSuccess: invalidate,
  });
  const rebuildAll = useMutation({
    mutationFn: () => apiClient.post(`/repositories/rebuild-all`),
    onSuccess: invalidate,
  });
  const pause = useMutation({
    mutationFn: (id: string) => apiClient.post(`/repositories/${id}/pause`),
    onSuccess: invalidate,
    // optimistic
    onMutate: async (id) => {
      await qc.cancelQueries({ queryKey: ["repositories", id] });
      const prev = qc.getQueryData<Repository>(["repositories", id]);
      if (prev) qc.setQueryData(["repositories", id], { ...prev, status: "Paused" as const });
      return { prev };
    },
    onError: (_e, id, ctx) => {
      if (ctx?.prev) qc.setQueryData(["repositories", id], ctx.prev);
    },
  });
  const resume = useMutation({
    mutationFn: (id: string) => apiClient.post(`/repositories/${id}/resume`),
    onSuccess: invalidate,
    onMutate: async (id) => {
      await qc.cancelQueries({ queryKey: ["repositories", id] });
      const prev = qc.getQueryData<Repository>(["repositories", id]);
      if (prev) qc.setQueryData(["repositories", id], { ...prev, status: "Active" as const });
      return { prev };
    },
    onError: (_e, id, ctx) => {
      if (ctx?.prev) qc.setQueryData(["repositories", id], ctx.prev);
    },
  });
  const remove = useMutation({
    mutationFn: ({ id, deleteCheckout }: { id: string; deleteCheckout: boolean }) =>
      apiClient.delete(`/repositories/${id}`, { params: { deleteCheckout } }),
    onSuccess: invalidate,
  });
  return { sync, reindex, relations, rebuildAll, pause, resume, remove };
}

// Search
export function useSearch(req: SearchRequest, enabled = true) {
  return useQuery({
    queryKey: ["search", req],
    queryFn: () => unwrap<SearchResult>(apiClient.post("/search", req)),
    enabled: enabled && req.query.trim().length > 0,
    placeholderData: keepPreviousData,
  });
}

export function useFileContent(
  repositoryId: string | undefined,
  filePath: string | undefined,
  fromLine?: number,
  toLine?: number,
) {
  return useQuery({
    queryKey: ["file", repositoryId, filePath, fromLine, toLine],
    queryFn: () =>
      unwrap<SourceSlice>(
        apiClient.get(`/repositories/${repositoryId}/file`, {
          params: { path: filePath, fromLine, toLine },
        }),
      ),
    enabled: !!repositoryId && !!filePath,
  });
}

// Symbol explorer
export function useSymbolDefinition(symbolId: string | undefined) {
  return useQuery({
    queryKey: ["symbol", symbolId],
    queryFn: () => unwrap<SymbolInfo>(apiClient.get(`/symbols/${symbolId}`)),
    enabled: !!symbolId,
  });
}
export function useSymbolReferences(symbolId: string | undefined) {
  return useQuery({
    queryKey: ["symbol", symbolId, "references"],
    queryFn: () => unwrap<RelationEntry[]>(apiClient.post("/references", { symbolId })),
    enabled: !!symbolId,
  });
}
export function useSymbolRelations(symbolId: string | undefined, direction: "outgoing" | "incoming" | "both" = "both") {
  return useQuery({
    queryKey: ["symbol", symbolId, "relations", direction],
    queryFn: () => unwrap<RelationEntry[]>(apiClient.get(`/symbols/${symbolId}/relations`, { params: { direction } })),
    enabled: !!symbolId,
  });
}
export function useSymbolImplementations(symbolId: string | undefined) {
  return useQuery({
    queryKey: ["symbol", symbolId, "implementations"],
    queryFn: () => unwrap<SymbolInfo[]>(apiClient.post("/implementations", { symbolId })),
    enabled: !!symbolId,
  });
}
export function useSymbolCallers(symbolId: string | undefined) {
  return useQuery({
    queryKey: ["symbol", symbolId, "callers"],
    queryFn: () => unwrap<SymbolInfo[]>(apiClient.post("/callers", { symbolId })),
    enabled: !!symbolId,
  });
}
export function useSymbolCallees(symbolId: string | undefined) {
  return useQuery({
    queryKey: ["symbol", symbolId, "callees"],
    queryFn: () => unwrap<SymbolInfo[]>(apiClient.post("/callees", { symbolId })),
    enabled: !!symbolId,
  });
}
export function useFindSymbol(query: string, repositoryId?: string, language?: string) {
  return useQuery({
    queryKey: ["symbols", "find", query, repositoryId, language],
    queryFn: () =>
      unwrap<SymbolInfo[]>(
        apiClient.post("/symbols/find", { query, repositoryId, language }),
      ),
    enabled: query.trim().length > 1,
  });
}

// Jobs polling every 2s
export function useJobs(repositoryId?: string | null, status?: string | null) {
  return useQuery({
    queryKey: ["jobs", repositoryId ?? "all", status ?? "all"],
    queryFn: () =>
      unwrap<IndexingJob[]>(
        apiClient.get("/jobs", { params: { repositoryId: repositoryId || undefined, status: status || undefined } }),
      ),
    refetchInterval: 2000,
  });
}
export function useJobActions() {
  const qc = useQueryClient();
  const cancel = useMutation({
    mutationFn: (jobId: string) => apiClient.post(`/jobs/${jobId}/cancel`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["jobs"] }),
  });
  const retry = useMutation({
    mutationFn: (jobId: string) => apiClient.post(`/jobs/${jobId}/retry`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["jobs"] }),
  });
  return { cancel, retry };
}

// Diagnostics - /api/diagnostics with fallback to /api/health
export function useDiagnostics() {
  return useQuery({
    queryKey: ["diagnostics"],
    queryFn: async () => {
      try {
        return await unwrap<Diagnostics>(apiClient.get("/diagnostics"));
      } catch {
        // fallback to /health -> map to Diagnostics shape
        const h: any = await unwrap<any>(apiClient.get("/health"));
        const map = (s: string) => (s === "Healthy" ? "OK" : "FAIL");
        return {
          database: { status: map(h.database?.status ?? "FAIL"), detail: h.database?.message },
          vectorIndex: { status: map(h.vector?.status ?? "FAIL"), detail: h.vector?.message },
          embeddingApi: { status: map(h.embedding?.status ?? "FAIL"), detail: h.embedding?.message, latencyMs: h.embedding?.latencyMs },
          indexer: { status: map(h.indexer?.status ?? "FAIL"), detail: h.indexer?.message },
          storage: { status: map(h.storage?.status ?? "FAIL"), detail: h.storage?.message },
        } as Diagnostics;
      }
    },
    refetchInterval: 10_000,
  });
}

// Settings
export function useSettings() {
  return useQuery({
    queryKey: ["settings"],
    queryFn: () => unwrap<Settings>(apiClient.get("/settings")),
  });
}
export function useUpdateSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: Settings) => apiClient.put("/settings", payload).then((r) => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["settings"] }),
  });
}
export function useProxies() {
  return useQuery({
    queryKey: ["proxies"],
    queryFn: () => unwrap<ProxyConfig[]>(apiClient.get("/proxies")),
  });
}
export function useCredentials() {
  return useQuery({
    queryKey: ["credentials"],
    queryFn: () => unwrap<CredentialInfo[]>(apiClient.get("/credentials")),
  });
}
export function useRepositoryGroups() {
  return useQuery({
    queryKey: ["repositoryGroups"],
    queryFn: () => unwrap<any[]>(apiClient.get("/repository-groups")),
  });
}
export function useActivity(limit = 20) {
  return useQuery({
    queryKey: ["activity", limit],
    queryFn: () => unwrap<ActivityEntry[]>(apiClient.get("/activity", { params: { limit } })),
  });
}

// Documentation (AI-generated)
export function useDocumentationList(repositoryId: string | undefined, targetType?: DocumentationTargetType) {
  return useQuery({
    queryKey: ["documentation", repositoryId, targetType ?? "all"],
    queryFn: () =>
      unwrap<Documentation[]>(
        apiClient.get(`/documentation/repositories/${repositoryId}`, {
          params: targetType ? { targetType } : undefined,
        }),
      ),
    enabled: !!repositoryId,
  });
}

export function useDocumentationDetail(id: string | undefined) {
  return useQuery({
    queryKey: ["documentation", "detail", id],
    queryFn: () => unwrap<DocumentationDetail>(apiClient.get(`/documentation/${id}`)),
    enabled: !!id,
  });
}

export function useDocumentationByTarget(
  repositoryId: string | undefined,
  targetType: DocumentationTargetType | undefined,
  targetRef: string | undefined,
) {
  return useQuery({
    queryKey: ["documentation", "target", repositoryId, targetType, targetRef],
    queryFn: () =>
      unwrap<DocumentationDetail>(
        apiClient.get(`/documentation/repositories/${repositoryId}/targets/${targetType}`, {
          params: { targetRef },
        }),
      ),
    enabled: !!repositoryId && !!targetType && !!targetRef,
  });
}

export function useDocumentationActions() {
  const qc = useQueryClient();
  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["documentation"] });
    qc.invalidateQueries({ queryKey: ["jobs"] });
  };
  const generateRepository = useMutation({
    mutationFn: (payload: { repositoryId: string; force?: boolean; maxSymbols?: number; strategy?: "usage" | "kind"; minUsage?: number }) =>
      unwrap<{ jobId: string }>(
        apiClient.post(`/documentation/repositories/${payload.repositoryId}/generate`, {
          force: payload.force ?? false,
          maxSymbols: payload.maxSymbols ?? null,
          strategy: payload.strategy ?? "usage",
          minUsage: payload.minUsage ?? 0,
        }),
      ),
    onSuccess: invalidate,
  });
  const generateTarget = useMutation({
    mutationFn: (payload: {
      repositoryId: string;
      targetType: DocumentationTargetType;
      targetRef?: string;
      symbolId?: string;
      force?: boolean;
    }) =>
      unwrap<GenerateDocumentationResponse>(apiClient.post("/documentation/targets/generate", payload)),
    onSuccess: invalidate,
  });
  const remove = useMutation({
    mutationFn: (id: string) => apiClient.delete(`/documentation/${id}`),
    onSuccess: invalidate,
  });
  return { generateRepository, generateTarget, remove };
}

// Repository links (cross-repo relations)
export function useRepositoryLinks(repositoryId?: string) {
  return useQuery({
    queryKey: ["repository-links", repositoryId ?? "all"],
    queryFn: () =>
      unwrap<RepositoryLink[]>(
        apiClient.get("/repository-links", { params: { repositoryId: repositoryId || undefined } }),
      ),
  });
}

export function useRepositoryLinkActions() {
  const qc = useQueryClient();
  const invalidate = () => qc.invalidateQueries({ queryKey: ["repository-links"] });
  const create = useMutation({
    mutationFn: (payload: { sourceRepositoryId: string; targetRepositoryId: string; linkType: string; note?: string }) =>
      unwrap<RepositoryLink>(apiClient.post("/repository-links", payload)),
    onSuccess: invalidate,
  });
  const remove = useMutation({
    mutationFn: (id: string) => apiClient.delete(`/repository-links/${id}`),
    onSuccess: invalidate,
  });
  return { create, remove };
}

// Per-object usage statistics
export function useRepositoryUsage(repositoryId?: string, days = 30) {
  return useQuery({
    queryKey: ["usage", repositoryId ?? "all", days],
    queryFn: () =>
      unwrap<SymbolUsageInfo[]>(
        apiClient.get(`/repositories/${repositoryId}/usage`, { params: { days, limit: 20 } }),
      ),
    enabled: !!repositoryId,
  });
}

// --- Projects ---
export function useProjects() {
  return useQuery({
    queryKey: ["projects"],
    queryFn: () => unwrap<Project[]>(apiClient.get("/projects")),
  });
}

export function useCreateProject() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: { name?: string; repositoryIds: string[]; path?: string; language?: string }) =>
      unwrap<Project>(apiClient.post("/projects", payload)),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["projects"] }),
  });
}

// --- Tasks ---
export function useTasks(params: TaskQueryParams = {}) {
  return useQuery({
    queryKey: ["tasks", "list", params],
    queryFn: () => unwrap<TaskItem[]>(apiClient.get("/tasks", { params })),
    placeholderData: keepPreviousData,
  });
}

export function useTask(id: string | undefined) {
  return useQuery({
    queryKey: ["tasks", "detail", id],
    queryFn: () => unwrap<TaskDetail>(apiClient.get(`/tasks/${id}`)),
    enabled: !!id,
  });
}

export function useTaskTree(params: TaskTreeParams = {}, enabled = true) {
  return useQuery({
    queryKey: ["tasks", "tree", params],
    queryFn: () => unwrap<TaskTreeResult>(apiClient.get("/tasks/tree", { params })),
    enabled,
    placeholderData: keepPreviousData,
  });
}

export function useTaskDependencies(id: string | undefined) {
  return useQuery({
    queryKey: ["tasks", "dependencies", id],
    queryFn: () => unwrap<EntityLink[]>(apiClient.get(`/tasks/${id}/dependencies`)),
    enabled: !!id,
  });
}

export function useTaskComments(id: string | undefined) {
  return useQuery({
    queryKey: ["tasks", "comments", id],
    queryFn: () => unwrap<TaskComment[]>(apiClient.get(`/tasks/${id}/comments`)),
    enabled: !!id,
  });
}

export function useTaskHistory(id: string | undefined) {
  return useQuery({
    queryKey: ["tasks", "history", id],
    queryFn: () => unwrap<TaskHistoryResult>(apiClient.get(`/tasks/${id}/history`)),
    enabled: !!id,
  });
}

export function useTaskActions() {
  const qc = useQueryClient();
  const invalidate = () => qc.invalidateQueries({ queryKey: ["tasks"] });
  const create = useMutation({
    mutationFn: (payload: {
      title: string;
      description?: string;
      kind?: TaskItem["kind"];
      status?: TaskItem["status"];
      priority?: TaskItem["priority"];
      projectId?: string;
      repositoryId?: string;
      parentTaskId?: string;
      createdBy?: string;
    }) => unwrap<TaskResult>(apiClient.post("/tasks", payload)),
    onSuccess: invalidate,
  });
  const update = useMutation({
    mutationFn: ({ id, ...payload }: {
      id: string;
      expectedVersion: number;
      title?: string;
      description?: string;
      kind?: TaskItem["kind"];
      status?: TaskItem["status"];
      priority?: TaskItem["priority"];
      projectId?: string;
      repositoryId?: string;
      parentTaskId?: string;
      clearParent?: boolean;
      actor?: string;
    }) => unwrap<TaskResult>(apiClient.put(`/tasks/${id}`, payload)),
    onSuccess: invalidate,
  });
  const archive = useMutation({
    mutationFn: ({ id, expectedVersion, actor }: { id: string; expectedVersion: number; actor?: string }) =>
      unwrap<TaskItem>(apiClient.post(`/tasks/${id}/archive`, null, { params: { expectedVersion, actor } })),
    onSuccess: invalidate,
  });
  const restore = useMutation({
    mutationFn: ({ id, expectedVersion, actor }: { id: string; expectedVersion: number; actor?: string }) =>
      unwrap<TaskItem>(apiClient.post(`/tasks/${id}/restore`, null, { params: { expectedVersion, actor } })),
    onSuccess: invalidate,
  });
  const addComment = useMutation({
    mutationFn: ({ id, body, author }: { id: string; body: string; author?: string }) =>
      unwrap<{ task: TaskItem; comment: TaskComment }>(
        apiClient.post(`/tasks/${id}/comments`, { body, author }),
      ),
    onSuccess: invalidate,
  });
  const addDependency = useMutation({
    mutationFn: ({ id, dependsOnTaskId, actor }: { id: string; dependsOnTaskId: string; actor?: string }) =>
      unwrap<TaskDependencyResult>(
        apiClient.post(`/tasks/${id}/dependencies`, { dependsOnTaskId, actor }),
      ),
    onSuccess: invalidate,
  });
  const removeDependency = useMutation({
    mutationFn: ({ id, dependsOnTaskId, actor }: { id: string; dependsOnTaskId: string; actor?: string }) =>
      apiClient.delete(`/tasks/${id}/dependencies/${dependsOnTaskId}`, { params: { actor } }),
    onSuccess: invalidate,
  });
  return { create, update, archive, restore, addComment, addDependency, removeDependency };
}

export function useImportTasks() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: { repositoryId: string; path: string; projectId?: string; dryRun?: boolean }) =>
      unwrap<TodoImportResult>(apiClient.post("/tasks/import", payload)),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["tasks"] }),
  });
}

export function useExportTasks() {
  return useMutation({
    mutationFn: (params: { projectId?: string; repositoryId?: string; includeArchived?: boolean }) =>
      unwrap<string>(apiClient.get("/tasks/export", { params, responseType: "text" })),
  });
}

// --- Knowledge ---
export function useKnowledgeList(params: KnowledgeQueryParams = {}, enabled = true) {
  return useQuery({
    queryKey: ["knowledge", "list", params],
    queryFn: () =>
      unwrap<KnowledgeEntry[]>(
        apiClient.get("/knowledge", { params, paramsSerializer: { indexes: null } }),
      ),
    enabled,
    placeholderData: keepPreviousData,
  });
}

export function useKnowledgeSearch(req: KnowledgeSearchRequest, enabled = true) {
  return useQuery({
    queryKey: ["knowledge", "search", req],
    queryFn: () => unwrap<KnowledgeSearchResult>(apiClient.post("/knowledge/search", req)),
    enabled,
    placeholderData: keepPreviousData,
  });
}

export function useKnowledge(id: string | undefined) {
  return useQuery({
    queryKey: ["knowledge", "detail", id],
    queryFn: () => unwrap<KnowledgeDetail>(apiClient.get(`/knowledge/${id}`)),
    enabled: !!id,
  });
}

export function useKnowledgeRelated(id: string | undefined, depth = 1, relation?: string) {
  return useQuery({
    queryKey: ["knowledge", "related", id, depth, relation ?? "all"],
    queryFn: () =>
      unwrap<KnowledgeEntry[]>(
        apiClient.get(`/knowledge/${id}/related`, { params: { depth, relation } }),
      ),
    enabled: !!id,
  });
}

export function useKnowledgeActions() {
  const qc = useQueryClient();
  const invalidate = () => qc.invalidateQueries({ queryKey: ["knowledge"] });
  const create = useMutation({
    mutationFn: (payload: {
      title: string;
      content: string;
      type: KnowledgeEntry["type"];
      projectId?: string;
      repositoryId?: string;
      sourceRef?: string;
      externalKey?: string;
      taskId?: string;
      createdBy?: string;
    }) => unwrap<{ entry: KnowledgeEntry; created: boolean }>(apiClient.post("/knowledge", payload)),
    onSuccess: invalidate,
  });
  const update = useMutation({
    mutationFn: ({ id, ...payload }: {
      id: string;
      expectedVersion: number;
      title?: string;
      content?: string;
      type?: KnowledgeEntry["type"];
      status?: KnowledgeEntry["status"];
      actor?: string;
    }) => unwrap<KnowledgeEntry>(apiClient.put(`/knowledge/${id}`, payload)),
    onSuccess: invalidate,
  });
  const markStale = useMutation({
    mutationFn: ({ id, expectedVersion, reason, actor }: {
      id: string;
      expectedVersion: number;
      reason?: string;
      actor?: string;
    }) => unwrap<KnowledgeEntry>(apiClient.post(`/knowledge/${id}/stale`, { expectedVersion, reason, actor })),
    onSuccess: invalidate,
  });
  const addLink = useMutation({
    mutationFn: ({ id, targetType, targetId, relation, note, actor }: {
      id: string;
      targetType: string;
      targetId: string;
      relation: string;
      note?: string;
      actor?: string;
    }) =>
      unwrap<KnowledgeLinkResult>(
        apiClient.post(`/knowledge/${id}/links`, { targetType, targetId, relation, note }, { params: { actor } }),
      ),
    onSuccess: invalidate,
  });
  const removeLink = useMutation({
    mutationFn: ({ id, targetType, targetId, relation, actor }: {
      id: string;
      targetType: string;
      targetId: string;
      relation: string;
      actor?: string;
    }) =>
      apiClient.delete(`/knowledge/${id}/links`, { params: { targetType, targetId, relation, actor } }),
    onSuccess: invalidate,
  });
  const reembed = useMutation({
    mutationFn: (limit: number) =>
      unwrap<{ embedded: number }>(apiClient.post("/knowledge/reembed", null, { params: { limit } })),
    onSuccess: invalidate,
  });
  return { create, update, markStale, addLink, removeLink, reembed };
}

// --- Revision / consolidation ---
export function useRevisionStatus() {
  return useQuery({
    queryKey: ["revision", "status"],
    queryFn: () => unwrap<RevisionStatus>(apiClient.get("/revision/status")),
    refetchInterval: 10_000,
  });
}

export function useRevisionJobs(params: RevisionJobQueryParams = {}) {
  return useQuery({
    queryKey: ["revision", "jobs", params],
    queryFn: () => unwrap<ConsolidationJob[]>(apiClient.get("/revision/jobs", { params })),
    refetchInterval: 5_000,
    placeholderData: keepPreviousData,
  });
}

export function useRevisionJob(id: string | undefined) {
  return useQuery({
    queryKey: ["revision", "job", id],
    queryFn: () => unwrap<ConsolidationJobDetail>(apiClient.get(`/revision/jobs/${id}`)),
    enabled: !!id,
  });
}

export function useRevisionActions() {
  const qc = useQueryClient();
  const invalidate = () => qc.invalidateQueries({ queryKey: ["revision"] });
  const run = useMutation({
    mutationFn: (payload: { projectId?: string; repositoryId?: string; dryRun?: boolean }) =>
      unwrap<RevisionRunResult>(apiClient.post("/revision/run", payload)),
    onSuccess: invalidate,
  });
  const analyze = useMutation({
    mutationFn: ({ id, actor }: { id: string; actor?: string }) =>
      unwrap<ConsolidationAnalysisResult>(apiClient.post(`/revision/jobs/${id}/analyze`, { actor })),
    onSuccess: invalidate,
  });
  const accept = useMutation({
    mutationFn: ({ id, expectedProposalVersion, actor }: {
      id: string;
      expectedProposalVersion: number;
      actor?: string;
    }) =>
      unwrap<ConsolidationApplyOutcome>(
        apiClient.post(`/revision/jobs/${id}/accept`, { expectedProposalVersion, actor }),
      ),
    onSuccess: invalidate,
  });
  const reject = useMutation({
    mutationFn: ({ id, actor, reason }: { id: string; actor?: string; reason?: string }) =>
      unwrap<ConsolidationApplyOutcome>(apiClient.post(`/revision/jobs/${id}/reject`, { actor, reason })),
    onSuccess: invalidate,
  });
  const cancel = useMutation({
    mutationFn: ({ id, actor }: { id: string; actor?: string }) =>
      unwrap<ConsolidationApplyOutcome>(apiClient.post(`/revision/jobs/${id}/cancel`, { actor })),
    onSuccess: invalidate,
  });
  const postpone = useMutation({
    mutationFn: ({ id, actor }: { id: string; actor?: string }) =>
      unwrap<ConsolidationApplyOutcome>(apiClient.post(`/revision/jobs/${id}/postpone`, { actor })),
    onSuccess: invalidate,
  });
  const retry = useMutation({
    mutationFn: ({ id, actor }: { id: string; actor?: string }) =>
      unwrap<ConsolidationApplyOutcome>(apiClient.post(`/revision/jobs/${id}/retry`, { actor })),
    onSuccess: invalidate,
  });
  const undo = useMutation({
    mutationFn: ({ id, actor }: { id: string; actor?: string }) =>
      unwrap<ConsolidationApplyOutcome>(apiClient.post(`/revision/jobs/${id}/undo`, { actor })),
    onSuccess: () => {
      invalidate();
      qc.invalidateQueries({ queryKey: ["knowledge"] });
    },
  });
  return { run, analyze, accept, reject, cancel, postpone, retry, undo };
}

