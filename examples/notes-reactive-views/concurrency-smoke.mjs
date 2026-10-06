import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchChromium, pause, tail, waitFor } from "../shared/smoke.mjs";

const clients = [];
const detail = async () => [
  ...await Promise.all(clients.map(async (client, index) => `client ${index + 1}: ${await client.diagnostics()}`)),
  `host stdout:\n${tail(output)}\nhost stderr:\n${tail(errors)}`,
].join("\n");
const retry = (condition, timeout = 15_000) => waitFor(condition, { timeout, label: "concurrent Reactive Notes clients", detail });

async function openClient(url) {
  const browser = await launchChromium(url, { profilePrefix: "runic-reactive-client-", timeout: 15_000 });
  clients.push(browser);
  const { evaluate } = browser;
  return {
    evaluate,
    snapshot: route => evaluate(`(async () => JSON.parse(await window.__runicBridge.call(${JSON.stringify(route + "Snapshot")})))()`),
    call: route => evaluate(`window.__runicBridge.call(${JSON.stringify(route)})`),
    kill: () => browser.kill(),
    dispose: () => browser.close(),
  };
}

const dll = fileURLToPath(new URL("./bin/Release/net10.0/NotesReactiveViews.dll", import.meta.url));
const webRoot = process.env.RUNIC_WEB_ROOT;
const host = spawn("dotnet", [dll, "--serve-only", "--verify-multi-client", "--multi-client",
  ...(webRoot ? ["--web-root", webRoot] : [])], {
  stdio: ["pipe", "pipe", "pipe"],
});
let output = "", errors = "";
host.stdout.on("data", chunk => { output += chunk.toString(); });
host.stderr.on("data", chunk => { errors += chunk.toString(); });
let first, second;
try {
  let url;
  await retry(() => {
    if (host.exitCode !== null) throw new Error(`Host exited: ${errors}`);
    url = output.match(/https?:\/\/[^\s]+/)?.[0];
    return url;
  });
  first = await openClient(url);
  await retry(async () => await first.evaluate('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  second = await openClient(url);
  await retry(async () => await second.evaluate('document.querySelector("#main h1")?.textContent') === "Reactive Notes");
  const pinnedBefore = (await first.snapshot("shell")).state.pinned;
  if (pinnedBefore.length !== 2 || (await second.snapshot("shell")).state.pinned[0]?.id !== pinnedBefore[0].id)
    throw new Error("The clients did not share the same routed View collection.");
  await first.call("shellSwapPinned");
  await retry(async () => (await second.snapshot("shell")).state.pinned[0]?.id === pinnedBefore[1].id);
  await first.call("shellRemovePinned");
  await retry(async () => (await second.snapshot(`content${pinnedBefore[0].id}`)).error?.kind === "disconnected");
  await first.call("shellRestorePinned");
  await retry(async () => (await second.snapshot("shell")).state.pinned[0]?.id === pinnedBefore[0].id);

  await first.evaluate('document.querySelector("[data-go=document]").click()');
  for (const client of [first, second]) {
    await retry(async () => await client.evaluate('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
    await retry(async () => await client.evaluate('document.querySelector("#compact-pane h2")?.textContent') === "Compact View");
  }
  const documentId = (await first.snapshot("shell")).state.main.id;
  const editorId = (await first.snapshot(`content${documentId}`)).state.currentPane.id;
  const editorRoute = `content${editorId}`;
  await retry(async () => (await first.snapshot(editorRoute)).state?.activationCount === 1);

  // Both clients command the same routers. Settle on Editor after overlapping
  // calls and require both component trees to recover from transient routes.
  await Promise.all([first.call("shellOpenHome"), second.call("shellOpenDocument")]);
  await second.call("shellOpenDocument");
  for (const client of [first, second])
    await retry(async () => await client.evaluate('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
  await Promise.all([
    first.call(`content${documentId}ShowPreview`),
    second.call(`content${documentId}ShowEditor`),
  ]);
  await second.call(`content${documentId}ShowEditor`);
  for (const client of [first, second])
    await retry(async () => await client.evaluate('document.querySelector("#document-pane h2")?.textContent') === "Full editor");
  const settled = (await second.snapshot(editorRoute)).state;
  if (settled.activationCount - settled.deactivationCount !== 1)
    throw new Error(`Overlapping routes left an unbalanced activation: ${JSON.stringify(settled)}`);

  first.kill();
  await pause(350);
  host.stdin.write("\n");
  await retry(() => output.includes("CLIENT_STILL_ACTIVE"));
  const afterFirstExit = (await second.snapshot(editorRoute)).state;
  if (afterFirstExit.activationCount - afterFirstExit.deactivationCount !== 1)
    throw new Error(`The surviving browser lost its activation: ${JSON.stringify(afterFirstExit)}`);
  await second.evaluate('(() => { const field = document.querySelector("#document-pane [data-title]"); field.value = "Surviving client"; field.dispatchEvent(new Event("change", { bubbles: true })); return true; })()');
  await retry(async () => await second.evaluate('document.querySelector("#compact-pane [data-title]")?.textContent') === "Surviving client");
  second.kill();
  host.stdin.end("\n");
  await retry(() => output.includes("CLIENTS_RELEASED"));
  await retry(() => host.exitCode !== null);
  if (host.exitCode !== 0)
    throw new Error(`The final browser exit did not release the View: ${output}\n${errors}`);
  console.log("REACTIVE_NOTES_CONCURRENCY_OK|two-clients|collection-reorder-prune-restore|overlapping-routes|survivor|final-disconnect");
} finally {
  await Promise.allSettled([first?.dispose(), second?.dispose()]);
  if (!host.stdin.writableEnded) host.stdin.end("\n");
  await pause(300);
  if (host.exitCode === null) host.kill("SIGTERM");
}
