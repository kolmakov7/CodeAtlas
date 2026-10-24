// OpenAPI placeholder types — will be overwritten by `orval` generation.
// Keep in sync with Api contracts (§25) and Domain entities.

export type RepositoryStatus = "Active" | "Syncing" | "Indexing" | "Paused" | "Error" | "Deleting";
export type JobStatus = "Queued" | "Running" | "Completed" | "Failed" | "Canceled";
export type JobType = "Sync" | "Index" | "Reindex" | "Delete" | "Clone" | "Embedding" | "Rebuild" | "Relations" | "Documentation";
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
  groupId?: string | null;
  proxyId?: string | null;
  credentialId?: string | null;
  syncIntervalSeconds?: number | null;
  lastSyncAt?: string | null;
  lastIndexAt?: string | null;
  lastError?: string | null;
  directoryFilterEnabled?: boolean;
  allowedDirectories?: string[] | null;
}

export interface EmbeddingDomainStats {
  domain: string; // "code" | "text"
  indexed: number;
  pending: number;
}

export interface EmbeddingProfile {
  endpoint?: string;
  model?: string;
  dimensions?: number;
  batchSize?: number;
  timeoutSeconds?: number;
}

export interface RelationTypeStats {
  relationType: string;
  count: number;
}

export interface RepositoryStatistics {
  repositoryId: string;
  name: string;
  fileCount: number;
  symbolCount: number;
  chunkCount: number;
  vectorCount: number;
  relationCount?: number;
  relationsByType?: RelationTypeStats[];
  embeddingStats: EmbeddingDomainStats[];
  embeddingsFailed: number;
  indexedCommit?: string | null;
  currentCommit?: string | null;
  status: string;
  languageBreakdown?: Record<string, number>;
}

