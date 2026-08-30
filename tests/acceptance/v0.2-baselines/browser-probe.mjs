// CDP keeps browser automation out of the measurement dependency graph. The
// receipt records only stable logical commands, never temporary profile paths.
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import process from 'node:process';

export const SERVER_PORT = 5173;
export const CDP_PORT = 9222;
export class BrowserCleanupError extends Error {
  constructor() { super('browser-cleanup-failed'); this.code = 'browser-cleanup-failed'; }
}
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function eventually(action, timeout = 30_000) {
  const deadline = Date.now() + timeout; let last;
  while (Date.now() < deadline) { try { return await action(); } catch (error) { last = error; await sleep(100); } }
  throw new Error(String(last?.message ?? 'browser-probe-timeout').replace(/[\r\n].*/s, '').slice(0, 160));
}
async function boundedResponse(url, fetchFn, timeoutMs = 5_000) {
  const controller = new AbortController(); const timer = setTimeout(() => controller.abort(), timeoutMs);
  try { return await fetchFn(url, { signal: controller.signal }); } finally { clearTimeout(timer); controller.abort(); }
}
async function boundedTargets(port, fetchFn, timeoutMs = 5_000) {
  const controller = new AbortController(); const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetchFn(`http://127.0.0.1:${port}/json`, { signal: controller.signal });
    if (!response.ok) throw new Error('cdp-targets-not-ready');
    // Keep the abort signal live through body consumption and JSON parsing.
    const body = await response.text(); if (body.length > 1_000_000) throw new Error('cdp-targets-too-large');
    return JSON.parse(body);
  } finally { clearTimeout(timer); controller.abort(); }
}
async function cdp(port, expression, fetchFn = fetch, WebSocketImpl = WebSocket) {
  const targets = await boundedTargets(port, fetchFn);
  const target = targets.find(item => item.type === 'page'); if (!target) throw new Error('no-browser-page');
  const socket = new WebSocketImpl(target.webSocketDebuggerUrl);
  try {
    const response = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('cdp-timeout')), 5_000); const done = value => { clearTimeout(timer); value instanceof Error ? reject(value) : resolve(value); };
      socket.addEventListener('open', () => { const id = 1; socket.addEventListener('message', event => { const message = JSON.parse(event.data); if (message.id === id) done(message); }); socket.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, returnByValue: true } })); }, { once: true });
      socket.addEventListener('error', () => done(new Error('cdp-error')), { once: true });
    });
    return response.result?.result?.value;
  } finally { try { socket.close(); } catch {} }
}
export const visibleExpression = token => `(() => { const e = document.querySelector('[data-e2e-view]'); if (!e || ${token === undefined ? 'false' : `e.getAttribute('data-baseline-token') !== ${JSON.stringify(token)}`}) return false; const s = getComputedStyle(e), r = e.getBoundingClientRect(); return s.display !== 'none' && s.visibility !== 'hidden' && Number(s.opacity) !== 0 && r.width > 0 && r.height > 0; })()`;
async function stop(child, timeoutMs) {
  if (!child) return;
  const closed = new Promise(resolve => child.once?.('close', resolve) ?? resolve());
  try { process.platform === 'win32' ? child.kill('SIGKILL') : process.kill(-child.pid, 'SIGKILL'); } catch { try { child.kill?.('SIGKILL'); } catch {} }
  let closedInTime = false;
  await new Promise(resolve => { const timer = setTimeout(resolve, timeoutMs); closed.then(() => { closedInTime = true; clearTimeout(timer); resolve(); }); });
  if (!closedInTime) throw new BrowserCleanupError();
}
function spawnProcess(spawnFn, command, args, cwd) { return spawnFn(command, args, { cwd, stdio: 'ignore', detached: process.platform !== 'win32' }); }
export function logicalBrowserArgv(kind) { return ['browser-dom-probe', kind, '--server-port=5173', '--cdp-port=9222', '--strictPort', '--isolated-profile']; }

async function session(options) {
  const { chromePath, serverCommand, serverArgs, cwd, url = `http://127.0.0.1:${SERVER_PORT}/`, spawnFn = spawn, fetchFn = fetch, cdpFn = cdp, closeTimeoutMs = 5_000 } = options;
  const profile = await mkdtemp(path.join(tmpdir(), 'runic-v02-chromium-'));
  let server; let browser;
  const close = async () => { const failures = await Promise.all([stop(browser, closeTimeoutMs), stop(server, closeTimeoutMs)].map(task => task.catch(error => error))); await rm(profile, { recursive: true, force: true }); const failure = failures.find(value => value instanceof Error); if (failure) throw failure; };
  try {
    server = spawnProcess(spawnFn, serverCommand, serverArgs, cwd);
    await eventually(async () => { const response = await boundedResponse(url, fetchFn); if (!response.ok) throw new Error('server-not-ready'); });
    browser = spawnProcess(spawnFn, chromePath, [`--remote-debugging-port=${CDP_PORT}`, `--user-data-dir=${profile}`, '--headless=new', '--no-first-run', '--no-default-browser-check', url], cwd);
    await eventually(async () => { if (!(await cdpFn(CDP_PORT, visibleExpression()))) throw new Error('view-not-visible'); });
    return { cdpFn, close };
  } catch (error) { await close(); throw error; }
}

// The injected clock lets the regression test prove the launch boundary.
export async function startupSample(options) {
  const clock = options.clock ?? (() => process.hrtime.bigint()); const started = clock();
  const active = await session(options);
  try { return { nanoseconds: Number(clock() - started), argv: logicalBrowserArgv('startup') }; }
  finally { await active.close(); }
}

export async function warmReloadSamples(options) {
  const { warmups, samples, writeToken } = options; const clock = options.clock ?? (() => process.hrtime.bigint()); const active = await session(options);
  const observations = [];
  try {
    for (let index = 0; index < warmups + samples; index += 1) {
      const token = `runic-v02-hmr-${index}-${clock()}`; const started = clock();
      await writeToken(token);
      await eventually(async () => { if (!(await active.cdpFn(CDP_PORT, visibleExpression(token)))) throw new Error('exact-rendered-token-not-observed'); });
      if (index >= warmups) observations.push(Number(clock() - started));
    }
    return { observations, argv: logicalBrowserArgv('reload') };
  } finally { await active.close(); }
}
