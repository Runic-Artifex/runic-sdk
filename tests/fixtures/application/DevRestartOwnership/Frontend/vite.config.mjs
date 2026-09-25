import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const pluginPath = process.env.RUNIC_PROBE_VITE_PLUGIN;
const originPath = process.env.RUNIC_PROBE_VITE_ORIGIN_PATH;
if (!pluginPath || !originPath) throw new Error("The Runic development probe paths are required.");
const { runic } = await import(pathToFileURL(pluginPath).href);

export default {
  plugins: [
    runic({ devtools: false }),
    fixtureHandoff(),
    {
      name: "runic-development-probe-origin",
      configureServer(server) {
        server.httpServer?.once("listening", () => {
          const address = server.httpServer?.address();
          if (typeof address === "object" && address)
            void writeFile(originPath, `http://127.0.0.1:${address.port}/\n`);
        });
      },
    },
  ],
};

function fixtureHandoff() {
  const readyManifest = process.env.RUNIC_VIEW_BRIDGE_READY_MANIFEST;
  const hostReady = process.env.RUNIC_VIEW_BRIDGE_HOST_READY;
  if (!readyManifest || !hostReady) throw new Error("The fixture handoff paths are required.");
  const readyPath = resolve(readyManifest);
  const hostPath = resolve(hostReady);
  let lastFingerprint;
  let timer;
  let generation = 0;
  return {
    name: "runic-development-probe-handoff",
    async configureServer(server) {
      lastFingerprint = await readFingerprint(readyPath);
      server.watcher.add(readyPath);
      const onReadyChange = (file) => {
        if (resolve(file) !== readyPath) return;
        clearTimeout(timer);
        timer = setTimeout(async () => {
          const fingerprint = await readFingerprint(readyPath);
          if (fingerprint === undefined || fingerprint === lastFingerprint) return;
          const currentGeneration = ++generation;
          if (!await waitForMatchingHost(hostPath, fingerprint) || currentGeneration !== generation) return;
          lastFingerprint = fingerprint;
          server.ws.send({ type: "full-reload" });
        }, 50);
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
    return typeof document?.fingerprint === "string" ? document.fingerprint : undefined;
  } catch { return undefined; }
}

async function waitForMatchingHost(path, fingerprint) {
  for (let attempt = 0; attempt < 400; attempt += 1) {
    try { if ((await readFile(path, "utf8")).trim() === fingerprint) return true; }
    catch { /* Await the managed host's acknowledgement. */ }
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  return false;
}
