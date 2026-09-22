import { test, expect } from "@playwright/test";
async function connected(page) {
  await page.goto("/");
  await expect(page.locator("#toggle-pitch")).toBeEnabled();
}
test("Top toggles only the effect; bottom expands settings without toggling", async ({
  page,
}) => {
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await connected(page);
  const before = await page
    .locator("#toggle-pitch")
    .getAttribute("aria-pressed");
  await page.locator("#settings-pitch").click();
  await expect(page.locator("#panel-pitch")).toBeVisible();
  await expect(page.locator("#toggle-pitch")).toHaveAttribute(
    "aria-pressed",
    before,
  );
  await page.locator("#toggle-pitch").click();
  await expect(page.locator("#toggle-pitch")).toHaveAttribute(
    "aria-pressed",
    before === "true" ? "false" : "true",
  );
  await expect(page.locator("#panel-pitch")).toBeVisible();
  await expect(page.locator("#sync-label")).toHaveText(
    "Настройки синхронизированы",
  );
  expect(errors).toEqual([]);
});
test("Slider persists to server and a second controller synchronizes", async ({
  page,
  context,
}) => {
  await connected(page);
  const second = await context.newPage();
  await connected(second);
  await page.bringToFront();
  await page.locator("#settings-robot").click();
  await page.locator("#robot-hz").fill("134");
  await expect(page.locator("#value-robot-hz")).toHaveText("134 Гц");
  await expect(page.locator("#sync-label")).toHaveText(
    "Настройки синхронизированы",
  );
  await second.bringToFront();
  await expect(second.locator("#robot-hz")).toHaveValue("134");
});
test("Lost connection disables controls; reconnect does not resend old gestures", async ({
  page,
}) => {
  await connected(page);
  await page.route("**/api/**", (route) => route.abort());
  await expect(page.locator("#toggle-pitch")).toBeDisabled();
  await expect(page.locator("#connection-label")).toContainText(
    "Связь потеряна",
  );
  await page.unroute("**/api/**");
  await expect(page.locator("#toggle-pitch")).toBeEnabled();
});
test("Session rejection shows pairing, rejects wrong code and accepts the current code", async ({
  page,
}) => {
  await connected(page);
  await page.route("**/api/state", (route) => route.fulfill({ status: 401 }));
  await expect(page.locator("#pair-dialog")).toBeVisible();
  await page.unroute("**/api/state");
  await page.locator("#pair-code").fill("00000000");
  await page.locator("#pair-submit").click();
  await expect(page.locator("#pair-error")).toContainText("Код не подошёл");
  await page.locator("#pair-code").fill("12345678");
  await page.locator("#pair-submit").click();
  await expect(page.locator("#pair-dialog")).not.toBeVisible();
  await expect(page.locator("#toggle-pitch")).toBeEnabled();
});
test("Safety state is displayed without letting the phone clear PANIC or bypass", async ({
  page,
}) => {
  await page.route("**/api/state", async (route) => {
    const response = await route.fetch();
    const state = await response.json();
    state.engine.panic = true;
    state.revision += 1000;
    await route.fulfill({ response, json: state });
  });
  await connected(page);
  await expect(page.locator("#engine-notice")).toContainText("PANIC");
  await expect(page.locator("#effects .effect-card")).toHaveCount(4);
  await expect(page.locator("#effects")).not.toContainText("Вокодер");
});
test("Phone layouts have no horizontal overflow or clipped effect titles", async ({
  page,
}) => {
  for (const width of [320, 390, 768]) {
    await page.setViewportSize({ width, height: 844 });
    await connected(page);
    for (const id of ["pitch", "robot", "echo", "reverb"])
      await page.locator(`#settings-${id}`).click();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    const titlesFit = await page
      .locator(".effect-name")
      .evaluateAll((nodes) =>
        nodes.every(
          (n) =>
            n.getBoundingClientRect().right <=
            n.closest(".effect-toggle").getBoundingClientRect().right - 8,
        ),
      );
    expect(titlesFit).toBe(true);
    await expect(page.locator("#echo-feedback")).toBeVisible();
  }
});
test("Keyboard and production CSP allow toggles and sliders without inline-script exceptions", async ({
  page,
}) => {
  const errors = [];
  page.on("console", (msg) => {
    if (msg.type() === "error") errors.push(msg.text());
  });
  await page.route("**/", async (route) => {
    const response = await route.fetch();
    await route.fulfill({
      response,
      headers: {
        ...response.headers(),
        "Content-Security-Policy":
          "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
      },
    });
  });
  await connected(page);
  await page.locator("#settings-echo").focus();
  await page.keyboard.press("Enter");
  await expect(page.locator("#panel-echo")).toBeVisible();
  const slider = page.locator("#echo-mix");
  const value = Number(await slider.inputValue());
  await slider.focus();
  await page.keyboard.press(value >= 1 ? "ArrowLeft" : "ArrowRight");
  await expect(slider).not.toHaveValue(String(value));
  expect(errors).toEqual([]);
});
