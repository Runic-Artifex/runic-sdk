// Shared helpers for the example smoke scripts. Every wait is bounded and a
// timeout reports the last result, the last error and optional page and host
// detail, so a CI failure never ends with "no detail".
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

const configuredScale = Number(process.env.RUNIC_SMOKE_TIMEOUT_SCALE || 1);
if (!Number.isFinite(configuredScale) || configuredScale <= 0)
  throw new Error(`RUNIC_SMOKE_TIMEOUT_SCALE must be a positive number, got ${process.env.RUNIC_SMOKE_TIMEOUT_SCALE}.`);

/** Multiplies every smoke timeout; set RUNIC_SMOKE_TIMEOUT_SCALE on slow runners. */
export const timeoutScale = configuredScale;
export const scaled = milliseconds => Math.ceil(milliseconds * timeoutScale);
export const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

/** The last `limit` characters of process output. */
export const tail = (text, limit = 6000) => text.length > limit ? `…${text.slice(-limit)}` : text;

const describe = value => {
  if (value instanceof Error) return value.cause ? `${value.message} (cause: ${describe(value.cause)})` : value.message;
  try { return JSON.stringify(value) ?? String(value); } catch { return String(value); }
};

/** Rejects when `promise` does not settle within `milliseconds` (not scaled). */
export function within(promise, milliseconds, label = "operation") {
  let timer;
  const expired = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`${label} did not settle within ${milliseconds} ms`)), milliseconds);
  });
  return Promise.race([promise, expired]).finally(() => clearTimeout(timer));
}

async function collectDetail(detail) {
  if (!detail) return "";
  try { return `\n${await within(Promise.resolve().then(detail), scaled(5_000), "diagnostics")}`; }
  catch (error) { return `\n(diagnostics unavailable: ${describe(error)})`; }
}

/**
 * Polls `condition` until it returns a truthy value and returns that value.
 * Each attempt is bounded by the remaining time (at least one second) and, if
 * set, by `attemptTimeout`, so a hung page cannot stall the smoke and a hung
 * request is retried within `timeout`. `condition` receives an AbortSignal
 * that aborts when its attempt ends. `detail` may return page or host output
 * for the timeout error.
 */
export async function waitFor(condition, { timeout = 12_000, attemptTimeout, interval = 50, label = "the condition", detail } = {}) {
  const limit = scaled(timeout), minimumAttempt = scaled(1_000);
  const attemptLimit = attemptTimeout === undefined ? Infinity : scaled(attemptTimeout);
  const deadline = Date.now() + limit;
  let attempts = 0, unsettled = 0, lastValue, lastError;
  while (true) {
    attempts++;
    const attempt = new AbortController();
    try {
      const value = await within(Promise.resolve(attempt.signal).then(condition),
        Math.min(Math.max(deadline - Date.now(), minimumAttempt), attemptLimit), "attempt");
      if (value) return value;
      lastValue = value;
    } catch (error) {
      lastError = error;
      if (error?.message?.startsWith("attempt did not settle")) unsettled++;
    } finally { attempt.abort(); }
    if (Date.now() >= deadline) break;
    await pause(interval);
  }
  const last = [`last result: ${describe(lastValue)}`, ...(lastError ? [`last error: ${describe(lastError)}`] : [])].join("; ");
  const hung = unsettled ? `, ${unsettled} did not settle` : "";
  throw new Error(`Timed out after ${limit} ms waiting for ${label} (${attempts} attempts${hung}); ${last}${await collectDetail(detail)}`,
    { cause: lastError });
}

/** Repeats `action` until it completes without throwing and returns its result. */
export async function retry(action, options = {}) {
  return (await waitFor(async signal => ({ value: await action(signal) }), options)).value;
}

function exited(child) {
  return child.exitCode !== null || child.signalCode !== null;
}

/** Resolves true once `child` exits, or false after `milliseconds` (not scaled). */
export function waitForExit(child, milliseconds) {
  if (exited(child)) return Promise.resolve(true);
  return new Promise(resolve => {
    const onClose = () => { clearTimeout(timer); resolve(true); };
    const timer = setTimeout(() => { child.off("close", onClose); resolve(false); }, milliseconds);
    child.once("close", onClose);
  });
}

/**
 * Starts headless Chromium on `url` and connects to its page over the
 * DevTools protocol. Console messages and uncaught exceptions are retained
 * for `diagnostics()`.
 */
