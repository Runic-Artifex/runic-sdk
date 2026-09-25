import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

// This is deliberately test infrastructure. It describes the handoff that a
// future private host adapter must implement without expanding Runic Vite's
// published option surface before that adapter exists.
export function viewBridgeHandoffTestPlugin({ readyManifest, hostReady }) {
  const readyPath = resolve(readyManifest);
  const hostPath = resolve(hostReady);
  let lastFingerprint;
  let timer;
  let generation = 0;

  const reloadWhenHostIsReady = async (server) => {
    const fingerprint = await readFingerprint(readyPath);
    if (fingerprint === undefined || fingerprint === lastFingerprint) return;
    const currentGeneration = ++generation;
    if (!await waitForMatchingHost(hostPath, fingerprint) || currentGeneration !== generation) return;
    lastFingerprint = fingerprint;
    server.ws.send({ type: "full-reload" });
  };

  return {
    name: "runic-view-bridge-handoff-test",
    async configureServer(server) {
      lastFingerprint = await readFingerprint(readyPath);
      server.watcher.add(readyPath);
      const onReadyChange = (file) => {
        if (resolve(file) !== readyPath) return;
        clearTimeout(timer);
        timer = setTimeout(() => { void reloadWhenHostIsReady(server); }, 50);
      };
      server.watcher.on("add", onReadyChange).on("change", onReadyChange);
      server.httpServer?.once("close", () => {
        clearTimeout(timer);
        server.watcher.off("add", onReadyChange).off("change", onReadyChange);
      });
    },
  };
}

async function readFingerprint(path) {
  try {
    const document = JSON.parse(await readFile(path, "utf8"));
    const fingerprint = document?.fingerprint;
    return typeof fingerprint === "string" && fingerprint.trim() === fingerprint &&
      fingerprint.length > 0 && fingerprint.length <= 128 ? fingerprint : undefined;
  } catch {
    return undefined;
  }
}

async function waitForMatchingHost(path, fingerprint) {
  for (let attempt = 0; attempt < 400; attempt += 1) {
    try {
      if ((await readFile(path, "utf8")).trim() === fingerprint) return true;
    } catch { /* The managed host has not acknowledged the new document yet. */ }
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  return false;
}
