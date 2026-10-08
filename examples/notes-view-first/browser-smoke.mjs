import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const dll = fileURLToPath(new URL("./bin/Release/net10.0/NotesViewFirst.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--serve-only", ...(process.env.RUNIC_WEB_ROOT ? ["--web-root", process.env.RUNIC_WEB_ROOT] : []), ...(process.env.RUNIC_VIEW_LOCATOR === "splat" ? ["--splat"] : []), ...(process.env.RUNIC_VERIFY_WEB_MOUNT === "1" ? ["--verify-web-mount"] : [])], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let browser;
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, timeout) => waitFor(condition, { timeout, label: "the composed Notes journey", detail });
try {
  let url;
  await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    url = output.match(/https?:\/\/[^\s]+/)?.[0];
    return url;
  });
  browser = await launchChromium(url, { profilePrefix: "runic-notes-composed-" });
  const { evaluate } = browser;
  const query = expression => evaluate(expression);
  const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
  const change = (selector, value) => evaluate(`(() => { const field = document.querySelector(${JSON.stringify(selector)}); field.value = ${JSON.stringify(value)}; field.dispatchEvent(new Event("input", { bubbles: true })); field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()`);
  const snapshot = route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`);
  // Clicks a button that has focus, as a keyboard or pointer activation leaves it.
  const press = selector => evaluate(`(() => { const button = document.querySelector(${JSON.stringify(selector)}); button.focus(); button.click(); return true; })()`);
  const focusReturnsTo = (selector, label) => waitFor(async () => await query(`document.activeElement === document.querySelector(${JSON.stringify(selector)})`) === true,
    { label: `focus returning to ${label} after its command`, detail });

  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Welcome to composed Notes");
  const firstShell = await snapshot("shell");
  const sidebarId = firstShell.state.sidebar.id;
  if (!sidebarId || firstShell.state.dialog !== null) throw new Error("Wrong initial shell state.");

  // The command disables its button while the navigation runs; focus comes back afterwards.
  await press("[data-go=notes]");
  await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Editor");
  await focusReturnsTo("[data-go=notes]", "Notes");
  // The same for a control that stays disabled across a frame, as a slower navigation leaves it.
  // Browsers that move focus off a disabled control do it at the next rendering update;
  // blur() does the same here, so the check holds in every browser.
  const disabledAcrossFrame = await query(`(async () => {
    const frame = () => new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0)));
    const button = document.querySelector("[data-go=notes]");
    button.focus();
    button.disabled = true;
    button.blur();
    await frame();
    const lost = document.activeElement !== button;
    button.disabled = false;
    await frame();
    return { lost, returned: document.activeElement === button };
  })()`);
  if (!disabledAcrossFrame.returned)
    throw new Error(`Focus did not return to a re-enabled button: ${JSON.stringify(disabledAcrossFrame)}`);
  const documentId = (await snapshot("shell")).state.main.id;
  const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
  await change("#document-pane input", "Draft");
  await change("#document-pane textarea", "Line one");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.title === "Draft");

  // Save declares SaveFailure: a missing title shows the frontend's typed text.
  const alerts = 'Array.from(document.querySelectorAll("#document-pane [role=alert]")).map(alert => alert.textContent.trim()).filter(Boolean)';
  await change("#document-pane input", " ");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.title === " ");
  await click("[data-save]");
  try {
    await retry(async () => (await query(alerts)).includes("A note needs a title."));
  } catch (error) {
    throw new Error(`The declared failure was not shown: ${JSON.stringify(await query(alerts))}; ${error}`);
  }
  await change("#document-pane input", "Draft");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.title === "Draft");

  await press("[data-pane=preview]");
  await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Draft");
  await focusReturnsTo("[data-pane=preview]", "Preview");
  if (await query('document.querySelector("#document-pane p")?.textContent') !== "Line one")
    throw new Error("Preview did not observe Editor state.");
  const detachedEditor = await snapshot(`content${editorId}`);
  if (detachedEditor.error?.kind !== "disconnected") throw new Error("Nested Editor endpoint stayed active after its outlet changed.");
  if (!(await snapshot(`content${sidebarId}`)).ok) throw new Error("Independent sidebar disconnected with the Editor.");

  await click("[data-pane=editor]");
  await retry(async () => await query('document.querySelector("#document-pane input")?.value') === "Draft");
  await change("#document-pane textarea", "Line two");
  await click("[data-save]");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.canSave === false);
  await click("[data-pane=preview]");
  await retry(async () => await query('document.querySelector("#document-pane p")?.textContent') === "Line two");
  await pause(350);
  await click("[data-pane=editor]");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.savedMessage === "Saved Draft");
  await retry(async () => await query('document.querySelector("[data-message]")?.textContent') === "Saved Draft");

  await change("#document-pane input", "Throwaway");
  await retry(async () => (await snapshot(`content${editorId}`)).state?.isDirty === true);
  await evaluate('document.querySelector("[data-go=home]").focus()');
  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#modal [role=dialog]") !== null'));
  const dialogId = (await snapshot("shell")).state.dialog.id;
  await retry(async () => await query('document.querySelector("#modal [data-cancel]")?.disabled === false'));
  await evaluate('document.activeElement.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }))');
  await retry(async () => await query('document.querySelector("#modal [role=dialog]") === null'));
  if (await query('document.querySelector("#main h1")?.textContent') !== "Document")
    throw new Error("Cancel unexpectedly left the document.");
  // Home is unavailable until its Back ends, so focus returns once it is enabled again.
  await waitFor(async () => await query('document.activeElement?.getAttribute("data-go")') === "home",
    { label: "the dialog returning focus to the sidebar", detail });
  if ((await snapshot(`content${dialogId}`)).error?.kind !== "disconnected")
    throw new Error("Closed dialog endpoint stayed active.");

  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#modal [data-confirm]")?.disabled === false'));
  await click("[data-confirm]");
  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Welcome to composed Notes");
  await click("[data-go=notes]");
  await retry(async () => await query('document.querySelector("#document-pane input")?.value') === "Draft");
  await evaluate("location.reload()");
  await retry(async () => await query('document.querySelector("#document-pane input")?.value') === "Draft");
  try {
    await retry(async () => await query('document.querySelector("#status")?.textContent') === "Connected.");
  } catch (error) {
    throw new Error(`Reconnect did not settle: ${await query('document.querySelector("#status")?.textContent')}; ${error}`);
  }
  if (process.env.RUNIC_VERIFY_WEB_MOUNT === "1") {
    host.stdin.end("\n");
    await retry(() => host.exitCode !== null);
    if (host.exitCode !== 0 || !output.includes("WEB_MOUNT_BROWSER_OK"))
      throw new Error(`Web mount verification failed: ${output}\n${errors}`);
  }
  console.log("NOTES_COMPOSED_OK|sidebar|nested-pane|declared-failure|modal|discard|async-detach|reload" + (process.env.RUNIC_VERIFY_WEB_MOUNT === "1" ? "|web-mount" : ""));
} finally {
  try { await browser?.close(); }
  finally {
    if (!host.stdin.writableEnded) host.stdin.end("\n");
    await pause(300);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
