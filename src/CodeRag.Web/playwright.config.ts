import { defineConfig, devices } from "@playwright/test";

const API_URL = process.env.E2E_API_URL ?? "http://localhost:8091";
const WEB_URL = process.env.E2E_WEB_URL ?? "http://localhost:5173";

/**
 * Playwright config for CodeAtlas UI tests.
 *
 * Starts two servers:
 *  - the .NET API with CODERAG__E2E_SEED=1 and a throwaway data dir (deterministic fixture);
 *  - the Vite dev server (proxies /api → the API).
 * Tests point at the Vite server (WEB_URL) so no wwwroot copy is required.
 *
 * Requires the API to be built once: `dotnet build src/CodeRag.Api` (see npm run e2e:prepare).
 */
export default defineConfig({
  testDir: "./tests/e2e",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : [["list"]],
  use: {
    baseURL: WEB_URL,
    trace: "on-first-retry",
    headless: true,
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: [
    {
      command: "npm run e2e:api",
      url: `${API_URL}/health/ready`,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      stdout: "pipe",
      stderr: "pipe",
    },
    {
      command: `VITE_API_TARGET=${API_URL} npm run dev -- --port 5173 --strictPort`,
      url: WEB_URL,
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
    },
  ],
});
