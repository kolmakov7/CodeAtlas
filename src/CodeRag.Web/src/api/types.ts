// OpenAPI placeholder types — will be overwritten by `orval` generation.
// Keep in sync with Api contracts (§25) and Domain entities.

export type RepositoryStatus = "Active" | "Syncing" | "Indexing" | "Paused" | "Error" | "Deleting";
export type JobStatus = "Queued" | "Running" | "Completed" | "Failed" | "Canceled";
export type JobType = "Sync" | "Index" | "Reindex" | "Delete";
export type SearchMode = "Hybrid" | "FullText" | "Semantic" | "Symbol";
export type SymbolKind = string; // e.g. Class, Interface, Method, Function, Property, Field, Enum, Namespace
export type HealthStatus = "OK" | "FAIL" | "Degraded";

export interface Repository {
  id: string;
  name: string;
  gitUrl: string;
  branch: string;
  localPath: string;
  currentCommit?: string | null;
  indexedCommit?: string | null;
  status: RepositoryStatus;
  enabled: boolean;
  proxyId?: string | null;
  credentialId?: string | null;
  syncIntervalSeconds?: number | null;
  lastSyncAt?: string | null;
  lastIndexAt?: string | null;
  lastError?: string | null;
}

export interface RepositoryStatistics {
  repositoryId: string;
  name: string;
  fileCount: number;
  symbolCount: number;
  chunkCount: number;
  vectorCount: number;
  indexedCommit?: string | null;
  currentCommit?: string | null;
  status: string;
  embeddingsIndexed: number;
  embeddingsPending: number;
  embeddingsFailed: number;
  languageBreakdown: Record<string, number>;
}

export interface GlobalStatistics {
  repositories: number;
  files: number;
  symbols: number;
  chunks: number;
  relations: number;
  embeddingsIndexed: number;
  embeddingsPending: number;
  embeddingsFailed: number;
  indexingRunning: number;
  indexingQueued: number;
  indexingFailed: number;
  storage: {
    sqliteSizeBytes: number;
    vectorSizeBytes: number;
    repositoriesSizeBytes: number;
  };
  recentActivity: ActivityEntry[];
  recentErrors: IndexingError[];
  languageBreakdown?: Record<string, number>;
}

export interface ActivityEntry {
  id: string;
  at: string;
  actor: string;
  action: string;
  repositoryId?: string | null;
  repositoryName?: string | null;
  detail?: string | null;
}

export interface IndexingError {
  id: string;
  repositoryId?: string | null;
  filePath?: string | null;
  message: string;
  at: string;
}

export interface SearchRequest {
  query: string;
  mode?: SearchMode;
  repositoryIds?: string[];
  languages?: string[];
  symbolKind?: string | null;
  limit?: number;
  offset?: number;
}

export interface SearchHit {
  repositoryId: string;
  repositoryName: string;
  filePath: string;
  language?: string | null;
  symbolId?: string | null;
  symbolName?: string | null;
  symbolKind?: string | null;
  fqn?: string | null;
  startLine?: number | null;
  endLine?: number | null;
  score: number;
  preview: string;
  highlightRanges?: Array<[number, number]>;
}

export interface SearchResult {
  items: SearchHit[];
  total: number;
  warnings?: string[];
}

export interface SymbolInfo {
  id: string;
  repositoryId: string;
  fileId: string;
  filePath: string;
  language: string;
  kind: string;
  name: string;
  fqn?: string | null;
  namespace?: string | null;
  parentSymbolId?: string | null;
  startLine: number;
  endLine: number;
  startColumn?: number;
  endColumn?: number;
  signature?: string | null;
}

export interface RelationEntry {
  fromSymbolId: string;
  toSymbolId: string;
  relationType: string;
  fromSymbol?: SymbolInfo | null;
  toSymbol?: SymbolInfo | null;
}

export interface SourceSlice {
  repositoryId: string;
  filePath: string;
  language?: string | null;
  content: string;
  startLine: number;
  endLine: number;
  totalLines: number;
}

export interface IndexingJob {
  id: string;
  repositoryId: string;
  repositoryName?: string | null;
  type: JobType;
  status: JobStatus;
  startedAt?: string | null;
  finishedAt?: string | null;
  progress?: number | null; // 0..1
  error?: string | null;
}

export interface VectorStoredModel {
  model: string | null;
  dim: number;
  contextLength: number | null;
  chunkCount: number;
}

export interface Diagnostics {
  database: { status: HealthStatus; detail?: string };
  vectorIndex: {
    status: HealthStatus;
    count?: number;
    sizeBytes?: number;
    detail?: string;
    stale?: boolean;
    reason?: string | null;
    stored?: VectorStoredModel[];
    currentModel?: string | null;
    currentDim?: number | null;
    currentContextLength?: number | null;
  };
  embeddingApi: { status: HealthStatus; latencyMs?: number; model?: string; detail?: string };
  indexer: { status: HealthStatus; queueDepth?: number; detail?: string };
  storage: { status: HealthStatus; freeBytes?: number; detail?: string };
}

export interface Settings {
  repositories?: {
    defaultBranch?: string;
    defaultSyncIntervalSeconds?: number | null;
    defaultProxyId?: string | null;
    defaultEnabled?: boolean;
  };
  indexing?: {
    syncIntervalSeconds?: number | null;
    maxConcurrentIndexers?: number;
    exclusions?: string[]; // globs
    languageExclusions?: string[];
  };
  embeddings?: {
    endpoint?: string;
    model?: string;
    dimensions?: number;
    batchSize?: number;
    timeoutSeconds?: number;
  };
  search?: {
    lexicalWeight?: number;
    semanticWeight?: number;
    structuralWeight?: number;
    defaultResultCount?: number;
    minSemanticScore?: number;
  };
  storage?: {
    dataDir?: string;
    vectorConfig?: string;
  };
  system?: {
    logLevel?: string;
  };
}

export interface ProxyConfig {
  id: string;
  name: string;
  scheme: "http" | "https" | "socks5" | "socks5h";
  host: string;
  port: number;
  username?: string | null;
}

export interface CredentialInfo {
  id: string;
  kind: string;
  username?: string | null;
  // secret never exposed
}
