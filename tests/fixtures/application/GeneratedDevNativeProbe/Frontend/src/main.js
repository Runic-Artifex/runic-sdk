import { revision } from "./value.js";

const viteClientUrl = document.querySelector('script[type="module"][src$="/@vite/client"]')?.src;
if (!viteClientUrl) throw new Error("The private document did not load the Vite client URL.");
const snapshotUrl = new URL("/__runic_native_handoff_snapshot", viteClientUrl);
const validDescriptor = descriptor => {
  if (!descriptor || !Number.isSafeInteger(descriptor.processId) || descriptor.processId < 1 ||
      typeof descriptor.instanceId !== "string" || !/^[a-f0-9]{32}$/i.test(descriptor.instanceId) ||
      typeof descriptor.fingerprint !== "string" || !/^[a-f0-9]{64}$/i.test(descriptor.fingerprint)) return false;
  try {
    const url = new URL(descriptor.url);
    return url.protocol === "http:" && url.hostname === "127.0.0.1" && Number(url.port) > 0 &&
      url.pathname === "/" && url.search === "" && url.hash === "" && url.username === "" && url.password === "";
  } catch { return false; }
};
const readDescriptor = async () => {
  const response = await fetch(snapshotUrl, { cache: "no-store" });
  if (!response.ok) return undefined;
  const text = await response.text();
  try { const descriptor = JSON.parse(text); return validDescriptor(descriptor) ? descriptor : undefined; }
  catch { throw new Error("The native host snapshot response was not JSON."); }
};
const initialDescriptor = await (async () => {
  for (let attempt = 0; attempt < 300; attempt += 1) {
    try { const descriptor = await readDescriptor(); if (descriptor) return descriptor; } catch { /* Startup races are expected. */ }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error("The native host did not publish a validated startup snapshot.");
})();
globalThis.__nativeHandoffBaseline = initialDescriptor;
globalThis.__nativeSnapshotAttempts = 0;
let handoffInFlight = false;

const handoffAfterValidatedSignal = async expected => {
  if (handoffInFlight || !expected || typeof expected.instanceId !== "string" || typeof expected.fingerprint !== "string" ||
      !Number.isSafeInteger(expected.eventId) || expected.eventId < 1) return;
  handoffInFlight = true;
  for (let attempt = 0; attempt < 300; attempt += 1) {
    globalThis.__nativeSnapshotAttempts += 1;
    try {
      const descriptor = await readDescriptor();
      if (descriptor && descriptor.instanceId === expected.instanceId && descriptor.fingerprint === expected.fingerprint &&
          descriptor.instanceId !== initialDescriptor.instanceId && descriptor.fingerprint !== initialDescriptor.fingerprint) {
        const acknowledgement = new URL("/__runic_native_handoff_ack", viteClientUrl);
        acknowledgement.searchParams.set("instanceId", descriptor.instanceId);
        acknowledgement.searchParams.set("fingerprint", descriptor.fingerprint);
        acknowledgement.searchParams.set("attempts", String(globalThis.__nativeSnapshotAttempts));
        acknowledgement.searchParams.set("eventId", String(expected.eventId));
        if (!(await fetch(acknowledgement, { cache: "no-store" })).ok) continue;
        globalThis.__nativeHandoffSnapshot = descriptor;
        location.replace(new URL("index.html", descriptor.url).href);
        return;
      }
    } catch { /* Retry until the replacement has acknowledged the new contract. */ }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  handoffInFlight = false;
  globalThis.__nativeHandoffSnapshotTimedOut = true;
};
import.meta.hot?.on("runic:generated-native-handoff-ready", descriptor => {
  void handoffAfterValidatedSignal(descriptor);
});

sessionStorage.setItem("loads", String(Number(sessionStorage.getItem("loads") ?? 0) + 1));
globalThis.__loads = Number(sessionStorage.getItem("loads"));
globalThis.__hmrUpdates = 0;
document.querySelector("#value").textContent = revision;
import.meta.hot?.accept("./value.js", module => {
  globalThis.__hmrUpdates += 1;
  document.querySelector("#value").textContent = module.revision;
});