export interface GlobalStatistics {
  repositories: number;
  files: number;
  symbols: number;
  chunks: number;
  relations?: number;
  relationCount?: number;
  relationsByType?: RelationTypeStats[];
  embeddingStats: EmbeddingDomainStats[];
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

export interface McpToolUsage {
  tool: string;
  total: number;
  errors: number;
}

export interface McpDailyUsage {
  day: string;
  total: number;
}

export interface McpUsageStats {
  totalCalls: number;
  totalErrors: number;
  days: number;
  byTool: McpToolUsage[];
  byDay: McpDailyUsage[];
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
  fromCommit?: string | null;
  toCommit?: string | null;
  totalItems?: number | null; // files (Index/Rebuild) или chunks (Embedding)
  itemsDone?: number | null;
  phase?: string | null; // clearing/diff/clone/fetch/indexing/relations/embedding/commit
  currentFile?: string | null;
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
  vectorDomains?: Array<{
    domain: string;
    status: HealthStatus;
    latencyMs?: number;
    detail?: string;
    stale?: boolean;
    reason?: string | null;
    stored?: VectorStoredModel[];
    currentModel?: string | null;
    currentDim?: number | null;
    currentContextLength?: number | null;
  }>;
  embeddingApi: { status: HealthStatus; latencyMs?: number; model?: string; detail?: string };
  embeddingDomains?: Array<{
    domain: string;
    status: HealthStatus;
    latencyMs?: number;
    detail?: string;
    model?: string | null;
    dimensions?: number | null;
  }>;
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
    parallelFileIndexers?: number;
    enableFileWatcher?: boolean;
    fileWatcherDebounceMs?: number;
    maxChunkChars?: number;
    chunkOverlapFraction?: number;
    minEmbeddingChars?: number;
    exclusions?: string[]; // globs
    languageExclusions?: string[];
  };
  embeddings?: {
    code?: EmbeddingProfile;
    text?: EmbeddingProfile;
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
  revision?: {
    enabled?: boolean;
    intervalDays?: number;
    checkMinutes?: number;
    maxEntriesPerRun?: number;
    similarityThreshold?: number;
    staleDays?: number;
    toolsEnabled?: boolean;
  };
  llm?: {
    endpoint?: string | null;
    model?: string | null;
    temperature?: number | null;
    maxTokens?: number | null;
    timeoutSeconds?: number | null;
    maxRetries?: number | null;
    maxContextChars?: number | null;
    configured?: boolean;
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

export type DocumentationTargetType = "repository" | "module" | "symbol";
export type DocumentationStatus = "notgenerated" | "generated" | "stale" | "generating" | "failed";

export interface Documentation {
  id: string;
  repositoryId: string;
  targetType: DocumentationTargetType;
  targetId?: string | null;
  targetRef: string;
  name: string;
  namespace?: string | null;
  filePath?: string | null;
  language?: string | null;
  status: DocumentationStatus;
  error?: string | null;
  model?: string | null;
  promptVersion: string;
  sourceHash?: string | null;
  generationCommit?: string | null;
  contextChars?: number | null;
  outputChars?: number | null;
  generatedAt: string;
  updatedAt: string;
}

export interface DocumentationDetail extends Documentation {
  content?: string | null;
}

export interface GenerateDocumentationResponse {
  documentation: DocumentationDetail;
  generated: boolean;
}

export interface RepositoryLink {
  id: string;
  sourceRepositoryId: string;
  sourceRepositoryName: string;
  targetRepositoryId: string;
  targetRepositoryName: string;
  linkType: string;
  confidence?: number | null;
  note?: string | null;
  createdAt: string;
}

export interface SymbolUsageInfo {
  symbolId: string;
  repositoryId: string;
  name: string;
  fqn?: string | null;
  kind: string;
  filePath?: string | null;
  usageCount: number;
  lastUsedAt: string;
  score: number;
}

export interface Project {
  id: string;
  repositoryId: string;
  name?: string | null;
  path?: string | null;
  language?: string | null;
  repositoryIds?: string[];
}

export type TaskKind = "Work" | "Temporary" | "Research" | "Note";
export type TaskState = "Open" | "InProgress" | "Blocked" | "Done" | "Cancelled" | "Archived";
export type TaskPriority = "Critical" | "High" | "Normal" | "Low" | "Backlog";

export interface TaskItem {
  id: string;
  title: string;
  description?: string | null;
  kind: TaskKind;
  status: TaskState;
  previousStatus?: TaskState | null;
  priority: TaskPriority;
  projectId?: string | null;
  repositoryId?: string | null;
  parentTaskId?: string | null;
  source: string;
  externalKey?: string | null;
  createdBy?: string | null;
  metadata?: string | null;
  createdAt: string;
  updatedAt: string;
  archivedAt?: string | null;
  version: number;
}

export interface TaskComment {
  id: string;
  taskId: string;
  author: string;
  body: string;
  createdAt: string;
}

export interface EntityLink {
  id: string;
  fromType: string;
  fromId: string;
  toType: string;
  toId: string;
  relation: string;
  note?: string | null;
  createdBy?: string | null;
  createdAt: string;
}

export interface EntityHistoryEntry {
  id: number;
  entityType: string;
  entityId: string;
  at: string;
  actor?: string | null;
  action: string;
  versionFrom?: number | null;
  versionTo?: number | null;
  changes?: string | null;
}

export interface TaskDetail {
  task: TaskItem;
  comments: TaskComment[];
  links: EntityLink[];
  history: EntityHistoryEntry[];
}

export interface TaskResult {
  task: TaskItem;
  created: boolean;
  links: EntityLink[];
}

export interface TaskTreeNode {
  task: TaskItem;
  children: TaskTreeNode[];
}

export interface TaskTreeResult {
  roots: TaskTreeNode[];
}

export interface TaskDependencyResult {
  task: TaskItem;
  dependsOn: TaskItem;
  created: boolean;
}

export interface TaskHistoryResult {
  task: TaskItem;
  history: EntityHistoryEntry[];
}

export interface TaskQueryParams {
  query?: string;
  status?: TaskState;
  kind?: TaskKind;
  projectId?: string;
  repositoryId?: string;
  parentTaskId?: string;
  includeArchived?: boolean;
  rootOnly?: boolean;
  limit?: number;
  offset?: number;
}

export interface TaskTreeParams {
  rootTaskId?: string;
  projectId?: string;
  repositoryId?: string;
  maxDepth?: number;
  includeArchived?: boolean;
}

export interface TodoImportItem {
  externalKey: string;
  title: string;
  status: TaskState;
  action: string;
}

export interface TodoImportResult {
  dryRun: boolean;
  found: number;
  created: number;
  updated: number;
  skipped: number;
  items: TodoImportItem[];
}

export type KnowledgeType = "Fact" | "Decision" | "Constraint" | "Research" | "Hypothesis" | "Issue" | "Observation";
export type KnowledgeStatus = "Active" | "Stale" | "Superseded" | "Archived";

export interface KnowledgeEntry {
  id: string;
  title: string;
  content: string;
  type: KnowledgeType;
  projectId?: string | null;
  repositoryId?: string | null;
  status: KnowledgeStatus;
  source: string;
  sourceRef?: string | null;
  externalKey?: string | null;
  contentHash: string;
  createdBy?: string | null;
  metadata?: string | null;
  createdAt: string;
  updatedAt: string;
  validFrom?: string | null;
  validUntil?: string | null;
  version: number;
  embeddedModel?: string | null;
  embeddedConfigHash?: string | null;
  embeddedAt?: string | null;
}

export interface KnowledgeDetail {
  entry: KnowledgeEntry;
  links: EntityLink[];
  history: EntityHistoryEntry[];
  embedded: boolean;
}

export type KnowledgeSearchMode = "hybrid" | "semantic" | "lexical";

export interface KnowledgeSearchRequest {
  query: string;
  mode?: KnowledgeSearchMode;
  types?: KnowledgeType[];
  statuses?: KnowledgeStatus[];
  projectId?: string;
  repositoryId?: string;
  globalOnly?: boolean;
  includeArchived?: boolean;
  limit?: number;
  offset?: number;
}

export interface KnowledgeSearchHit {
  entry: KnowledgeEntry;
  score: number;
  matchedBy: string;
}

export interface KnowledgeSearchResult {
  hits: KnowledgeSearchHit[];
  warnings: string[];
  mode: string;
}

export interface KnowledgeQueryParams {
  query?: string;
  types?: KnowledgeType[];
  statuses?: KnowledgeStatus[];
  projectId?: string;
  repositoryId?: string;
  globalOnly?: boolean;
  includeArchived?: boolean;
  limit?: number;
  offset?: number;
}

export interface KnowledgeLinkResult {
  link: EntityLink;
  created: boolean;
}

export type ConsolidationJobStatus =
  | "Pending"
  | "ReadyForAnalysis"
  | "AwaitingLlm"
  | "ReadyForReview"
  | "Accepted"
  | "Rejected"
  | "Cancelled"
  | "Error"
  | "Postponed"
  | "Undone";

export type ConsolidationJobType = "Duplicates" | "Contradiction" | "MissingSource" | "Stale" | "Consolidate";

export interface ConsolidationJob {
  id: string;
  type: ConsolidationJobType;
  status: ConsolidationJobStatus;
  reason: string;
  projectId?: string | null;
  repositoryId?: string | null;
  dedupeKey: string;
  inputVersion: string;
  attempts: number;
  lastError?: string | null;
  analysis?: string | null;
  proposal?: string | null;
  proposalVersion: number;
  createdAt: string;
  updatedAt: string;
  resolvedAt?: string | null;
  resolvedBy?: string | null;
  resultKnowledgeId?: string | null;
  version: number;
}

export interface ConsolidationEntity {
  id: string;
  version: number;
  title: string;
  content: string;
  type: KnowledgeType;
  status: KnowledgeStatus;
  contentHash: string;
}

export interface ConsolidationJobDetail {
  job: ConsolidationJob;
  items: ConsolidationEntity[];
  history: EntityHistoryEntry[];
}

export interface RevisionRun {
  id: string;
  trigger: string;
  status: string;
  startedAt: string;
  finishedAt?: string | null;
  stats?: string | null;
  error?: string | null;
}

export interface RevisionStatus {
  llm: { configured: boolean; status: string };
  lastRun?: RevisionRun | null;
  queue: {
    readyForAnalysis: number;
    awaitingLlm: number;
    readyForReview: number;
    errors: number;
  };
}

export interface RevisionRunResult {
  runId: string;
  scanned: number;
  jobsCreated: number;
  jobsSkipped: number;
  jobIds: string[];
  warnings: string[];
}

export interface ConsolidationAnalysisResult {
  jobId: string;
  status: string;
  llmConfigured: boolean;
  error?: string | null;
  proposal?: string | null;
}

export interface ConsolidationApplyOutcome {
  success: boolean;
  jobId: string;
  status: string;
  resultKnowledgeId?: string | null;
  error?: string | null;
}

export interface RevisionJobQueryParams {
  status?: ConsolidationJobStatus;
  type?: ConsolidationJobType;
  projectId?: string;
  repositoryId?: string;
  limit?: number;
  offset?: number;
}
