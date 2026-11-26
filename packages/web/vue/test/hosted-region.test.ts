// @vitest-environment happy-dom
// Hosted regions (experimental): RunicHostedRegion renders <runic-hosted-region> with its fallback slot per state.
import { createApp, createSSRApp, h, nextTick, ref } from "vue";
import { renderToString } from "vue/server-renderer";
import { expect, test } from "vitest";
import { RunicHostedRegion } from "../dist/experimental.js";

// The element's shadow roots are closed: keep them, to load a region's frame and read what its slots show.
const shadows = new WeakMap<Element, ShadowRoot>();
const attachShadow = Element.prototype.attachShadow;
Element.prototype.attachShadow = function (this: Element, init: ShadowRootInit) {
  const shadow = attachShadow.call(this, init);
  shadows.set(this, shadow);
  return shadow;
};
// happy-dom loads no frame page (and logs that it does not): the frame's load is the test's.
const loadFrame = (region: Element) => shadows.get(region)!.querySelector("iframe")!.dispatchEvent(new Event("load"));
// The text of the content the element's slots show: the state's slot, else the slot nested in it as its fallback
// content, and so on (happy-dom's flattened assignment does not follow fallback content).
const shown = (region: Element) => {
  for (let slot = shadows.get(region)!.querySelector("slot"); slot; slot = slot.querySelector(":scope > slot")) {
    const assigned = slot.assignedElements();
    if (assigned.length > 0) return assigned.map(element => element.textContent).join("|");
  }
  return "";
};

// A producer with hosted-regions/1 and hosted-region-loading/1 behind the page's Bridge: it records requests, and the test
// pushes.
const listeners = new Set<(message: string) => void>();
const requests: Record<string, unknown>[] = [];
(globalThis as { __runicBridge?: unknown }).__runicBridge = {
  isConnected: () => true,
  async request(text: string) {
    const message = JSON.parse(text) as Record<string, unknown>;
    requests.push(message);
    return message["kind"] === "hello"
      ? JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-producer", version: "1.0.0" }, features: ["hosted-regions/1", "hosted-region-loading/1"] })
      : '{"ok":true}';
  },
  onMessage(listener: (message: string) => void) { listeners.add(listener); return () => listeners.delete(listener); },
  onConnectionChange(listener: (connected: boolean) => void) { listener(true); return () => {}; },
};
const push = (message: object) => { for (const listener of listeners) listener(JSON.stringify(message)); };
const until = async (condition: () => boolean) => {
  for (let attempt = 0; attempt < 100 && !condition(); attempt++) await new Promise(resolve => setTimeout(resolve, 10));
  expect(condition()).toBe(true);
};

test("renders the element, declares the region and renders the fallback slot of each state", async () => {
  const states: string[] = [];
  const container = document.body.appendChild(document.createElement("div"));
  const app = createApp({
    render: () => h(RunicHostedRegion, { name: "check", sizing: "content-height", label: "System check", class: "step", onStateChange: (state: string) => states.push(state) },
      { fallback: ({ state }: { state: string }) => h("p", `fallback ${state}`) }),
  });
  app.mount(container);
  const region = container.querySelector("runic-hosted-region")!;
  expect([region.getAttribute("name"), region.getAttribute("sizing"), region.getAttribute("label"), region.className]).toEqual(["check", "content-height", "System check", "step"]);
  await until(() => requests.some(request => request["kind"] === "hosted-region-declare" && request["sizing"] === "content-height"));
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback waiting");
  push({ kind: "hosted-region-empty", name: "check", reason: "ended" });
  await until(() => region.getAttribute("state") === "ended");
  await nextTick();
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback ended");
  expect(states).toEqual(["ended"]);
  app.unmount();
  await until(() => requests.some(request => request["kind"] === "hosted-region-remove"));
});

const frame = (name: string) => ({ kind: "hosted-region-content", name, embedding: "frame", src: `http://127.0.0.1:9/_runic_frame/${name}`, title: null, allow: "" });
type Status = { state: string; stalled: boolean; failureReason: string | null; retry: () => boolean };

