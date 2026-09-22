import { defineConfig, devices } from "@playwright/test";
export default defineConfig({
  testDir: "./tests/web",
  testMatch: "**/*.spec.mjs",
  fullyParallel: false,
  workers: 1,
  use: {
    baseURL: "http://127.0.0.1:3000",
    trace: "retain-on-failure",
    launchOptions: process.env.CHROMIUM_EXECUTABLE
      ? {
          executablePath: process.env.CHROMIUM_EXECUTABLE,
          args: [
            "--no-sandbox",
            "--disable-dev-shm-usage",
            "--use-angle=swiftshader",
          ],
        }
      : {},
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "node tools/preview-web.mjs",
    url: "http://127.0.0.1:3000",
    reuseExistingServer: !process.env.CI,
  },
});
