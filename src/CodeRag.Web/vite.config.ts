import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "path";

// API the dev server proxies to. Overridable for e2e (port 8080 may be taken by other services).
const apiTarget = process.env.VITE_API_TARGET || "http://localhost:8080";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: apiTarget,
        changeOrigin: true,
      },
      "/health": {
        target: apiTarget,
        changeOrigin: true,
      },
      "/openapi": {
        target: apiTarget,
        changeOrigin: true,
      },
      "/mcp": {
        target: apiTarget,
        changeOrigin: true,
      },
    },
  },
  preview: {
    port: 4173,
  },
  build: {
    outDir: "dist",
    sourcemap: true,
  },
});