test("renders the stalled slot of a stalled region, and its fallback slot for loading without it", async () => {
  const details: unknown[] = [];
  const exposed = ref<Status>();
  const fallback = ({ state, stalled }: Status) => h("p", `fallback ${state}${stalled ? " stalled" : ""}`);
  const container = document.body.appendChild(document.createElement("div"));
  const app = createApp({
    render: () => h("div", [
      h(RunicHostedRegion, { name: "stalled", ref: exposed, onStateChange: (_state: string, _previous: string, detail: unknown) => details.push(detail) },
        { fallback, stalled: () => h("p", "taking longer") }),
      h(RunicHostedRegion, { name: "loading" }, { fallback }),
    ]),
  });
  app.mount(container);
  const [withSlot, without] = [...container.querySelectorAll("runic-hosted-region")];
  await until(() => ["stalled", "loading"].every(name => requests.some(request => request["kind"] === "hosted-region-declare" && request["name"] === name)));
  push(frame("stalled"));
  push(frame("loading"));
  await until(() => withSlot!.getAttribute("state") === "loading" && without!.getAttribute("state") === "loading");
  await nextTick();
  expect([shown(withSlot!), shown(without!)]).toEqual(["fallback loading", "fallback loading"]);
  loadFrame(withSlot!);
  loadFrame(without!);
  await until(() => withSlot!.hasAttribute("stalled") && without!.hasAttribute("stalled"));
  await nextTick();
  expect(withSlot!.querySelector("[slot=stalled]")!.textContent).toBe("taking longer");
  expect(without!.querySelector("[slot=stalled]")).toBeNull();
  expect([shown(withSlot!), shown(without!)]).toEqual(["taking longer", "fallback loading stalled"]);
  expect([exposed.value!.state, exposed.value!.stalled]).toEqual(["loading", true]);
  expect(details.at(-1)).toEqual({ state: "loading", previous: "loading", stalled: true, reason: null });
  app.unmount();
});

test("renders the failed slot with its reason, retry asks the host, and a region without it shows its fallback slot", async () => {
  const exposed = ref<Status>();
  const container = document.body.appendChild(document.createElement("div"));
  const app = createApp({
    render: () => h("div", [
      h(RunicHostedRegion, { name: "failed", ref: exposed }, {
        fallback: () => h("p", "fallback"),
        failed: ({ failureReason, retry }: Status) => h("button", { type: "button", onClick: () => retry() }, `failed: ${failureReason}`),
      }),
      h(RunicHostedRegion, { name: "fallback" }, { fallback: ({ state, failureReason }: Status) => h("p", `fallback ${state} ${failureReason}`) }),
    ]),
  });
  app.mount(container);
  const [withSlot, without] = [...container.querySelectorAll("runic-hosted-region")];
  await until(() => ["failed", "fallback"].every(name => requests.some(request => request["kind"] === "hosted-region-declare" && request["name"] === name)));
  expect(exposed.value!.retry()).toBe(false);
  push({ kind: "hosted-region-empty", name: "failed", reason: "failed", failure: "unreachable" });
  push({ kind: "hosted-region-empty", name: "fallback", reason: "failed", failure: "timeout" });
  await until(() => withSlot!.getAttribute("state") === "failed" && without!.getAttribute("state") === "failed");
  await nextTick();
  expect(withSlot!.querySelector("[slot=failed]")!.textContent).toBe("failed: unreachable");
  expect(without!.querySelector("[slot=failed]")).toBeNull();
  expect([shown(withSlot!), shown(without!)]).toEqual(["failed: unreachable", "fallback failed timeout"]);
  expect(exposed.value!.failureReason).toBe("unreachable");
  withSlot!.querySelector("button")!.click();
  await until(() => requests.some(request => request["kind"] === "hosted-region-retry" && request["name"] === "failed"));
  expect(requests.find(request => request["kind"] === "hosted-region-retry")).not.toHaveProperty("src");
  expect(exposed.value!.retry()).toBe(true);
  app.unmount();
});

test("renders on the server", async () => {
  const html = await renderToString(createSSRApp({ render: () => h(RunicHostedRegion, { name: "check" }, { fallback: () => h("p", "Not running") }) }));
  expect(html.replace(/<!--\[-->|<!--\]-->/g, "")).toBe('<runic-hosted-region name="check"><div slot="fallback" style="display:contents;"><p>Not running</p></div></runic-hosted-region>');
});
