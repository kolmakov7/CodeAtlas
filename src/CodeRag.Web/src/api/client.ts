import axios, { AxiosRequestConfig } from "axios";

// Unified Axios instance — baseURL "/api" proxied by vite to http://localhost:8080
export const apiClient = axios.create({
  baseURL: "/api",
  headers: { "Content-Type": "application/json" },
  timeout: 30_000,
});

// Orval mutator: generic wrapper used by generated hooks
export const apiMutator = <T>(config: AxiosRequestConfig): Promise<T> =>
  apiClient(config).then((res) => res.data);

// Named export expected by orval.config.ts override.mutator
export const apiClientMutator = apiMutator;

export default apiClient;
