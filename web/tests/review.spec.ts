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
  await expect(page.getByText("CI evidence unverified", { exact: true })).toBeVisible();
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

test("lets a maintainer retry verification and keeps the original verdict", async ({ page }) => {
  let verified = false;
  const digest = "sha256:" + "d".repeat(64);
  await page.route(/\/api\/v1\/analyses\/[^/?]+$/, async route => {
    const response = await route.fetch();
    const detail = await response.json();
    detail.provenance.ci_provider = "github";
    detail.verification = verified ? { status: "VERIFIED", image_digest: digest } : { status: "UNVERIFIED" };
    await route.fulfill({ response, json: detail });
  });
  await page.route("**/api/v1/analyses/*/verification", route => route.fulfill({ status: 409 }), { times: 1 });
  await signIn(page);
  await page.getByRole("button", { name: /^Open FAIL report/ }).click();
  const button = page.getByRole("button", { name: "Verify CI evidence" });
  await button.click();
  await expect(page.getByRole("alert")).toHaveText("The report could not be matched to trusted CI evidence.");
  await page.route("**/api/v1/analyses/*/verification", route => {
    expect(route.request().headers()["x-csrf-token"]).toBeTruthy();
    verified = true;
    return route.fulfill({ json: { status: "VERIFIED" } });
  });
  await button.click();
  await expect(page.getByText("CI evidence verified", { exact: true })).toBeVisible();
  await expect(page.getByText(digest, { exact: true })).toBeVisible();
  await expect(page.getByText("FAIL", { exact: true })).toBeVisible();
  await expect(button).toHaveCount(0);
});
