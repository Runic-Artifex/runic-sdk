// @vitest-environment node
// useField's attachment in Chromium: a checked text field written on blur, a number input typed key by key, a
// checkbox, and a rejected checked write, against the mock's slow replies. Skipped without
// PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH (the dev shell and CI set it).
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { chromium, type Browser, type Page } from "playwright-core";
import { build, type Rolldown } from "vite";
import { afterAll, beforeAll, describe, expect, test } from "vitest";

const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];
type FieldTest = { fieldTest: { state(): Record<string, unknown>; sets(member: string): unknown[] } };

describe.skipIf(!chromiumPath)("useField with {@attach} in Chromium", () => {
  let browser: Browser;
  let script: string;
  beforeAll(async () => {
    const output = await build({
      configFile: false, logLevel: "silent", plugins: [svelte()], resolve: { conditions: ["browser"] },
      define: { "process.env.NODE_ENV": '"development"' },
      build: { write: false, minify: false, rolldownOptions: { input: new URL("browser/field-page.ts", import.meta.url).pathname } },
    }) as Rolldown.RolldownOutput;
    const chunks = output.output.filter(item => item.type === "chunk");
    expect(chunks.length).toBe(1);
    script = chunks[0]!.code;
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
  }, 60_000);
  afterAll(async () => { await browser?.close(); });

  async function open(): Promise<{ tab: Page; errors: string[] }> {
    const tab = await browser.newPage();
    const errors: string[] = [];
    tab.on("pageerror", error => errors.push(String(error)));
    // The runtime needs crypto.randomUUID, which a page has only in a secure context such as http://localhost.
    await tab.route("http://localhost/**", route => new URL(route.request().url()).pathname === "/page.js"
      ? route.fulfill({ contentType: "text/javascript", body: script })
      : route.fulfill({ contentType: "text/html", body: '<!doctype html><div id="root"></div><script type="module" src="/page.js"></script>' }));
    await tab.goto("http://localhost/");
    await tab.locator("#title[data-ready=true]").waitFor();
    return { tab, errors };
  }
  const state = (tab: Page) => tab.evaluate(() => (globalThis as unknown as FieldTest).fieldTest.state());
  const sets = (tab: Page, member: string) => tab.evaluate(name => (globalThis as unknown as FieldTest).fieldTest.sets(name), member);

  test("writes a checked text field on blur and shows a rejected write's draft", async () => {
    const { tab, errors } = await open();
    const title = tab.locator("#title");
    await title.click();
    await title.pressSequentially("Hello", { delay: 10 });
    await tab.keyboard.press("Tab");
    await tab.locator("#title[data-editing=false]").waitFor();
    expect((await state(tab))["title"]).toBe("Hello");
    await title.fill("Bad");
    await tab.keyboard.press("Tab");
    await tab.locator("#title[data-error=rejected]").waitFor();
    expect(await title.inputValue()).toBe("Bad");
    expect(await title.getAttribute("aria-invalid")).toBe("true");
    expect((await state(tab))["title"]).toBe("Hello");
    expect(errors).toEqual([]);
    await tab.close();
  }, 60_000);

  test("a number input keeps `-` and `1.` while typing and writes numbers; a checkbox writes booleans", async () => {
    const { tab, errors } = await open();
    const step = tab.locator("#step");
    await step.fill("");
    await step.pressSequentially("-1.5", { delay: 30 });
    await tab.locator("#step[data-editing=false]").waitFor();
    expect(await step.inputValue()).toBe("-1.5");
    expect((await state(tab))["step"]).toBe(-1.5);
    const written = await sets(tab, "step");
    for (const value of written) expect(typeof value).toBe("number");
    expect(written.at(-1)).toBe(-1.5);
    await tab.locator("#done").click();
    await tab.locator("#done[data-editing=false]").waitFor();
    expect(await tab.locator("#done").isChecked()).toBe(true);
    expect((await state(tab))["done"]).toBe(true);
    expect(errors).toEqual([]);
    await tab.close();
  }, 60_000);
});
