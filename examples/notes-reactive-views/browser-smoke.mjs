import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const dll = fileURLToPath(new URL("./bin/Release/net10.0/NotesReactiveViews.dll", import.meta.url));
const webRoot = process.env.RUNIC_WEB_ROOT;
const verifyClientDisconnect = process.env.RUNIC_VERIFY_CLIENT_DISCONNECT === "1";
const verifyPendingMount = process.env.RUNIC_VERIFY_PENDING_MOUNT === "1";
const verifyCallBurst = process.env.RUNIC_VERIFY_CALL_BURST === "1";
const host = spawn("dotnet", [dll, "--serve-only", ...(webRoot ? ["--web-root", webRoot] : []),
  ...(verifyClientDisconnect ? ["--verify-client-disconnect"] : [])], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let browser;
const detail = async () => `${browser ? await browser.diagnostics() : "browser: not started"}\n` +
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`;
const retry = (condition, timeout) => waitFor(condition, { timeout, label: "the Reactive Notes journey", detail });
try {
  let url;
  await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    url = output.match(/https?:\/\/[^\s]+/)?.[0];
    return url;
  });
  browser = await launchChromium(url, { profilePrefix: "runic-notes-reactive-" });
  const { command, evaluate } = browser;
  const query = expression => evaluate(expression);
  const click = async selector => {
    try {
      await retry(() => evaluate(`(() => {
        const element = document.querySelector(${JSON.stringify(selector)});
        if (!element || element.matches(":disabled")) return false;
        element.click();
        return true;
      })()`));
    } catch (cause) {
      throw new Error(`The Reactive Notes control did not become clickable: ${selector}\n${cause.message}`, { cause });
    }
  };
  const change = (selector, value) => evaluate(`(() => { const field = document.querySelector(${JSON.stringify(selector)}); field.value = ${JSON.stringify(value)}; field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()`);
  const snapshot = route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`);
  // WebUI sends a click event for each element with an id it has not marked,
  // and those collide with Bridge calls (#53); runic-cswebui.js marks them first.
  const expectClickEventsSuppressed = async moment => {
    const unmarked = await query('Array.from(document.querySelectorAll("[id]:not([data-webui_click_is_set])"), element => element.id)');
    if (unmarked.length !== 0)
      throw new Error(`WebUI can send click events ${moment} for: ${JSON.stringify(unmarked)}`);
  };

  await retry(async () => {
    if (await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes") return true;
    const status = await query('document.querySelector("#status")?.textContent');
    if (status && status !== "Connecting…") {
      const raw = await query('(async () => await window.__runicBridge.call("shellSnapshot"))()');
      throw new Error(`Browser status: ${status}; raw: ${raw}; host: ${errors}`);
    }
    return false;
  });
  await expectClickEventsSuppressed("after connecting");
  if (verifyCallBurst) {
    // Native WebUI can give two calls that arrive together one event slot, and
    // one of them then never receives a reply (#53). Unless the client sends
    // calls one admission at a time, a burst this size loses some.
    const count = 2000;
    const settled = await evaluate(`(async () => {
      let settled = 0;
      const calls = Array.from({ length: ${count} }, (_, index) => window.__runicBridge.call(
        index % 2 ? "shellSnapshot" : "__runicOperationStatus", "invalid-json").then(() => settled++));
      await Promise.race([Promise.all(calls), new Promise(resolve => setTimeout(resolve, 150_000))]);
      return settled;
    })()`, { timeout: 160_000 });
    if (settled !== count) throw new Error(`Concurrent Bridge calls lost replies: ${settled} of ${count} settled.`);
  }
  if (verifyClientDisconnect) {
    await click("[data-go=document]");
    await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
    await retry(async () => await query('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
    const documentId = (await snapshot("shell")).state.main.id;
    const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
    await retry(async () => (await snapshot(`content${editorId}`)).state?.activationCount === 1);
    browser.kill();
    await retry(() => output.includes("CLIENT_DISCONNECT_OBSERVED"));
    host.stdin.end("\n");
    try { await retry(() => host.exitCode !== null || host.signalCode !== null); }
    catch (cause) {
      throw new Error(`The Notes host did not exit after client disconnect. exit: ${host.exitCode}; signal: ${host.signalCode}; stdout: ${output}; stderr: ${errors}; ${cause}`);
    }
    if (host.exitCode !== 0 || !output.includes("CLIENT_DISCONNECT_OK"))
      throw new Error(`Client disconnect verification failed: exit: ${host.exitCode}; signal: ${host.signalCode}; ${output}\n${errors}`);
    console.log("REACTIVE_NOTES_CLIENT_DISCONNECT_OK");
  } else if (verifyPendingMount) {
    await evaluate(`(() => {
      const bridge = window.__runicBridge;
      const original = bridge.call;
      const invoke = original.bind(bridge);
      let held = false;
      bridge.call = (name, ...args) => {
        if (!held && name.startsWith("content") && name.endsWith("Mount")) {
          held = true;
          return new Promise((resolve, reject) => {
            window.__releaseMount = () => {
              bridge.call = original;
              invoke(name, ...args).then(resolve, reject);
              return true;
            };
          });
        }
        return invoke(name, ...args);
      };
      return true;
    })()`);
    await click("[data-go=document]");
    await retry(async () => await query('typeof window.__releaseMount') === "function");
    await click("[data-go=home]");
    await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
    await evaluate("window.__releaseMount()");
    await pause(100);
    if ((await snapshot("shell")).state.main.kind !== "home")
      throw new Error("A delayed mount changed the current route.");
    await click("[data-go=document]");
    await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
    await retry(async () => await query('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
    const documentId = (await snapshot("shell")).state.main.id;
    const editorId = (await snapshot(`content${documentId}`)).state.currentPane.id;
    const editorState = (await snapshot(`content${editorId}`)).state;
    if (editorState?.activationCount !== 1 || editorState?.deactivationCount !== 0)
      throw new Error(`A delayed mount left a stale activation: ${JSON.stringify(editorState)}`);
    if (await query('document.querySelector("#status")?.textContent') !== "Connected.")
      throw new Error("A delayed mount surfaced as a shell error.");
    console.log("REACTIVE_NOTES_PENDING_MOUNT_OK");
  } else {
  const firstShell = await snapshot("shell");
  if (firstShell.state.main.kind !== "home") throw new Error("Top-level router did not start at Home.");
  const originalPins = firstShell.state.pinned;
  if (originalPins.map(item => item.kind).join(",") !== "pinnedNote,pinnedTask")
    throw new Error(`The polymorphic View collection has the wrong initial items: ${JSON.stringify(originalPins)}`);
  for (const item of originalPins) {
    if (!(await snapshot(`content${item.id}`)).state)
      throw new Error(`The ${item.kind} View has no routed state.`);
  }
  if (webRoot) {
    await retry(async () => await query('document.querySelectorAll("#pinned [data-pin]").length') === 2);
    await retry(async () => await query('document.querySelector("#pinned [data-pin=pinnedTask]")?.textContent?.includes("High")') === true);
    await click("[data-pinned-action=swap]");
  } else {
    await evaluate('(async () => window.__runicBridge.call("shellSwapPinned"))()');
  }
  await retry(async () => (await snapshot("shell")).state.pinned[0]?.kind === "pinnedTask");
  const reorderedPins = (await snapshot("shell")).state.pinned;
  if (reorderedPins[0].id !== originalPins[1].id || reorderedPins[1].id !== originalPins[0].id)
    throw new Error("Reordering the View collection changed routed identities.");
  if (webRoot) await click("[data-pinned-action=remove]");
  else await evaluate('(async () => window.__runicBridge.call("shellRemovePinned"))()');
  await retry(async () => (await snapshot("shell")).state.pinned.length === 1);
  if ((await snapshot(`content${originalPins[0].id}`)).error?.kind !== "disconnected")
    throw new Error("Removing a View collection item kept its route connected.");
  if (!(await snapshot(`content${originalPins[1].id}`)).state)
    throw new Error("Removing a sibling View disconnected the surviving item.");
  if (webRoot) await click("[data-pinned-action=restore]");
  else await evaluate('(async () => window.__runicBridge.call("shellRestorePinned"))()');
  await retry(async () => (await snapshot("shell")).state.pinned.length === 2);
  const restoredPins = (await snapshot("shell")).state.pinned;
  if (restoredPins[0].id !== originalPins[0].id || restoredPins[1].id !== originalPins[1].id)
    throw new Error("Restoring a View collection item lost its stable route identity.");
  if (!(await snapshot(`content${originalPins[0].id}`)).state)
    throw new Error("Restoring a View collection item did not reconnect its route.");

  await click("[data-go=document]");
  await retry(async () => await query('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
  await retry(async () => await query('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
  await expectClickEventsSuppressed("after navigating");
  const documentId = (await snapshot("shell")).state.main.id;
  const documentRoute = `content${documentId}`;
  const documentState = (await snapshot(documentRoute)).state;
  if (documentState.currentPane.kind !== "editor" || documentState.compactNote.kind !== "editorCompact")
    throw new Error("Nested router or compact View contract chose the wrong presentation kind.");
  const editorId = documentState.currentPane.id;
  const compactId = documentState.compactNote.id;
  if (editorId === compactId) throw new Error("Two View contracts reused one presentation identity.");
  const editorRoute = `content${editorId}`;
  const compactRoute = `content${compactId}`;
  await retry(async () => (await snapshot(editorRoute)).state?.activationCount === 1);
  if ((await snapshot(compactRoute)).state?.activationCount !== 1)
    throw new Error("Two mounted Views activated one ViewModel more than once.");

  // Save declares SaveFailure: a missing title shows the frontend's typed text,
  // and ObserveBridgeExceptions keeps it away from ReactiveUI's default handler.
  await change("#document-pane [data-title]", " ");
  await retry(async () => (await snapshot(editorRoute)).state?.title === " ");
  await click("[data-save]");
  const alerts = 'Array.from(document.querySelectorAll("#document-pane [role=alert]")).filter(alert => !alert.hidden).map(alert => alert.textContent.trim())';
  try {
    await retry(async () => (await query(alerts)).includes("A note needs a title."));
  } catch (error) {
    throw new Error(`The declared failure was not shown: ${JSON.stringify(await query(alerts))}; ${error}`);
  }
  // The other case of the union: a title over 120 characters.
  const longTitle = "x".repeat(121);
  await change("#document-pane [data-title]", longTitle);
  await retry(async () => (await snapshot(editorRoute)).state?.title === longTitle);
  await click("[data-save]");
  await retry(async () => (await query(alerts)).includes("A title can have at most 120 characters."));
  await change("#document-pane [data-title]", "Shared note");
  await change("#document-pane [data-body]", "Both Views see this text.");
  await click("[data-save]");
  await retry(async () => (await snapshot(compactRoute)).state?.savedMessage === "Saved Shared note");
  await retry(async () => await query('document.querySelector("#compact-pane [data-title]")?.textContent') === "Shared note");
  await retry(async () => await query('document.querySelector("#compact-pane [data-body]")?.textContent') === "Both Views see this text.");

  await click("[data-pane=preview]");
  await retry(async () => await query('document.querySelector("#document-pane [data-heading]")?.textContent') === "Shared note");
  if ((await snapshot(editorRoute)).error?.kind !== "disconnected")
    throw new Error("The full editor endpoint stayed active after the nested route changed.");
  const whilePreview = (await snapshot(compactRoute)).state;
  if (whilePreview?.activationCount !== 1 || whilePreview?.deactivationCount !== 0)
    throw new Error("The remaining compact View lost the shared activation lease.");

  await click("[data-pane=editor]");
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  const remounted = (await snapshot(editorRoute)).state;
  if (remounted?.activationCount !== 1 || remounted?.deactivationCount !== 0)
    throw new Error("Remounting the full View restarted an already active ViewModel.");

  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  if ((await snapshot(compactRoute)).error?.kind !== "disconnected")
    throw new Error("The compact endpoint stayed active after leaving Document.");
  await click("[data-go=document]");
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  const returned = (await snapshot(editorRoute)).state;
  if (returned?.activationCount !== 2 || returned?.deactivationCount !== 1)
    throw new Error(`WhenActivated did not stop and restart across top-level routing: ${JSON.stringify(returned)}`);
  if ((await snapshot(compactRoute)).state?.activationCount !== 2)
    throw new Error("Compact View did not reconnect to the original ViewModel.");

  await evaluate("window.__runicReloadProbe = true");
  await command("Page.reload");
  try { await retry(async () => await query('window.__runicReloadProbe === undefined && document.readyState === "complete"')); }
  catch (cause) {
    const detail = await query('({ url: location.href, marker: window.__runicReloadProbe, ready: document.readyState, navigation: performance.getEntriesByType("navigation")[0]?.type, status: document.querySelector("#status")?.textContent })');
    throw new Error(`Reload did not create a new document: ${JSON.stringify(detail)}; host: ${output} ${errors}; ${cause}`);
  }
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  await retry(async () => await query('document.querySelector("#compact-pane [data-title]")?.textContent') === "Shared note");
  const afterReload = (await snapshot(editorRoute)).state;
  if (!afterReload || afterReload.activationCount - afterReload.deactivationCount !== 1)
    throw new Error(`Reload left an incorrect activation lease count: ${JSON.stringify(afterReload)}`);
  if ((await snapshot(compactRoute)).state?.activationCount !== afterReload.activationCount)
    throw new Error("The compact View did not reconnect in the new browser session.");

  await click("[data-go=home]");
  await retry(async () => await query('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  await click("[data-go=document]");
  await retry(async () => await query('document.querySelector("#document-pane [data-title]")?.value') === "Shared note");
  const afterReloadRoute = (await snapshot(editorRoute)).state;
  if (!afterReloadRoute || afterReloadRoute.activationCount !== afterReload.activationCount + 1
      || afterReloadRoute.deactivationCount !== afterReload.deactivationCount + 1)
    throw new Error(`Navigation after reload did not release and reacquire the lease: ${JSON.stringify(afterReloadRoute)}`);
  // CS-WebUI before 2.5.0-beta.4.6 did not terminate encoded UTF-8 replies, so a
  // large (over ~2 KB) reply could reach the page with stray trailing bytes. The
  // reply JSON escapes the non-ASCII title, so this checks size, not multi-byte text.
  const unicodeTitle = "Grüße, 日本語, emoji 🎉 — ".repeat(120);
  await change("#document-pane [data-title]", unicodeTitle);
  await retry(async () => (await snapshot(editorRoute)).state?.title === unicodeTitle);
  const corruptReplies = await evaluate(`(async () => {
    const corrupt = [];
    for (let index = 0; index < 300; index++) {
      const raw = await window.__runicBridge.call(${JSON.stringify(editorRoute + "Snapshot")});
      let title;
      try { title = JSON.parse(raw).state?.title; } catch {}
      if (title !== ${JSON.stringify(unicodeTitle)}) corrupt.push(String(raw).slice(-40));
      await window.__runicBridge.call("shellSnapshot");
    }
    return corrupt;
  })()`, { timeout: 60_000 });
  if (corruptReplies.length)
    throw new Error(`${corruptReplies.length} large Bridge replies were corrupted; endings: ${JSON.stringify(corruptReplies.slice(0, 3))}`);
  await change("#document-pane [data-title]", "Operation roundtrip");
  await retry(async () => (await snapshot(editorRoute)).state?.canSave === true);
  const requestId = "browser-operation-roundtrip";
  const admission = await evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(editorRoute + "StartSave")}, ${JSON.stringify(requestId)})))()`);
  if (admission.kind !== "accepted" || admission.requestId !== requestId || typeof admission.contract !== "string")
    throw new Error(`Advanced Save admission lost its wire identity: ${JSON.stringify(admission)}`);
  const identity = JSON.stringify({ contract: admission.contract, requestId });
  const terminal = await evaluate(`(async () => JSON.parse(await window.__runicBridge.call("__runicOperationWait", ${JSON.stringify(identity)})))()`);
  if (terminal.kind !== "succeeded" || terminal.contract !== admission.contract || terminal.requestId !== requestId)
    throw new Error(`Advanced Save did not finish through its window route: ${JSON.stringify(terminal)}`);
  const cancel = await evaluate(`(async () => JSON.parse(await window.__runicBridge.call("__runicOperationCancel", ${JSON.stringify(identity)})))()`);
  if (cancel.kind !== "not-running") throw new Error(`Terminal operation accepted cancellation: ${JSON.stringify(cancel)}`);
  const invalid = await evaluate('(async () => JSON.parse(await window.__runicBridge.call("__runicOperationStatus", "invalid-json")))()');
  if (invalid.kind !== "invalid-request") throw new Error(`Malformed operation identity was accepted: ${JSON.stringify(invalid)}`);
  await evaluate("window.__runicDiscardPromptCount = 0; window.confirm = () => { window.__runicDiscardPromptCount++; return false; }");
  await click("[data-discard]");
  await retry(async () => await query('document.querySelector("#document-pane [data-message]")?.textContent') === "Kept current changes.");
  if (await query("window.__runicDiscardPromptCount") !== 1)
    throw new Error("The declined discard bypassed the mounted browser interaction handler.");
  if ((await snapshot(editorRoute)).state?.body !== "Both Views see this text.")
    throw new Error("A declined discard changed the shared note.");
  await evaluate("window.confirm = () => { window.__runicDiscardPromptCount++; return true; }");
  await click("[data-discard]");
  await retry(async () => (await snapshot(editorRoute)).state?.body === "");
  await retry(async () => await query('document.querySelector("#document-pane [data-message]")?.textContent') === "Discarded Operation roundtrip");
  if (await query("window.__runicDiscardPromptCount") !== 2)
    throw new Error("The approved discard did not reach the mounted browser interaction handler exactly once.");

  // WebUI never settles calls in flight when its WebSocket closes. A call the
  // admission timeout released is late; a reconnect must forget it, or its
  // missing admission swallows the admission of a call sent afterwards.
  const admissions = () => query("window.__runicBridgeAdmissions()");
  await evaluate(`(() => {
    const webui = window.webui;
    const call = webui.call;
    webui.call = function (name, ...args) {
      if (name === "__runicLostProbe") return new Promise(() => {});
      return call.call(this, name, ...args);
    };
    void window.__runicBridge.call("__runicLostProbe");
    return true;
  })()`);
  await retry(async () => (await admissions()).late === 1);
  const { result: prototype } = await command("Runtime.evaluate", { expression: "WebSocket.prototype" });
  const { objects } = await command("Runtime.queryObjects", { prototypeObjectId: prototype.objectId });
  const closed = await command("Runtime.callFunctionOn", {
    objectId: objects.objectId, returnByValue: true,
    functionDeclaration: "function () { let count = 0; for (const socket of this) if (socket.readyState === WebSocket.OPEN) { socket.close(); count++; } return count; }",
  });
  if (closed.result.value !== 1) throw new Error(`Expected one open WebUI WebSocket, closed ${closed.result.value}.`);
  await retry(async () => await query("window.webui.isConnected()") === false);
  await retry(async () => await query("window.webui.isConnected()") === true);
  // Generated clients resume with their own calls; wait until .NET admitted them.
  await retry(async () => !(await admissions()).waiting);
  const afterLoss = await admissions();
  if (afterLoss.late !== 0)
    throw new Error(`A reconnect kept admissions from the lost connection: ${JSON.stringify(afterLoss)}`);
  await retry(async () => (await snapshot("shell")).state?.main.kind === "document");
  await expectClickEventsSuppressed("after reconnecting");
  console.log("REACTIVE_NOTES_BROWSER_OK|view-collection|polymorphic-dispatch|stable-reorder|pruned-route|restored-route|nested-routing|view-contract|shared-state|command|shared-activation|route-deactivation|reload-lease|operation-wire|interaction-fallback-and-confirmation|reconnect-admissions|click-events-suppressed");
  }
} finally {
  try { await browser?.close(); }
  finally {
    if (!host.stdin.writableEnded) host.stdin.end("\n");
    await pause(300);
    if (host.exitCode === null) host.kill("SIGTERM");
  }
}
