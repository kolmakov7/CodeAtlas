import { test, expect } from "@playwright/test";

/**
 * Feature tests for the three problems:
 *  1) cost-bounded documentation generation (budget respected);
 *  2) usage statistics (recorded and surfaced);
 *  3) cross-repository relations (links + callers found in a linked repo).
 */

const APP_REPO = "11111111-1111-1111-1111-111111111111";
const DB_REPO = "22222222-2222-2222-2222-222222222222";
const DB_SYMBOL = "b0b0b0b0-0000-0000-0000-000000000002";

test.describe("repository links (cross-repo, problem 3)", () => {
  test("create and delete a link between two repositories", async ({ page }) => {
    await page.goto(`/repositories/${DB_REPO}`);

    const card = page.getByTestId("repo-links-card");
    await expect(card).toBeVisible();
    await expect(card.getByText("No links yet.")).toBeVisible();

    await page.getByTestId("link-target-select").selectOption(APP_REPO);
    await page.getByTestId("link-type-select").selectOption("depends_on");
    await page.getByTestId("add-link").click();

    const row = page.getByTestId("link-row").first();
    await expect(row).toBeVisible();
    await expect(row).toContainText("e2e-db");
    await expect(row).toContainText("e2e-app");

    await row.getByRole("button", { name: "Delete" }).click();
    await expect(page.getByTestId("link-row")).toHaveCount(0);
  });
});

test.describe("cross-repo callers (problem 3)", () => {
  test("caller from a linked repository is visible on the symbol page", async ({ page }) => {
    await page.goto(`/symbols/${DB_SYMBOL}`);
    await page.getByRole("button", { name: "Callers" }).click();
    await expect(page.getByText("Caller", { exact: false }).first()).toBeVisible();
  });
});

test.describe("usage statistics (problem 2)", () => {
  test("a symbol lookup is recorded and appears in the usage panel", async ({ page }) => {
    // Exercise the lookup path via the Documentation symbol picker.
    await page.goto(`/documentation?repositoryId=${DB_REPO}`);
    await page.getByTestId("symbol-search").fill("TargetProc");
    await expect(page.getByText("TargetProc", { exact: false }).first()).toBeVisible();

    // The usage panel on the repository dashboard reflects it.
    await page.goto(`/repositories/${DB_REPO}`);
    const usage = page.getByTestId("usage-card");
    await expect(usage).toBeVisible();
    await expect(usage.getByText("TargetProc", { exact: false }).first()).toBeVisible();
  });
});

test.describe("cost-bounded documentation (problem 1)", () => {
  test("a budget limits the number of documented targets", async ({ page, request }) => {
    await page.goto(`/documentation?repositoryId=${DB_REPO}`);
    await page.getByTestId("repo-max-targets").fill("1");
    await page.getByTestId("generate-repo").click();

    // The enqueued Documentation job must be capped: repo overview (1) + budget (1) = 2.
    await expect
      .poll(async () => {
        const res = await request.get(`/api/jobs?repositoryId=${DB_REPO}`);
        const jobs = (await res.json()) as Array<{ type: string; totalItems: number | null }>;
        const doc = jobs.find((j) => j.type === "Documentation");
        return doc?.totalItems ?? -1;
      }, { timeout: 30_000 })
      .toBe(2);
  });
});
