import { afterEach, beforeEach, expect, test } from "bun:test";
import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { launchChromium, scaled, waitFor, within } from "../../examples/shared/smoke.mjs";

// A Chromium stand-in. Each launch takes the next mode from
// RUNIC_STANDIN_MODES: "stall" writes DevToolsActivePort for a port that
// accepts connections and never answers, "serve" answers the DevTools
// requests launchChromium makes, "redirect" does too but opens the page
// without its query, as a server redirect that drops it does, and "exit"
// exits at once. Each launch logs its start page and the DevTools commands
// it answered.
const standIn = `#!${process.execPath}
import { appendFileSync, readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { join } from "node:path";
const log = process.env.RUNIC_STANDIN_LOG;
const launch = readFileSync(log, "utf8").split("\\n").filter(line => line.includes('"mode"')).length;
const mode = process.env.RUNIC_STANDIN_MODES.split(",")[launch] ?? "exit";
const profile = process.argv.find(arg => arg.startsWith("--user-data-dir=")).slice("--user-data-dir=".length);
const url = process.argv.at(-1);
appendFileSync(log, JSON.stringify({ mode, pid: process.pid, profile, url }) + "\\n");
const answered = method => appendFileSync(log, JSON.stringify({ pid: process.pid, method }) + "\\n");
const announce = port => writeFileSync(join(profile, "DevToolsActivePort"), port + "\\n/devtools/browser/stand-in\\n");
if (mode === "exit") process.exit(3);
if (mode === "stall") {
  const server = createServer(() => {}).listen(0, "127.0.0.1", () => announce(server.address().port));
} else {
  let location = url;
  const server = Bun.serve({
    hostname: "127.0.0.1", port: 0,
    fetch(request, server) {
      const path = new URL(request.url).pathname;
      if (path === "/json/list") return Response.json([{ type: "page", url: location,
        webSocketDebuggerUrl: "ws://127.0.0.1:" + server.port + "/devtools/page/1" }]);
      if (server.upgrade(request)) return;
      return new Response("not found", { status: 404 });
    },
    websocket: {
      message(socket, data) {
        const { id, method, params } = JSON.parse(data);
        answered(method);
        if (method === "Page.navigate") {
          const opened = new URL(params.url);
          if (mode === "redirect") opened.search = "";
          location = opened.href;
        }
        const result = method === "Runtime.evaluate" ? { result: { type: "string", value: location } } : {};
        socket.send(JSON.stringify({ id, result }));
      },
    },
  });
  announce(server.port);
}
`;

let directory, log;
const saved = {};
const entries = () => readFileSync(log, "utf8").split("\n").filter(Boolean).map(line => JSON.parse(line));
const launches = () => entries().filter(entry => entry.mode);
const commands = pid => entries().filter(entry => entry.pid === pid && entry.method).map(entry => entry.method);
const running = pid => { try { process.kill(pid, 0); return true; } catch { return false; } };
const warnings = [];
const warn = console.warn;

beforeEach(() => {
  directory = mkdtempSync(join(tmpdir(), "runic-smoke-helper-"));
  const executable = join(directory, "chromium.mjs");
  writeFileSync(executable, standIn);
  chmodSync(executable, 0o755);
  log = join(directory, "launches.log");
  writeFileSync(log, "");
  for (const name of ["PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH", "RUNIC_STANDIN_LOG", "RUNIC_STANDIN_MODES"]) saved[name] = process.env[name];
  process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH = executable;
  process.env.RUNIC_STANDIN_LOG = log;
  warnings.length = 0;
  console.warn = message => warnings.push(message);
});

afterEach(() => {
  console.warn = warn;
  for (const launch of launches()) if (running(launch.pid)) process.kill(launch.pid, "SIGKILL");
  for (const [name, value] of Object.entries(saved))
    if (value === undefined) delete process.env[name]; else process.env[name] = value;
  rmSync(directory, { recursive: true, force: true });
});

const url = "http://127.0.0.1:9/app?mode=variant";

test("launchChromium starts on about:blank and opens the page once it is connected", async () => {
  process.env.RUNIC_STANDIN_MODES = "serve";
  const browser = await launchChromium(url, { profilePrefix: "runic-smoke-helper-", timeout: 1_500 });
  try {
    const [launch] = launches();
    expect(launch.url).toBe("about:blank");
    expect(commands(launch.pid)).toEqual(["Runtime.enable", "Log.enable", "Page.bringToFront", "Page.navigate", "Runtime.evaluate"]);
    expect(await browser.evaluate("location.href")).toBe(url);
  } finally { await browser.close(); }
}, 30_000);

