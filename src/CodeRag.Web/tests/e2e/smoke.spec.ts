import { test, expect } from "@playwright/test";

/**
 * Full-app smoke test: every route renders, there is no 404 fallback, no uncaught page
 * errors, and the seeded fixture repositories are visible. This is the guard that "the UI
 * is not broken" as required by the task's definition of done.
 */

const routes: Array<[string, RegExp]> = [
  ["/", /Dashboard/],
  ["/repositories", /Repositories/],
  ["/search", /Search/],
  ["/documentation", /Documentation/],
  ["/jobs", /Jobs/],
  ["/tasks", /Tasks/],
  ["/knowledge", /Knowledge/],
  ["/review", /Review/],
  ["/settings", /Settings/],
  ["/diagnostics", /Diagnostics/],
];

test.describe("smoke: all pages render", () => {
  for (const [path, heading] of routes) {
    test(`renders ${path}`, async ({ page }) => {
      const errors: string[] = [];
      page.on("pageerror", (e) => errors.push(String(e)));

      await page.goto(path);
      await expect(page.getByRole("heading", { level: 1 }).first()).toContainText(heading);
      await expect(page.getByText(/Page not found/i)).toHaveCount(0);
      expect(errors, `uncaught page errors: ${errors.join("\n")}`).toEqual([]);
    });
  }

  test("sidebar navigation works", async ({ page }) => {
    await page.goto("/");
    await page.getByTestId("nav-repositories").click();
    await expect(page).toHaveURL(/\/repositories$/);
    await expect(page.getByRole("heading", { level: 1 }).first()).toContainText(/Repositories/);
  });

  test("seeded repositories are listed", async ({ page }) => {
    await page.goto("/repositories");
    await expect(page.getByText("e2e-app", { exact: false }).first()).toBeVisible();
    await expect(page.getByText("e2e-db", { exact: false }).first()).toBeVisible();
  });

  test("repository dashboard opens from the list", async ({ page }) => {
    await page.goto("/repositories");
    await page.getByText("e2e-db", { exact: false }).first().click();
    await expect(page).toHaveURL(/\/repositories\/[0-9a-f-]+$/);
    await expect(page.getByRole("heading", { level: 1 }).first()).toContainText(/e2e-db/);
  });
});