export async function launchChromium(url, { profilePrefix = "runic-smoke-", timeout = 12_000 } = {}) {
  const profile = await mkdtemp(join(tmpdir(), profilePrefix));
  const child = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, url,
  ], { stdio: "ignore" });
  const consoleLines = [];
  const pending = new Map();
  let socket, nextId = 0, closed = false;

  const remember = line => { consoleLines.push(line); if (consoleLines.length > 100) consoleLines.shift(); };
  const browser = {
    process: child,
    profile,
    /** Sends one DevTools command; rejects on protocol errors or after `timeout`. */
    async command(method, params = {}, { timeout: commandTimeout = 30_000 } = {}) {
      if (closed) throw new Error(`DevTools connection is closed; cannot send ${method}`);
      const id = ++nextId;
      const response = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
      try {
        socket.send(JSON.stringify({ id, method, params }));
        const message = await within(response, scaled(commandTimeout), `DevTools ${method}`);
        if (message.error || message.result?.exceptionDetails) throw new Error(JSON.stringify(message));
        return message.result;
      } finally { pending.delete(id); }
    },
    /** Evaluates `expression` in the page, awaiting promises, and returns its value. */
    async evaluate(expression, options) {
      return (await browser.command("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }, options)).result.value;
    },
    /** Recent console output, the page state and a bounded DOM excerpt. */
    async diagnostics() {
      let page;
      try {
        page = await browser.evaluate(`({ url: location.href, readyState: document.readyState,
          html: document.body?.innerHTML.slice(0, 4000) ?? null })`, { timeout: 2_000 });
      } catch (error) { page = { unavailable: describe(error) }; }
      const lines = consoleLines.length ? consoleLines.join("\n") : "(none)";
      return `page: ${page.url ?? ""} ${page.readyState ?? ""}${page.unavailable ? ` unavailable: ${page.unavailable}` : ""}\n` +
        `DOM: ${page.html ?? "(none)"}\nbrowser console:\n${lines}`;
    },
    kill() { child.kill("SIGKILL"); },
    /**
     * Closes the connection and stops Chromium, forcing it after 3 seconds,
     * then removes the profile. Throws if Chromium did not exit.
     */
    async close() {
      closed = true;
      socket?.close();
      if (!exited(child)) {
        const graceful = waitForExit(child, 3_000);
        child.kill("SIGTERM");
        if (!await graceful) {
          const forced = waitForExit(child, 3_000);
          child.kill("SIGKILL");
          if (!await forced) throw new Error(`Chromium did not exit after SIGKILL; its profile ${profile} was left for runner cleanup.`);
        }
      }
      await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    },
  };

  try {
    const port = await retry(async () => {
      if (exited(child)) throw new Error(`Chromium exited: ${child.exitCode ?? child.signalCode}`);
      const value = Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]);
      if (!value) throw new Error("DevToolsActivePort is incomplete");
      return value;
    }, { label: "the Chromium DevTools port", timeout });
    // A cold Chromium, notably on Windows runners, can accept a DevTools
    // request and not answer it. Each request is aborted after a few seconds
    // and retried, so one stalled request does not use the whole budget.
    let targets;
    const target = await waitFor(async signal => {
      if (exited(child)) throw new Error(`Chromium exited: ${child.exitCode ?? child.signalCode}`);
      targets = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal })).json();
      return targets.find(entry => entry.type === "page" && entry.url.startsWith(url));
    }, {
      label: `the Chromium page for ${url}`, timeout, attemptTimeout: 5_000,
      detail: () => `Chromium ${exited(child) ? `exited: ${child.exitCode ?? child.signalCode}` : "is running"}; DevTools port ${port}; ` +
        `last targets: ${targets ? describe(targets.map(entry => ({ type: entry.type, url: entry.url }))) : "(no reply)"}`,
    });
    socket = new WebSocket(target.webSocketDebuggerUrl);
    await within(new Promise((resolve, reject) => {
      socket.addEventListener("open", resolve, { once: true });
      socket.addEventListener("error", () => reject(new Error("DevTools WebSocket failed to open")), { once: true });
    }), scaled(timeout), "DevTools WebSocket");
    socket.addEventListener("message", ({ data }) => {
      const message = JSON.parse(data);
      if (message.id !== undefined) { pending.get(message.id)?.resolve(message); return; }
      if (message.method === "Runtime.consoleAPICalled") {
        const text = message.params.args.map(arg => arg.value ?? arg.description ?? arg.type).join(" ");
        remember(`[console.${message.params.type}] ${text}`);
      } else if (message.method === "Runtime.exceptionThrown") {
        const details = message.params.exceptionDetails;
        remember(`[exception] ${details.exception?.description ?? details.text}`);
      } else if (message.method === "Log.entryAdded") {
        remember(`[${message.params.entry.source}.${message.params.entry.level}] ${message.params.entry.text}`);
      }
    });
    socket.addEventListener("close", () => {
      closed = true;
      for (const { reject } of pending.values()) reject(new Error("DevTools connection closed"));
    });
    await browser.command("Runtime.enable");
    await browser.command("Log.enable");
    return browser;
  } catch (error) {
    await browser.close().catch(() => {});
    throw error;
  }
}
