// @vitest-environment node
// Typing key by key into a `bind:value` input bound with useTextField in Chromium never loses a character (#673), with
// the mock's slow, optionally reordered replies. Skipped without PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH (the dev shell and CI set it).
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { chromium, type Browser } from "playwright-core";
import { build, type Rolldown } from "vite";
import { afterAll, beforeAll, describe, expect, test } from "vitest";

const chromiumPath = process.env["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"];
const sentence = "Typing key by key never loses a character.";
type Page = { textFieldTest: { shown: string[]; title(): unknown; sets(): number } };

describe.skipIf(!chromiumPath)("useTextField with bind:value in Chromium", () => {
  let browser: Browser;
  let script: string;
  beforeAll(async () => {
    const output = await build({
      configFile: false, logLevel: "silent", plugins: [svelte()], resolve: { conditions: ["browser"] },
      define: { "process.env.NODE_ENV": '"development"' },
      build: { write: false, minify: false, rolldownOptions: { input: new URL("browser/text-field-page.ts", import.meta.url).pathname } },
    }) as Rolldown.RolldownOutput;
    const chunks = output.output.filter(item => item.type === "chunk");
    expect(chunks.length).toBe(1);
    script = chunks[0]!.code;
    browser = await chromium.launch({ executablePath: chromiumPath!, headless: true });
  }, 60_000);
  afterAll(async () => { await browser?.close(); });

  for (const [reorder, coalesce] of [[false, true], [true, true], [true, false]] as const) {
    test(`keeps every key with latency "slow"${reorder ? " and reordered requests" : ""} (coalesce: ${coalesce})`, async () => {
      const tab = await browser.newPage();
      const errors: string[] = [];
      tab.on("pageerror", error => errors.push(String(error)));
      // The runtime needs crypto.randomUUID, which a page has only in a secure context such as http://localhost.
      await tab.route("http://localhost/**", route => new URL(route.request().url()).pathname === "/page.js"
        ? route.fulfill({ contentType: "text/javascript", body: script })
        : route.fulfill({ contentType: "text/html", body: '<!doctype html><div id="root"></div><script type="module" src="/page.js"></script>' }));
      await tab.goto(`http://localhost/?reorder=${reorder ? 1 : 0}&coalesce=${coalesce ? 1 : 0}`);
      const input = tab.locator("#title[data-ready=true]");
      await input.click();
      await input.pressSequentially(sentence, { delay: 10 });
      await tab.locator("#title[data-editing=false]").waitFor();
      expect(await input.inputValue()).toBe(sentence);
      const result = await tab.evaluate(() => {
        const { textFieldTest } = globalThis as unknown as Page;
        return { shown: textFieldTest.shown, title: textFieldTest.title(), sets: textFieldTest.sets() };
      });
      expect(result.title).toBe(sentence);
      for (const value of result.shown) expect(sentence.startsWith(value), value).toBe(true);
      if (coalesce) expect(result.sets).toBeLessThan(sentence.length);
      else expect(result.sets).toBe(sentence.length);
      expect(errors).toEqual([]);
      await tab.close();
    }, 60_000);
  }
});
