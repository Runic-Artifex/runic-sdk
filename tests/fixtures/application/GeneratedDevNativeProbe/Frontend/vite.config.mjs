import { readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const pluginPath = process.env.RUNIC_PROBE_VITE_PLUGIN;
const originPath = process.env.RUNIC_PROBE_VITE_ORIGIN_PATH;
const startupPath = process.env.RUNIC_PROBE_STARTUP_PATH;
const ackPath = process.env.RUNIC_PROBE_HANDOFF_ACK_PATH;
const readyManifest = process.env.RUNIC_VIEW_BRIDGE_READY_MANIFEST;
const hostReady = process.env.RUNIC_VIEW_BRIDGE_HOST_READY;
if (!pluginPath || !originPath || !startupPath || !ackPath || !readyManifest || !hostReady)
  throw new Error("The Runic development probe paths are required.");
const { runic } = await import(pathToFileURL(pluginPath).href);

function validateNativeUrl(value) {
  let url;
  try { url = new URL(value); } catch { return undefined; }
  if (url.protocol !== "http:" || url.hostname !== "127.0.0.1" || !Number.isInteger(Number(url.port)) ||
      Number(url.port) < 1 || Number(url.port) > 65535 || url.username !== "" || url.password !== "" ||
      url.pathname !== "/" || url.search !== "" || url.hash !== "") return undefined;
  return url.href;
}

function nativeDocumentHandoff() {
  let currentDescriptor;
  const allowedNativeOrigins = new Set();
  let handoffEventId = 0;
  let timer;
  return {
    name: "runic-generated-native-document-handoff",
    configureServer(viteServer) {
      viteServer.watcher.add(dirname(startupPath));
      const readVerifiedDescriptor = async () => {
        let startup, manifest, acknowledged;
        try {
          [startup, manifest, acknowledged] = await Promise.all([
            readFile(startupPath, "utf8").then(JSON.parse),
            readFile(readyManifest, "utf8").then(JSON.parse),
            readFile(hostReady, "utf8"),
          ]);
        } catch { return; }
        const nativeUrl = validateNativeUrl(startup?.nativeUrl);
        if (typeof manifest?.fingerprint !== "string" || startup?.fingerprint !== manifest.fingerprint ||
            acknowledged.trim() !== manifest.fingerprint || !Number.isSafeInteger(startup?.processId) || startup.processId < 1 ||
            typeof startup?.instanceId !== "string" || !/^[a-f0-9]{32}$/i.test(startup.instanceId) || !nativeUrl) return;
        return { processId: startup.processId, instanceId: startup.instanceId, fingerprint: manifest.fingerprint, url: nativeUrl };
      };
      const reconcile = async () => {
        const descriptor = await readVerifiedDescriptor();
        if (!descriptor) return;
        if (currentDescriptor === undefined) {
          currentDescriptor = descriptor;
          allowedNativeOrigins.add(new URL(descriptor.url).origin);
          return;
        }
        if (descriptor.instanceId === currentDescriptor.instanceId) return;
        currentDescriptor = descriptor;
        allowedNativeOrigins.add(new URL(descriptor.url).origin);
        // This private fixture event carries only the already-validated
        // replacement identity. The document still fetches and acknowledges
        // the descriptor before it changes location.
        viteServer.ws.send({
          type: "custom",
          event: "runic:generated-native-handoff-ready",
          data: { instanceId: descriptor.instanceId, fingerprint: descriptor.fingerprint, eventId: ++handoffEventId },
        });
      };
      viteServer.middlewares.use(async (request, response, next) => {
        const requestUrl = new URL(request.url ?? "/", "http://127.0.0.1");
        if (requestUrl.pathname !== "/__runic_native_handoff_snapshot" && requestUrl.pathname !== "/__runic_native_handoff_ack") return next();
        if (request.method !== "GET") { response.statusCode = 405; response.end(); return; }
        const descriptor = await readVerifiedDescriptor();
        const origin = request.headers.origin;
        if (!descriptor || typeof origin !== "string" || (!allowedNativeOrigins.has(origin) && origin !== new URL(descriptor.url).origin)) {
          response.statusCode = descriptor ? 403 : 503; response.setHeader("Cache-Control", "no-store"); response.end(); return;
        }
        allowedNativeOrigins.add(new URL(descriptor.url).origin);
        response.setHeader("Access-Control-Allow-Origin", origin); response.setHeader("Vary", "Origin"); response.setHeader("Cache-Control", "no-store");
        if (requestUrl.pathname === "/__runic_native_handoff_ack") {
          const attempts = Number(requestUrl.searchParams.get("attempts"));
          const eventId = Number(requestUrl.searchParams.get("eventId"));
          if (requestUrl.searchParams.get("instanceId") !== descriptor.instanceId || requestUrl.searchParams.get("fingerprint") !== descriptor.fingerprint ||
              !Number.isSafeInteger(attempts) || attempts < 1 || !Number.isSafeInteger(eventId) || eventId !== handoffEventId) {
            response.statusCode = 409; response.end(); return;
          }
          await writeFile(ackPath, JSON.stringify({ ...descriptor, attempts, eventId }) + "\n"); response.statusCode = 204; response.end(); return;
        }
        response.setHeader("Content-Type", "application/json; charset=utf-8"); response.end(JSON.stringify(descriptor));
      });
      const onDescriptorInputChange = file => {
        if (![startupPath, readyManifest, hostReady].some(path => resolve(file) === resolve(path))) return;
        clearTimeout(timer); timer = setTimeout(() => { void reconcile(); }, 25);
      };
      viteServer.watcher.on("add", onDescriptorInputChange).on("change", onDescriptorInputChange);
      viteServer.watcher.add([dirname(startupPath), dirname(readyManifest), dirname(hostReady)]);
      // Establish the first fully validated descriptor as baseline even when
      // Vite began after its files already existed and no initial add fires.
      void reconcile();
    },
  };
}

export default {
  plugins: [
    runic({ devtools: false }),
    nativeDocumentHandoff(),
    { name: "runic-generated-dev-native-probe-origin", configureServer(server) {
      server.httpServer?.once("listening", () => {
        const address = server.httpServer?.address();
        if (typeof address === "object" && address) void writeFile(originPath, `http://127.0.0.1:${address.port}/\n`);
      });
    } },
  ],
};