test("launchChromium fails when a redirect dropped the page's query", async () => {
  process.env.RUNIC_STANDIN_MODES = "redirect,serve";
  const error = await launchChromium(url, { profilePrefix: "runic-smoke-helper-", timeout: 1_500 }).catch(caught => caught);
  expect(error).toBeInstanceOf(Error);
  expect(error.message).toBe(`Chromium opened http://127.0.0.1:9/app for ${url}: a redirect dropped mode=variant, so the page ` +
    "would not see them. Request the page the server redirects to.");
  const all = launches();
  expect(all.map(launch => launch.mode)).toEqual(["redirect"]);
  expect(running(all[0].pid)).toBe(false);
  expect(existsSync(all[0].profile)).toBe(false);
  expect(warnings).toHaveLength(0);
}, 30_000);

test("launchChromium relaunches a Chromium whose DevTools never answered with a fresh profile", async () => {
  process.env.RUNIC_STANDIN_MODES = "stall,serve";
  const browser = await launchChromium(url, { profilePrefix: "runic-smoke-helper-", timeout: 1_500 });
  try {
    expect(browser.launches).toBe(2);
    const [stalled, served] = launches();
    expect([stalled.mode, served.mode]).toEqual(["stall", "serve"]);
    expect(served.profile).not.toBe(stalled.profile);
    expect(browser.profile).toBe(served.profile);
    expect(running(stalled.pid)).toBe(false);
    expect(existsSync(stalled.profile)).toBe(false);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain("launch 1");
    expect(warnings[0]).toContain("Chromium was running but DevTools never answered");
    expect(warnings[0]).toContain("last targets: (no reply)");
  } finally { await browser.close(); }
  expect(existsSync(browser.profile)).toBe(false);
}, 30_000);

test("launchChromium fails after the bounded relaunch with every launch's diagnostics", async () => {
  process.env.RUNIC_STANDIN_MODES = "stall,stall,serve";
  const error = await launchChromium(url, { profilePrefix: "runic-smoke-helper-", timeout: 1_500 }).catch(caught => caught);
  expect(error).toBeInstanceOf(Error);
  expect(error.message).toContain("Chromium did not connect in 2 launches with fresh profiles");
  expect(error.message).toMatch(/launch 1 after \d+ ms \(Chromium was running but DevTools never answered\)/);
  expect(error.message).toMatch(/launch 2 after \d+ ms \(Chromium was running but DevTools never answered\)/);
  expect(error.message).toContain("Chromium is running; DevTools port");
  expect(error.message).toContain("wall clock");
  const all = launches();
  expect(all.map(launch => launch.mode)).toEqual(["stall", "stall"]);
  for (const launch of all) {
    expect(running(launch.pid)).toBe(false);
    expect(existsSync(launch.profile)).toBe(false);
  }
}, 30_000);

test("launchChromium does not relaunch a Chromium that exited", async () => {
  process.env.RUNIC_STANDIN_MODES = "exit,serve";
  const error = await launchChromium(url, { profilePrefix: "runic-smoke-helper-", timeout: 1_500 }).catch(caught => caught);
  expect(error.message).toContain("Chromium exited: 3");
  expect(launches().map(launch => launch.mode)).toEqual(["exit"]);
  expect(warnings).toHaveLength(0);
}, 30_000);

const block = milliseconds => { const end = performance.now() + milliseconds; while (performance.now() < end); };

test("within names a timer that fired late because the process stalled", async () => {
  const timeout = within(new Promise(() => {}), 50, "stalled operation").catch(error => error);
  block(800);
  const error = await timeout;
  expect(error.message).toMatch(/^stalled operation did not settle within 50 ms; wall clock \d+ ms against a 50 ms timer budget, so the process or runner stalled$/);
});

test("waitFor reports wall-clock time next to its budget and a stall beyond it", async () => {
  const budget = scaled(100);
  const quick = await waitFor(() => false, { timeout: 100, label: "nothing" }).catch(error => error);
  expect(quick.message).toMatch(/^Timed out after \d+ ms \(wall clock \d+ ms\) waiting for nothing \(\d+ attempts\); last result: false$/);
  const stalled = await waitFor(() => { block(scaled(2_500)); return false; }, { timeout: 100, label: "a stalled page" }).catch(error => error);
  expect(stalled.message).toMatch(/^Timed out after \d+ ms \(wall clock \d+ ms\) waiting for a stalled page \(1 attempts\); last result: false; wall clock \d+ ms against a \d+ ms timer budget, so the process or runner stalled$/);
  expect(stalled.message).toContain(`against a ${budget} ms timer budget`);
}, 15_000);
