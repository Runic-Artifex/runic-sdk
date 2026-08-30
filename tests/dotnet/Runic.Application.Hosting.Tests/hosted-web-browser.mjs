import { chromium } from "playwright-core";

const [url] = process.argv.slice(2);
const executablePath = process.env.WEBUI_BROWSER_PATH;
if (!url || !executablePath) throw new Error("Expected hosted-web URL and WEBUI_BROWSER_PATH.");

const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage"],
});
try {
  const page = await browser.newPage();
  await page.goto(url, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.dataset.result !== "pending", undefined, { timeout: 15_000 });
  const result = await page.evaluate(() => ({
    result: document.body.dataset.result,
    error: document.body.dataset.error,
    view: document.querySelector("#view")?.textContent,
    refresh: document.querySelector("#refresh")?.textContent,
    refreshResult: document.body.dataset.refresh,
  }));
  if (result.result !== "pass" || result.view !== "Reconnected" || result.refreshResult !== "pass" || result.refresh !== "Refresh events observed") {
    throw new Error(`Browser bridge smoke failed: ${JSON.stringify(result)}`);
  }
  console.log("hosted-web-browser-ok");
} finally {
  await browser.close();
}
