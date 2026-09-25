import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { stripTypeScriptTypes } from "node:module";

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function retry(action, milliseconds = 15_000) {
  const deadline = Date.now() + milliseconds;
  while (Date.now() < deadline) {
    try { const value = await action(); if (value) return value; } catch { /* Server or new page context is starting. */ }
    await delay(40);
  }
  throw new Error("Timed out waiting for the Window Bridge ordinary client fixture.");
}

const dll = fileURLToPath(new URL("./bin/Release/net10.0/Runic.Application.CsWebUi.Tests.dll", import.meta.url));
const host = spawn("dotnet", [dll, "--window-bridge-browser-host"], { stdio: ["pipe", "pipe", "pipe"] });
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let chrome, socket, profile;
let browserSucceeded = false;
try {
  const url = await retry(() => {
    if (host.exitCode !== null) throw new Error(`Fixture exited: ${errors}\n${output}`);
    return output.match(/WINDOW_BRIDGE_BROWSER_URL=(https?:\/\/\S+)/)?.[1];
  });
  profile = await mkdtemp(join(tmpdir(), "runic-sdk-window-bridge-shared-client-"));
  chrome = spawn(process.env.WEBUI_BROWSER_PATH ?? "chromium", [
    "--headless", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage",
    "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
    `--user-data-dir=${profile}`, url,
  ], { stdio: "ignore" });
  const port = await retry(async () => Number((await readFile(join(profile, "DevToolsActivePort"), "utf8")).split("\n")[0]));
  const target = await retry(async () => {
    const entries = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    return entries.find(entry => entry.type === "page" && entry.url.startsWith(url));
  });
  socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  let nextId = 0;
  const pending = new Map();
  socket.addEventListener("message", ({ data }) => {
    const message = JSON.parse(data);
    const finish = pending.get(message.id);
    if (finish) { pending.delete(message.id); finish(message); }
  });
  async function command(method, params = {}) {
    const id = ++nextId;
    const result = new Promise(resolve => pending.set(id, resolve));
    socket.send(JSON.stringify({ id, method, params }));
    return result;
  }
  async function evaluate(expression) {
    const response = await command("Runtime.evaluate", {
      expression, awaitPromise: true, returnByValue: true,
    });
    if (response.error || response.result?.exceptionDetails) throw new Error(JSON.stringify(response));
    return response.result.result.value;
  }
  await retry(() => evaluate("globalThis.webui?.isConnected() === true && !!globalThis.runicCsWebUi?.endpoints?.['notes.editor.mount']"));
  const documentEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  const clientSource = await readFile(new URL("./window-bridge-notes-client.ts", import.meta.url), "utf8");
  const clientJavaScript = stripTypeScriptTypes(clientSource);
  const clientUrl = `data:text/javascript;base64,${Buffer.from(clientJavaScript).toString("base64")}`;
  await evaluate(`(async () => {
    const { connectNotes } = await import(${JSON.stringify(clientUrl)});
    globalThis.notes = await connectNotes();
    return true;
  })()`);
  const initialEpoch = await evaluate("globalThis.runicCsWebUi.documentEpoch");
  if (documentEpoch !== initialEpoch) throw new Error("The ordinary client crossed a document reload.");
  const rawCall = (route, payload = {}) => evaluate(`(async () => {
    const descriptor = globalThis.runicCsWebUi.endpoints[${JSON.stringify(route)}];
    return JSON.parse(await globalThis.webui.call("__runicBridgeDispatch", globalThis.runicCsWebUi.credential,
      JSON.stringify({ v: 1, endpoint: descriptor.endpoint, generation: descriptor.generation,
        payload: ${JSON.stringify({ ...payload, documentEpoch })} })));
  })()`);

  const ids = await evaluate(`(async () => {
    const first = globalThis.notes.editor();
    const peer = globalThis.notes.editor();
    globalThis.owners = { first, peer };
    await first.mount();
    await peer.mount();
    return [first.presentationId, peer.presentationId];
  })()`);
  if (new Set(ids).size !== 2) throw new Error(`The two Editor consumers reused a presentation ID: ${JSON.stringify(ids)}`);
  const titleWork = await evaluate(`(async () => {
    const initial = await globalThis.owners.first.title();
    const direct = await globalThis.owners.first.setTitle("From ordinary TypeScript", "ordinary-direct");
    const conflict = await globalThis.owners.peer.writeTitle("Stale", initial, "ordinary-stale");
    const rebased = await globalThis.owners.peer.writeTitle("Shared title", conflict.current, "ordinary-rebased");
    return { initial, direct, conflict, rebased };
  })()`);
  if (titleWork.initial.value !== "Draft" || titleWork.initial.version !== 0
      || titleWork.direct.kind !== "applied" || titleWork.direct.current.version !== 1
      || titleWork.conflict.kind !== "conflict" || titleWork.conflict.current.value !== "From ordinary TypeScript"
      || titleWork.rebased.kind !== "applied" || titleWork.rebased.current.value !== "Shared title"
      || titleWork.rebased.current.version !== 2)
    throw new Error(`The ordinary title methods lost their typed receipts: ${JSON.stringify(titleWork)}`);
  // The adapter emits only a data-free refresh hint. The mounted owner uses
  // its ordinary typed route to pull state after receiving it.
  await evaluate(`(() => {
    globalThis.refreshHints = [];
    globalThis.__runicBridgePublish = hint => {
      globalThis.refreshHints.push(hint);
      globalThis.refreshPull = globalThis.owners.peer.title();
    };
  })()`);
  const refreshTrigger = await rawCall("notes.debug.refresh", { presentationId: ids[1] });
  if (refreshTrigger.ok !== true) throw new Error(`A mounted Editor could not request a refresh hint: ${JSON.stringify(refreshTrigger)}`);
  const refreshPull = await evaluate("globalThis.refreshPull");
  const refreshHint = await evaluate("globalThis.refreshHints[0]");
  if (refreshPull.value !== "Shared title" || refreshPull.version !== 2
      || refreshHint?.payload?.protocol !== "runic.window-bridge.refresh"
      || refreshHint?.payload?.version !== 1 || refreshHint?.payload?.revision !== 1
      || Object.keys(refreshHint.payload).length !== 3)
    throw new Error(`A refresh hint did not lead to an authorized typed pull: ${JSON.stringify({ refreshHint, refreshPull })}`);
  await evaluate("globalThis.owners.first.release()");
  const peerTitle = await evaluate("globalThis.owners.peer.title()");
  if (peerTitle.value !== "Shared title" || peerTitle.version !== 2)
    throw new Error(`Releasing the first Editor removed its peer: ${JSON.stringify(peerTitle)}`);
  const staleFirstGet = await rawCall("notes.title.get", { presentationId: ids[0] });
  const staleFirstSet = await rawCall("notes.title.set", {
    presentationId: ids[0], requestId: "released-first-set", value: "Must not apply",
  });
  const staleFirstChecked = await rawCall("notes.title.writeChecked", {
    presentationId: ids[0], requestId: "released-first-checked", value: "Must not apply", expected: peerTitle,
  });
  const staleFirstSave = await rawCall("notes.save.start", {
    presentationId: ids[0], requestId: "released-first-save",
  });
  if (staleFirstGet.ok !== false || staleFirstGet.kind !== "rejected"
      || staleFirstSet.ok !== false || staleFirstSet.kind !== "rejected"
      || staleFirstChecked.ok !== false || staleFirstChecked.kind !== "rejected"
      || staleFirstSave.accepted !== false || staleFirstSave.kind !== "rejected")
    throw new Error(`A released Editor callback reached its peer's model: ${JSON.stringify({ staleFirstGet, staleFirstSet, staleFirstChecked, staleFirstSave })}`);
  const staleRefresh = await rawCall("notes.debug.refresh", { presentationId: ids[0] });
  await delay(50);
  const hintCountAfterStaleRefresh = await evaluate("globalThis.refreshHints.length");
  if (staleRefresh.ok !== false || staleRefresh.kind !== "rejected" || hintCountAfterStaleRefresh !== 1)
    throw new Error(`A released Editor caused a refresh hint or typed pull: ${JSON.stringify({ staleRefresh, hintCountAfterStaleRefresh })}`);
  const peerAfterStale = await evaluate("globalThis.owners.peer.title()");
  if (peerAfterStale.value !== "Shared title" || peerAfterStale.version !== 2)
    throw new Error(`A stale Editor callback changed the peer's title: ${JSON.stringify(peerAfterStale)}`);

  // Cleanup while the callback is pending must await and release that mount.
  await evaluate(`(async () => {
    const racing = globalThis.notes.editor();
    globalThis.owners.racing = racing;
    const mount = racing.mount();
    const cleanup = racing.release();
    await Promise.all([mount, cleanup]);
    return true;
  })()`);
  await evaluate("globalThis.owners.peer.release()");
  const afterRace = await rawCall("notes.title.get", { presentationId: ids[1] });
  if (afterRace.ok !== false || afterRace.kind !== "rejected")
    throw new Error(`A mount completing after teardown leaked its presentation: ${JSON.stringify(afterRace)}`);

  // Model an HMR component replacement: mount the new owner before the old
  // one's teardown. A late duplicate cleanup must never release the new one.
  const hotIds = await evaluate(`(async () => {
    const old = globalThis.notes.editor();
    const replacement = globalThis.notes.editor();
    globalThis.owners.old = old;
    globalThis.owners.replacement = replacement;
    await old.mount();
    await replacement.mount();
    return [old.presentationId, replacement.presentationId];
  })()`);
  if (new Set([...ids, ...hotIds]).size !== 4)
    throw new Error(`Overlapping Editor consumers reused a presentation ID: ${JSON.stringify([...ids, ...hotIds])}`);
  await evaluate("globalThis.owners.old.release()");

  const oldId = hotIds[0];
  const lateCleanup = await rawCall("notes.editor.unmount", { presentationId: oldId });
  if (lateCleanup.ok !== false || lateCleanup.kind !== "rejected")
    throw new Error(`A duplicate stale cleanup was accepted: ${JSON.stringify(lateCleanup)}`);
  const lateOldGet = await rawCall("notes.title.get", { presentationId: oldId });
  const lateOldSave = await rawCall("notes.save.start", { presentationId: oldId, requestId: "late-old-save" });
  if (lateOldGet.ok !== false || lateOldGet.kind !== "rejected"
      || lateOldSave.accepted !== false || lateOldSave.kind !== "rejected")
    throw new Error(`The replaced Editor's late callback reached the mounted replacement: ${JSON.stringify({ lateOldGet, lateOldSave })}`);

  const replacementTitle = await evaluate("globalThis.owners.replacement.title()");
  if (replacementTitle.value !== "Shared title" || replacementTitle.version !== 2)
    throw new Error(`Old owner cleanup removed the replacement: ${JSON.stringify(replacementTitle)}`);
  await evaluate("globalThis.pendingOrdinarySave = globalThis.owners.replacement.save('ordinary-save'); true");
  const heldSave = await evaluate("Promise.race([globalThis.pendingOrdinarySave.then(() => 'completed'), new Promise(resolve => setTimeout(() => resolve('held'), 50))])");
  if (heldSave !== "held") throw new Error("Ordinary Save resolved before the accepted operation completed.");
  const runningSave = await evaluate("globalThis.notes.saveStatus('ordinary-save')");
  if (runningSave.kind !== "running")
    throw new Error(`The awaited Save did not expose its admitted operation: ${JSON.stringify(runningSave)}`);
  const releasedSave = await rawCall("notes.save.release");
  if (!releasedSave.ok) throw new Error(`The fixture did not release the held Save: ${JSON.stringify(releasedSave)}`);
  await evaluate("globalThis.pendingOrdinarySave");
  const finishedSave = await retry(async () => {
    const status = await evaluate("globalThis.notes.saveStatus('ordinary-save')");
    return status.kind === "succeeded" ? status : null;
  });
  if (finishedSave.requestId !== "ordinary-save" || finishedSave.error !== null)
    throw new Error(`The ordinary Save status was not terminal: ${JSON.stringify(finishedSave)}`);
  const duplicateSave = await evaluate("globalThis.owners.replacement.startSave('ordinary-save')");
  if (duplicateSave.accepted || duplicateSave.kind !== "succeeded" || duplicateSave.requestId !== "ordinary-save")
    throw new Error(`A duplicate Save request was replayed or decoded incorrectly: ${JSON.stringify(duplicateSave)}`);
  await evaluate("globalThis.notes.navigateToPreview()");
  const titleRetired = await evaluate("globalThis.runicCsWebUi.endpoints['notes.title.get'] === undefined");
  if (!titleRetired) throw new Error("Preview navigation retained the Editor title endpoint.");
  await evaluate("globalThis.owners.replacement.release()");
  browserSucceeded = true;
} finally {
  socket?.close();
  chrome?.kill("SIGTERM");
  host.stdin.end("\n");
  if (browserSucceeded) await retry(() => output.includes("WINDOW_BRIDGE_BROWSER_STOPPED"), 5_000);
  await delay(100);
  if (host.exitCode === null) host.kill("SIGTERM");
  if (profile) await rm(profile, { recursive: true, force: true });
}
console.log("SDK_WINDOW_BRIDGE_ORDINARY_CLIENT_OK|one-document|two-consumers|typed-snapshot|setter-receipts|refresh-hint-authorized-pull|stale-refresh-rejected|awaited-save|duplicate-admission-decoded|independent-release|exact-presentation-callback-gate|late-mount-drained|overlap-replacement|stale-cleanup-rejected|navigation-before-unmount|scope-drained");
