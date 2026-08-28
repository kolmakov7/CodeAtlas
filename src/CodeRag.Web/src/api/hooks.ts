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
  return { sync, reindex, pause, resume, remove };
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

// Diagnostics
export function useDiagnostics() {
  return useQuery({
    queryKey: ["diagnostics"],
    queryFn: () => unwrap<Diagnostics>(apiClient.get("/diagnostics")),
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
export function useActivity(limit = 20) {
  return useQuery({
    queryKey: ["activity", limit],
    queryFn: () => unwrap<ActivityEntry[]>(apiClient.get("/activity", { params: { limit } })),
  });
}
