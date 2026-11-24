import { expect, test, type Page } from "@playwright/test";

async function signIn(page: Page) {
  const password = process.env.GUARDIAN_DEMO_PASSWORD;
  if (!password) throw new Error("Load GUARDIAN_DEMO_PASSWORD from the local demo environment.");
  await page.goto("/");
  await page.getByLabel("Email").fill(process.env.GUARDIAN_DEMO_EMAIL ?? "reviewer@example.invalid");
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Analysis history" })).toBeVisible();
}

test("reviews the actual FlowForge reports and filters the breaking change", async ({ page }) => {
  await signIn(page);
  await page.getByRole("button", { name: /^Open FAIL report/ }).click();
  await expect(page.getByRole("heading", { name: "Contract analysis" })).toBeVisible();
  await expect(page.getByText("3107066933", { exact: true })).toBeVisible();
  await expect(page.getByText("GET /api/v1/runs/{run_id}", { exact: true })).toBeVisible();
  await page.getByLabel("Severity").selectOption("WARN");
  await page.getByLabel("Rule").fill("response-property-type-changed");
  await page.getByRole("button", { name: "Apply filters" }).click();
  await expect(page.getByText("No findings match.")).toBeVisible();
  await page.getByLabel("Severity").selectOption("ERR");
  await page.getByRole("button", { name: "Apply filters" }).click();
  await expect(page.getByText(/ERR .*response-property-type-changed/)).toBeVisible();
  await page.getByRole("button", { name: /Analysis history/ }).click();
  await page.getByRole("button", { name: /^Open PASS report/ }).click();
  await expect(page.getByText("3107071785", { exact: true })).toBeVisible();
  await expect(page.getByText("No findings match.")).toBeVisible();
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
});

test("shows loading, errors and empty history with a retry", async ({ page }) => {
  let failHistory: () => void = () => {};
  const failure = new Promise<void>(resolve => { failHistory = resolve; });
  await page.route("**/api/v1/projects/*/analyses*", async route => {
    await failure;
    await route.fulfill({ status: 503 });
  }, { times: 1 });
  await signIn(page);
  await expect(page.getByRole("status")).toHaveText(/Loading analyses/);
  failHistory();
  await expect(page.getByRole("alert")).toHaveText(/Could not reach Guardian/);
  await page.route("**/api/v1/projects/*/analyses*", route =>
    route.fulfill({ json: { items: [], next_cursor: null } }));
  await page.getByRole("button", { name: "Retry" }).click();
  await expect(page.getByText("No analyses have been published.")).toBeVisible();
});

test("hides protected content when a session expires", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await signIn(page);
  await page.route(/\/api\/v1\/analyses\/[^/?]+$/, route => route.fulfill({ status: 401 }));
  await page.getByRole("button", { name: /^Open FAIL report/ }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Contract analysis" })).toHaveCount(0);
});
