// Opens a built template application's page in Chromium and fails on any Content-Security-Policy violation.
//
//   bun check-template-page.mjs --host desktop|cswebui [--policy-from <Program.cs>] [--meta-policy] -- <command>...
//   bun check-template-page.mjs --self-check
//
// The command starts the application with RUNIC_APPLICATION_SERVE_ONLY=1 and without DOTNET_ENVIRONMENT, as a
// production build runs, so the host serves the page with the policy the generated Program.cs sets. With
// --policy-from one of the served document's Content-Security-Policy headers must be exactly that policy; without it
// the document must have none (CS-WebUI sends no headers). --meta-policy requires a <meta> policy, which SvelteKit's
// kit.csp adds. The check waits for the Bridge, shows the Counter View, increments it and fails when the page
// reported a violation event or Chromium logged a policy message. A serve-only Runic Desktop application starts
// without DISPLAY and WAYLAND_DISPLAY, as it needs none; CS-WebUI calls gtk_init and keeps them. --host comes first
// because Bun drops a leading "--" from a script's arguments.
// --self-check serves a page with a blocked inline style and fails unless the collector reports it.
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { createServer } from "node:http";
import { chromium } from "playwright-core";
import { watchCspViolations } from "./csp-violations.mjs";

const timeout = 60_000;

/** The policy a generated Program.cs sends outside Development. */
export function programPolicy(source) {
  const policies = [...source.matchAll(/^const string contentSecurityPolicy = "([^"]+)";$/gm)].map(match => match[1]);
  if (policies.length !== 1) throw new Error(`Expected one contentSecurityPolicy in Program.cs, found ${policies.length}.`);
  return policies[0];
}

async function launch() {
  const executablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
  if (!executablePath) throw new Error("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH names no Chromium.");
  // No bypassCSP: the page runs under its real policy.
  return chromium.launch({ executablePath, headless: true, timeout });
}

