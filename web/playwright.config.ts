import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  use: { baseURL: process.env.GUARDIAN_WEB_URL ?? "http://127.0.0.1:5186" },
});
