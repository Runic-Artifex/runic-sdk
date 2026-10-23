// Drives the CS-WebUI menu bar probe (CsWebUiWindowProbes --probe-menu-bar) from headless Chromium through the reserved
// __runicMenuBar* routes (specs/desktop/menu-bar-page.md): the state reaches the page in the platform's order, enabled
// follows the declaration and CanExecuteChanged, invoke answers executed, disabled and stale, SetMenuBar reaches a
// waiting page, and the shortcut lease belongs to the connection. The probe then checks what its commands ran.
// RUNIC_PROBE_EXECUTABLE selects a published probe; otherwise the Release build runs with dotnet.
import { spawn } from "node:child_process";
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
  const call = (route, request) => browser.evaluate(`(async () => JSON.parse(await window.__runicBridge.call(
    ${JSON.stringify(route)}${request === undefined ? "" : `, ${JSON.stringify(JSON.stringify(request))}`})))()`);
  const items = state => state.menus.flatMap(function flat(entry) { return entry.kind === "menu" ? entry.entries.flatMap(flat) : [entry]; });
  const item = (state, label) => items(state).find(entry => entry.label === label);
  const invoke = (state, label) => call("__runicMenuBarInvoke", { revision: state.revision, key: item(state, label).key });

  // The state reaches the page: the platform's order, the declaration and the commands' availability.
  let state = await retry(async () => {
    const current = await call("__runicMenuBarState");
    return current.kind === "state" && current.menus.length === 3 && item(current, "Save")?.enabled === true && current;
  }, "the menu bar state");
  check(state.protocol === 1 && state.placement === "page" && state.shortcuts === "page", "The state's envelope", state);
  const order = state.platform === "macos" ? ["Edit", "File", "View"] : ["File", "Edit", "View"];
  check(JSON.stringify(state.menus.map(menu => menu.label)) === JSON.stringify(order), "The menus' order", state.menus);
  const save = item(state, "Save");
  check(save.commandId === "file.save" && save.shortcut === "Primary+S" && save.accessKey === "S" && save.ariaKeyShortcuts, "Save", save);
  check(item(state, "Undo").enabled === false, "Undo, disabled in the declaration", item(state, "Undo"));
  check(item(state, "Mode A").checked === true && item(state, "Mode B").checked === false && item(state, "Mode B").radioGroup === "mode",
    "The radio items", items(state));

  // Invoke runs an enabled item; Lock makes Save's CanExecute false, which a waiting page receives.
  check((await invoke(state, "Save")).kind === "executed", "Invoking Save", state);
  const waited = call("__runicMenuBarWait", { after: state.revision });
  check((await invoke(state, "Lock")).kind === "executed", "Invoking Lock", state);
  state = await waited;
  state = await retry(async () => {
    const current = item(state, "Save").enabled ? await call("__runicMenuBarWait", { after: state.revision }) : state;
    state = current;
    return item(current, "Save").enabled === false && current;
  }, "Save disabled after its CanExecuteChanged");
  check((await invoke(state, "Save")).kind === "disabled", "Invoking the locked Save", state);
  check((await invoke(state, "Undo")).kind === "disabled", "Invoking the declared-disabled Undo", state);

  // Mode B replaces the bar with SetMenuBar: the waiting page receives it, and an item of the older bar is stale.
  const before = state;
  const replaced = call("__runicMenuBarWait", { after: state.revision });
  check((await invoke(state, "Mode B")).kind === "executed", "Invoking Mode B", state);
  state = await replaced;
  state = await retry(async () => {
    const current = item(state, "Mode B").checked ? state : await call("__runicMenuBarWait", { after: state.revision });
    state = current;
    return item(current, "Mode B").checked === true && current;
  }, "the bar SetMenuBar replaced");
  check(item(state, "Mode A").checked === false && item(state, "Save").enabled === false, "The replaced bar", items(state));
  check((await invoke(before, "Lock")).kind === "stale", "Invoking an item of the older bar", before);
  check((await call("__runicMenuBarInvoke", { revision: state.revision, key: "9m/9i" })).kind === "stale", "Invoking an unknown key", state);
  check((await call("__runicMenuBarInvoke", { revision: "x" })).kind === "invalid-request", "An invalid invoke", state);

  // The "app" lease belongs to this connection and is returned with "runic".
  check((await call("__runicMenuBarControl", { shortcuts: "app" })).shortcuts === "app", "Taking the app lease", state);
  check((await call("__runicMenuBarControl", { shortcuts: "runic" })).shortcuts === "page", "Returning the app lease", state);

  host.stdin.end("\n");
  const code = await waitFor(async () => {
    const value = await Promise.race([exited, pause(200).then(() => undefined)]);
    return value === undefined ? false : { code: value };
  }, { timeout: 20_000, label: "the probe's exit", detail });
  if (code.code !== 0 || !output.includes("CSWEBUI_MENU_BAR_OK"))
    throw new Error(`The probe exited with ${code.code}:\n${tail(output)}\n${tail(errors)}`);
  console.log("CSWEBUI_MENU_BAR_PAGE_OK|state|can-execute-changed|executed|disabled|stale|set-menu-bar|lease");
} finally {
  try { await browser?.close(); }
  finally {
    if (host.exitCode === null && !host.stdin.writableEnded) host.stdin.end("\n");
    await pause(250);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
