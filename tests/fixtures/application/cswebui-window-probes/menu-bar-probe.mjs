// Drives the CS-WebUI menu bar probe (CsWebUiWindowProbes --probe-menu-bar) from headless Chromium through the reserved
// route runic.menu-bar on the Views channel (specs/desktop/menu-bar-page.md, "Protocol 2"): the state reaches the page
// in the platform's order, enabled follows the declaration and CanExecuteChanged, invoke answers executed, disabled and
// stale, SetMenuBar reaches a watching page through the connection's mailbox, and the shortcut lease belongs to the
// connection. The probe then checks what its commands ran.
// RUNIC_PROBE_EXECUTABLE selects a published probe; otherwise the Release build runs with dotnet.
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../../../../examples/shared/smoke.mjs";

const executable = process.env.RUNIC_PROBE_EXECUTABLE;
const dll = fileURLToPath(new URL("./bin/Release/net10.0/CsWebUiWindowProbes.dll", import.meta.url));
const host = spawn(executable ?? "dotnet", [...(executable ? [] : [dll]), "--probe-menu-bar"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "", browser;
host.stdout.on("data", chunk => { output += chunk; });
host.stderr.on("data", chunk => { errors += chunk; });
const exited = new Promise(resolve => host.on("exit", code => resolve(code)));
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, label) => waitFor(condition, { timeout: 20_000, label, detail });
const check = (condition, message, value) => {
  if (!condition) throw new Error(`${message}: ${JSON.stringify(value)}`);
};

try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Probe exited: ${errors}`);
    return output.match(/RUNIC_APPLICATION_URL=(\S+)/)?.[1];
  }, "probe URL");
  browser = await launchChromium(url, { profilePrefix: "runic-cswebui-menu-", timeout: 20_000 });
  await retry(() => browser.evaluate("window.__runicBridge?.isConnected() === true"), "the CS-WebUI connection");
  // The page keeps the menu-bar pushes, which runic-cswebui.js drains from the connection's mailbox.
  await browser.evaluate(`(() => {
    window.__menuBarStates = [];
    window.__runicBridge.onMessage(text => {
      const message = JSON.parse(text);
      if (message.kind === "menu-bar" && message.route === "runic.menu-bar") window.__menuBarStates.push(message.state);
    });
  })()`);
  const send = message => browser.evaluate(`(async () => JSON.parse(await window.__runicBridge.request(${JSON.stringify(JSON.stringify(message))})))()`);
  const hello = await send({ kind: "hello", protocol: 4, session: randomUUID(), client: { name: "menu-bar-probe", version: "0" } });
  check(hello.ok === true && hello.protocol === 4, "The hello", hello);
  const request = async (kind, members = {}) => {
    const reply = await send({ kind, route: "runic.menu-bar", ...members });
    check(reply.ok === true, `The ${kind} request`, reply);
    return reply;
  };
  const items = state => state.menus.flatMap(function flat(entry) { return entry.kind === "menu" ? entry.entries.flatMap(flat) : [entry]; });
  const item = (state, label) => items(state).find(entry => entry.label === label);
  const invoke = async (state, label) => (await request("menu-bar-invoke", { revision: state.revision, key: item(state, label).key })).result;
  // The latest state: the last push when it is newer than the one at hand.
  const latest = async state => {
    const pushed = await browser.evaluate("window.__menuBarStates.at(-1) ?? null");
    return pushed && pushed.revision > state.revision ? pushed : state;
  };

  // The state reaches the page: the platform's order, the declaration and the commands' availability.
  let state = (await request("menu-bar-watch")).state;
  state = await retry(async () => {
    state = await latest(state);
    return state.menus.length === 3 && item(state, "Save")?.enabled === true && state;
  }, "the menu bar state");
  check(state.protocol === 2 && state.kind === undefined && state.placement === "page" && state.shortcuts === "page",
    "The state's envelope", state);
  const order = state.platform === "macos" ? ["Edit", "File", "View"] : ["File", "Edit", "View"];
  check(JSON.stringify(state.menus.map(menu => menu.label)) === JSON.stringify(order), "The menus' order", state.menus);
  const save = item(state, "Save");
  check(save.commandId === "file.save" && save.shortcut === "Primary+S" && save.accessKey === "S" && save.ariaKeyShortcuts, "Save", save);
  check(item(state, "Undo").enabled === false, "Undo, disabled in the declaration", item(state, "Undo"));
  check(item(state, "Mode A").checked === true && item(state, "Mode B").checked === false && item(state, "Mode B").radioGroup === "mode",
    "The radio items", items(state));

  // Invoke runs an enabled item; Lock makes Save's CanExecute false, which the watching page receives as a push.
  check(await invoke(state, "Save") === "executed", "Invoking Save", state);
  check(await invoke(state, "Lock") === "executed", "Invoking Lock", state);
  state = await retry(async () => {
    state = await latest(state);
    return item(state, "Save").enabled === false && state;
  }, "Save disabled after its CanExecuteChanged");
  check(await invoke(state, "Save") === "disabled", "Invoking the locked Save", state);
  check(await invoke(state, "Undo") === "disabled", "Invoking the declared-disabled Undo", state);

  // Mode B replaces the bar with SetMenuBar: the watching page receives it, and an item of the older bar is stale.
  const before = state;
  check(await invoke(state, "Mode B") === "executed", "Invoking Mode B", state);
  state = await retry(async () => {
    state = await latest(state);
    return item(state, "Mode B").checked === true && state;
  }, "the bar SetMenuBar replaced");
  check(item(state, "Mode A").checked === false && item(state, "Save").enabled === false, "The replaced bar", items(state));
  check(await invoke(before, "Lock") === "stale", "Invoking an item of the older bar", before);
  check((await request("menu-bar-invoke", { revision: state.revision, key: "9m/9i" })).result === "stale", "Invoking an unknown key", state);
  const invalid = await send({ kind: "menu-bar-invoke", route: "runic.menu-bar", revision: "x" });
  check(invalid.ok === false && invalid.error?.kind === "rejected" && invalid.error?.reason === "invalid-request", "An invalid invoke", invalid);

  // The "app" lease belongs to this connection and is returned with "runic".
  check((await request("menu-bar-shortcuts", { shortcuts: "app" })).state.shortcuts === "app", "Taking the app lease", state);
  check((await request("menu-bar-shortcuts", { shortcuts: "runic" })).state.shortcuts === "page", "Returning the app lease", state);
  check((await request("menu-bar-unwatch")).ok === true, "Unwatching the bar", state);

  // A reload is a new connection: its receive loop starts again, a hello reusing the closed connection's session is
  // released, and the new connection's watch receives the next bar through its own mailbox.
  const session = randomUUID();
  check((await send({ kind: "hello", protocol: 4, session: "probe-reload-check", client: { name: "menu-bar-probe", version: "0" } })).ok === false,
    "A second hello on the connection", state);
  await browser.evaluate("location.reload()").catch(() => undefined);
  await retry(() => browser.evaluate("window.__runicBridge?.isConnected() === true && window.__menuBarStates === undefined"),
    "the reloaded page's connection");
  await browser.evaluate(`(() => {
    window.__menuBarStates = [];
    window.__runicBridge.onMessage(text => {
      const message = JSON.parse(text);
      if (message.kind === "menu-bar" && message.route === "runic.menu-bar") window.__menuBarStates.push(message.state);
    });
  })()`);
  const reloaded = await send({ kind: "hello", protocol: 4, session, client: { name: "menu-bar-probe", version: "0" } });
  check(reloaded.ok === true, "The reloaded page's hello", reloaded);
  state = (await request("menu-bar-watch")).state;
  check(await invoke(state, "Mode A") === "executed", "Invoking Mode A after the reload", state);
  state = await retry(async () => {
    state = await latest(state);
    return item(state, "Mode A").checked === true && state;
  }, "the bar Mode A pushed to the reloaded page");

  host.stdin.end("\n");
  const code = await waitFor(async () => {
    const value = await Promise.race([exited, pause(200).then(() => undefined)]);
    return value === undefined ? false : { code: value };
  }, { timeout: 20_000, label: "the probe's exit", detail });
  if (code.code !== 0 || !output.includes("CSWEBUI_MENU_BAR_OK"))
    throw new Error(`The probe exited with ${code.code}:\n${tail(output)}\n${tail(errors)}`);
  console.log("CSWEBUI_MENU_BAR_PAGE_OK|state|can-execute-changed|executed|disabled|stale|set-menu-bar|lease|reconnect");
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
