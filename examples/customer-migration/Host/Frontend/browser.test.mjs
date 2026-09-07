import { stopHost } from "./host-process.mjs";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";

const here = dirname(fileURLToPath(import.meta.url));
const temporary = await mkdtemp(resolve(tmpdir(), "runic-customer-browser-"));
const file = resolve(temporary, "customers.json");
const host = spawn(
  process.env.RUNIC_CUSTOMER_HOST_EXECUTABLE ?? "dotnet",
  process.env.RUNIC_CUSTOMER_HOST_EXECUTABLE ? ["--serve"] : [
    resolve(
      here,
      `../bin/${process.env.CONFIGURATION ?? "Debug"}/net10.0/CustomerDesktop.dll`,
    ),
    "--serve",
  ],
  {
    cwd: here,
    env: { ...process.env, RUNIC_CUSTOMERS_FILE: file },
    stdio: ["pipe", "pipe", "pipe"],
  },
);
let output = "";
let browser;
let page;
try {
  const url = await new Promise((accept, reject) => {
    const timeout = setTimeout(
      () => reject(new Error(`Host did not start: ${output}`)),
      20000,
    );
    host.on("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    host.on("exit", (code) => {
      clearTimeout(timeout);
      reject(new Error(`Host exited ${code}: ${output}`));
    });
    host.stderr.on("data", (chunk) => {
      output += chunk;
    });
    host.stdout.on("data", (chunk) => {
      output += chunk;
      const match = output.match(/Customer editor: (https?:\/\/\S+)/);
      if (match) {
        clearTimeout(timeout);
        accept(match[1]);
      }
    });
  });
  browser = await chromium.launch({
    headless: true,
    ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH }
      : {}),
  });
  page = await browser.newPage({ viewport: { width: 1200, height: 920 } });
  page.setDefaultTimeout(10000);
  const errors = [];
  page.on("pageerror", (error) => {
    errors.push(error.message);
    console.error("Browser error:", error.message);
  });
  page.on("console", (message) => {
    if (message.type() === "error")
      console.error("Browser console:", message.text());
  });
  await page.goto(url);
  await page.getByLabel("Full name", { exact: true }).waitFor();
  await page.waitForFunction(
    () => document.querySelector("#name")?.value === "Alex Morgan",
  );
  assert.equal(
    await page
      .getByRole("button", { name: "Save customer", exact: true })
      .isDisabled(),
    true,
  );
  assert.equal(
    await page.evaluate(() => window.confirmCustomerClose()),
    true,
    "Clean editor can close",
  );
  await page.getByLabel("Find a customer").fill("fieldwork");
  assert.equal(await page.locator(".customer").count(), 1);
  await page.getByLabel("Find a customer").fill("");
  await page.getByLabel("Full name", { exact: true }).fill("X");
  await page.getByLabel("Email address").focus();
  await page.getByText("Enter a name with 2–100 characters.").waitFor();
  await page.getByLabel("Full name", { exact: true }).fill("Alex Browser");
  await page.evaluate(() => {
    window.closeResult = window.confirmCustomerClose();
  });
  const closePrompt = page.getByRole("dialog", {
    name: "Close without saving?",
  });
  await closePrompt.waitFor();
  await closePrompt.getByRole("button", { name: "Keep editing" }).click();
  assert.equal(await page.evaluate(() => window.closeResult), false);
  assert.equal(
    await page.getByLabel("Full name", { exact: true }).inputValue(),
    "Alex Browser",
  );
  await page.evaluate(() => {
    window.closeResult = window.confirmCustomerClose();
  });
  await closePrompt.waitFor();
  await page.keyboard.press("Escape");
  assert.equal(await page.evaluate(() => window.closeResult), false);
  await page.evaluate(() => {
    window.closeResult = window.confirmCustomerClose();
  });
  await closePrompt.waitFor();
  await closePrompt.getByRole("button", { name: "Discard and close" }).click();
  assert.equal(await page.evaluate(() => window.closeResult), true);
  await page.getByRole("button", { name: /Sam Rivera/ }).click();
  await page.getByRole("dialog").waitFor();
  await page.getByRole("button", { name: "Keep editing" }).click();
  assert.equal(
    await page.getByLabel("Full name", { exact: true }).inputValue(),
    "Alex Browser",
  );
  await page
    .getByRole("button", { name: "Save customer", exact: true })
    .click();
  await page.getByText("Customer saved", { exact: true }).waitFor();
  assert.equal(
    JSON.parse(await readFile(file, "utf8"))[0].Name,
    "Alex Browser",
  );
  await page.getByLabel("Company", { exact: true }).fill("Cancelled Company");
  await page
    .getByRole("button", { name: "Save customer", exact: true })
    .click();
  assert.equal(
    await page.evaluate(() => window.confirmCustomerClose()),
    false,
    "Saving editor denies close",
  );
  await page.getByRole("button", { name: "Cancel save", exact: true }).click();
  await page
    .getByText("Save cancelled; your draft is unchanged", { exact: true })
    .waitFor();
  assert.equal(
    await page.getByLabel("Company", { exact: true }).inputValue(),
    "Cancelled Company",
  );
  assert.equal(
    JSON.parse(await readFile(file, "utf8"))[0].Company,
    "Northstar Studio",
  );
  await page.getByRole("button", { name: "Reconnect", exact: true }).click();
  await page.waitForFunction(
    () => !document.querySelector(".bottom button")?.disabled,
  );
  assert.equal(
    await page.getByLabel("Company", { exact: true }).inputValue(),
    "Cancelled Company",
  );
  await page
    .getByRole("button", { name: "Discard changes", exact: true })
    .click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Discard changes", exact: true })
    .click();
  assert.equal(
    await page.getByLabel("Company", { exact: true }).inputValue(),
    "Northstar Studio",
  );
  await page.getByLabel("Import contact details").setInputFiles({
    name: "contact.json",
    mimeType: "application/json",
    buffer: Buffer.from(
      JSON.stringify({
        name: "Imported Person",
        email: "import@example.com",
        company: "Imported Studio",
      }),
    ),
  });
  await page.waitForFunction(
    () => document.querySelector("#name")?.value === "Imported Person",
  );
  await page
    .getByRole("button", { name: "Save customer", exact: true })
    .click();
  await page.getByText("Customer saved", { exact: true }).waitFor();
  assert.equal(
    JSON.parse(await readFile(file, "utf8"))[0].Name,
    "Imported Person",
  );
  await page.getByRole("button", { name: /Sam Rivera/ }).click();
  assert.equal(
    await page.getByLabel("Full name", { exact: true }).inputValue(),
    "Sam Rivera",
  );
  await page.getByLabel("Email address").fill("import@example.com");
  await page
    .getByRole("button", { name: "Save customer", exact: true })
    .click();
  await page.getByText("Another customer uses this email address.").waitFor();
  assert.equal(
    await page.getByLabel("Email address").getAttribute("aria-invalid"),
    "true",
  );
  if (process.env.RUNIC_CUSTOMER_SCREENSHOT)
    await page.screenshot({
      path: process.env.RUNIC_CUSTOMER_SCREENSHOT,
      fullPage: true,
    });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
    true,
    "Mobile layout must not overflow",
  );
  assert.deepEqual(errors, []);
  console.log(
    "Customer migration browser acceptance passed: live C# bridge, validation, save, cancel, dirty navigation, close confirmation, reconnect, import, uniqueness, responsive layout.",
  );
} catch (error) {
  if (page) console.error("Page:", (await page.locator("body").innerText()).slice(0, 5000));
  throw error;
} finally {
  await browser?.close();
  try { await stopHost(host); }
  finally { await rm(temporary, { recursive: true, force: true }); }
}