/** Opens a page with a blocked inline style attribute; the collector must report it as an event and a message. */
export async function selfCheck() {
  const server = createServer((_request, response) => {
    response.writeHead(200, { "content-type": "text/html", "content-security-policy": "default-src 'self'" });
    response.end('<!doctype html><title>CSP self-check</title><p style="color: red">blocked style</p>');
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const browser = await launch();
  try {
    const page = await browser.newPage();
    const csp = await watchCspViolations(page);
    await page.goto(`http://127.0.0.1:${server.address().port}/`, { waitUntil: "load", timeout });
    for (let waited = 0; (csp.violations.length === 0 || csp.messages.length === 0) && waited < 5_000; waited += 100)
      await page.waitForTimeout(100);
    const style = csp.violations.find(violation => violation.effectiveDirective === "style-src-attr");
    if (!style || csp.messages.length === 0)
      throw new Error(`The collector missed the blocked inline style:\n${csp.report() || "(nothing reported)"}`);
    return csp;
  } finally {
    await browser.close();
    server.close();
  }
}

function startApplication(command, noDisplay) {
  const env = { ...process.env, RUNIC_APPLICATION_SERVE_ONLY: "1" };
  delete env.DOTNET_ENVIRONMENT;
  delete env.ASPNETCORE_ENVIRONMENT;
  if (noDisplay) { delete env.DISPLAY; delete env.WAYLAND_DISPLAY; }
  // Its own process group, so a host that ignores the stop line ends with everything it started.
  const child = spawn(command[0], command.slice(1), { env, detached: true, stdio: ["pipe", "pipe", "pipe"] });
  const application = { child, output: "", errors: "", exited: null };
  child.stdout.on("data", chunk => { application.output += chunk; });
  child.stderr.on("data", chunk => { application.errors += chunk; });
  application.exit = new Promise(resolve => child.on("exit", (code, signal) => {
    application.exited = { code, signal };
    resolve(application.exited);
  }));
  child.on("error", error => { application.errors += `\n${error.stack}`; application.exited ??= { code: null, signal: "spawn" }; });
  return application;
}

async function applicationUrl(application) {
  for (let waited = 0; waited < timeout; waited += 100) {
    const url = application.output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
    if (url) return url;
    if (application.exited) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error("The application did not report RUNIC_APPLICATION_URL.");
}

async function stopApplication(application) {
  if (application.exited) return;
  // A line on standard input ends serving.
  application.child.stdin.end("\n");
  const stopped = await Promise.race([application.exit, new Promise(resolve => setTimeout(resolve, 30_000, null))]);
  if (!stopped) {
    try { process.kill(-application.child.pid, "SIGKILL"); } catch { /* already gone */ }
    await application.exit;
  }
}

const tail = text => text.split("\n").slice(-40).join("\n");

async function checkPage(url, expectedHeader, metaPolicy) {
  const browser = await launch();
  try {
    const page = await browser.newPage();
    const csp = await watchCspViolations(page);
    const response = await page.goto(url, { waitUntil: "load", timeout });
    if (!response?.ok()) throw new Error(`The page answered ${response?.status()}: ${url}`);
    // Runic Desktop adds a policy of its own (frame-ancestors 'self'); the browser enforces every header.
    const headers = await response.headerValues("content-security-policy");
    if (expectedHeader ? !headers.includes(expectedHeader) : headers.length > 0)
      throw new Error(`The page's Content-Security-Policy headers are ${JSON.stringify(headers)}; expected ${
        expectedHeader ? `one to be ${JSON.stringify(expectedHeader)}` : "none"}.`);
    const header = headers.join(" + ") || null;
    const metas = await page.locator('meta[http-equiv="content-security-policy" i]').count();
    if (metaPolicy && metas === 0) throw new Error("The page has no <meta> Content-Security-Policy.");
    // A Bridge state push and a command round trip: the Counter button enables once the Window is connected.
    await page.getByRole("button", { name: "Counter", exact: true }).click({ timeout });
    await page.getByRole("heading", { name: "Counter View" }).waitFor({ timeout });
    const count = page.locator(".count");
    await count.filter({ hasText: /^\s*0\s*$/ }).waitFor({ timeout });
    await page.getByRole("button", { name: "Increment", exact: true }).click({ timeout });
    await count.filter({ hasText: /^\s*1\s*$/ }).waitFor({ timeout });
    // Violations are reported asynchronously; give late ones, such as from a mount effect, time to arrive.
    await page.waitForTimeout(1_000);
    const report = csp.report();
    if (report) throw new Error(`The page violated its Content-Security-Policy:\n${report}`);
    return { header, metas };
  } finally {
    await browser.close();
  }
}

async function main(args) {
  if (args[0] === "--self-check") {
    const csp = await selfCheck();
    console.log(`BROWSER_CSP_SELF_CHECK_OK|${csp.violations.length} event(s)|${csp.messages.length} message(s)`);
    return;
  }
  const separator = args.indexOf("--");
  const host = args[0] === "--host" ? args[1] : null;
  if (!["desktop", "cswebui"].includes(host) || separator < 0 || separator === args.length - 1)
    throw new Error("Usage: check-template-page.mjs --host desktop|cswebui [options] -- <command>...");
  const options = args.slice(0, separator);
  const command = args.slice(separator + 1);
  const policyFrom = options.includes("--policy-from") ? options[options.indexOf("--policy-from") + 1] : null;
  const expectedHeader = policyFrom ? programPolicy(readFileSync(policyFrom, "utf8")) : null;
  const application = startApplication(command, host === "desktop");
  try {
    const url = await applicationUrl(application);
    const { header, metas } = await checkPage(url, expectedHeader, options.includes("--meta-policy"));
    console.log(`BROWSER_CSP_OK|header=${header ?? "none"}|meta=${metas}`);
  } catch (error) {
    error.message += `\napplication stdout:\n${tail(application.output)}\napplication stderr:\n${tail(application.errors)}`;
    throw error;
  } finally {
    await stopApplication(application);
  }
}

if (import.meta.main) {
  try {
    await main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
