// @vitest-environment happy-dom
// Vue DevTools (#1041): the development build (dist/development/) adds a "Runic Views" custom inspector and a
// "Runic Bridge calls" timeline layer, fed by the development channels of @runic-artifex/views, when Vue DevTools is on
// the page. The production build (dist/) has none of it.
import { afterEach, describe, expect, test } from "vitest";
import { createApp, defineComponent, h, nextTick } from "vue";
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";

// The development build shares the production build's declarations (dist/*.d.ts).
const load = (entry: string): Promise<unknown> => import(new URL(`../dist/development/${entry}`, import.meta.url).href);
const devtools = await load("devtools.js") as typeof import("../dist/devtools.js");
const development = await load("index.js") as typeof import("../dist/index.js");

const host = globalThis as unknown as Record<string | symbol, unknown>;
const contract = "Demo.Notes.NotesViewModel:ABC";
type State = { readonly title: string };

// Vue DevTools' global hook, which Vue's development build reports to: a stand-in that ignores what it is told.
const installHook = () => { host["__VUE_DEVTOOLS_GLOBAL_HOOK__"] = { emit() {}, on() {}, once() {}, off() {} }; };
const tick = (ms = 20) => new Promise(resolve => setTimeout(resolve, ms));
/**
 * Waits until `check` holds (the plugin follows the channels at most every 100 ms). Bounded by the test's timeout, which
 * vitest.config.ts scales on slow runners.
 */
async function until(check: () => boolean | Promise<boolean>): Promise<void> {
  while (!await check()) await tick(10);
}

afterEach(() => {
  devtools.stopPageModel();
  delete host["__VUE_DEVTOOLS_GLOBAL_HOOK__"];
  delete host[Symbol.for("runic.views.inspector")];
  delete host[Symbol.for("runic.views.calls")];
});

async function notes() {
  const bridge = createMockBridge();
  installMockBridge(bridge);
  const view = bridge.view("notes", {
    state: { title: "Draft" }, contract,
    commands: { save: () => ({}), fail: () => { throw new Error("The save failed."); } },
  });
  return { view, connect: () => connectView<State>({ contract, route: "notes", members: { title: bridgeWire.string } }) };
}

type Recorded = { inspectors: unknown[]; layers: unknown[]; events: { layerId: string; event: Record<string, unknown> }[]; sent: string[] };

/** A stand-in for the plugin API of `@vue/devtools-api`, which records what the plugin does. */
function fakeApi() {
  const recorded: Recorded = { inspectors: [], layers: [], events: [], sent: [] };
  const handlers: { tree?: (payload: never) => void; state?: (payload: never) => void } = {};
  const api = {
    addInspector: (options: unknown) => { recorded.inspectors.push(options); },
    addTimelineLayer: (options: unknown) => { recorded.layers.push(options); },
    addTimelineEvent: (options: { layerId: string; event: Record<string, unknown> }) => { recorded.events.push(options); },
    sendInspectorTree: (id: string) => { recorded.sent.push(`tree ${id}`); },
    sendInspectorState: (id: string) => { recorded.sent.push(`state ${id}`); },
    now: () => 1000,
    on: {
      getInspectorTree: (handler: (payload: never) => void) => { handlers.tree = handler; },
      getInspectorState: (handler: (payload: never) => void) => { handlers.state = handler; },
    },
  };
  const tree = (filter = "") => {
    const payload = { inspectorId: "runic-views", filter, rootNodes: [] as unknown[] };
    handlers.tree!(payload as never);
    return payload.rootNodes;
  };
  const state = (nodeId: string) => {
    const payload = { inspectorId: "runic-views", nodeId, state: undefined as unknown };
    handlers.state!(payload as never);
    return payload.state as Record<string, { key: string; value: unknown }[]>;
  };
  const descriptors: Record<string, unknown>[] = [];
  const setupDevtoolsPlugin = (descriptor: Record<string, unknown>, setup: (value: typeof api) => void) => {
    descriptors.push(descriptor);
    setup(api);
  };
  return { recorded, tree, state, descriptors, load: async () => ({ setupDevtoolsPlugin }) };
}

describe("Vue DevTools", () => {
  test("the inspector lists the page's Views with their state and recent calls, and the timeline shows the calls", async () => {
    installHook();
    const fake = fakeApi();
    const app = createApp({ render: () => null });
    devtools.attachDevtools(app, fake.load as never);
    devtools.attachDevtools(app, fake.load as never);
    await until(() => fake.descriptors.length > 0);
    expect(fake.descriptors).toHaveLength(1);
    expect(fake.descriptors[0]).toMatchObject({ id: "dev.runic-artifex.views", label: "Runic Views", app });
    expect(fake.recorded.inspectors).toEqual([expect.objectContaining({ id: "runic-views", label: "Runic Views" })]);
    expect(fake.recorded.layers).toEqual([expect.objectContaining({ id: "runic-bridge-calls", label: "Runic Bridge calls" })]);

    const { connect } = await notes();
    const client = await connect();
    await client.command("save");
    await client.command("fail").catch(() => undefined);
    await until(() => fake.recorded.sent.includes("tree runic-views") && (fake.state("notes")["Recent calls"]?.length ?? 0) === 2);

    expect(fake.recorded.sent).toContain("tree runic-views");
    const [node] = fake.tree() as { id: string; label: string; tags: { label: string }[] }[];
    expect(node).toMatchObject({ id: "notes", label: "NotesViewModel" });
    expect(node!.tags.map(tag => tag.label)).toContain("notes");
    expect(fake.tree("nothing")).toEqual([]);
    const state = fake.state("notes");
    expect(state["View"]).toEqual(expect.arrayContaining([{ key: "route", value: "notes", editable: false },
      { key: "contract", value: contract, editable: false }]));
    expect(state["State"]).toEqual([{ key: "title", value: "Draft", editable: false }]);
    const calls = state["Recent calls"]!;
    expect(calls.map(call => call.key)).toEqual([expect.stringMatching(/^#\d+ command fail$/), expect.stringMatching(/^#\d+ command save$/)]);
    expect(calls[0]!.value).toMatchObject({ outcome: "error", error: expect.stringContaining("The save failed.") });
    expect(calls[1]!.value).toMatchObject({ outcome: "ok" });

    const events = fake.recorded.events.filter(event => event.layerId === "runic-bridge-calls").map(event => event.event);
    expect(events.map(event => `${event["title"]} ${event["subtitle"]}`)).toEqual([
      "command NotesViewModel.save start", "command NotesViewModel.save ok",
      "command NotesViewModel.fail start", "command NotesViewModel.fail error",
    ]);
    expect(events[1]!["groupId"]).toBe(events[0]!["groupId"]);
    expect(events[3]!["logType"]).toBe("error");

    client.dispose();
    await until(() => JSON.stringify(fake.tree()).includes('"closed"'));
    expect(fake.tree()).toEqual([expect.objectContaining({ id: "notes", tags: expect.arrayContaining([expect.objectContaining({ label: "closed" })]) })]);
  });

  test("without Vue DevTools on the page nothing loads and no channel is read", async () => {
    let loaded = 0;
    const app = createApp({ render: () => null });
    devtools.attachDevtools(app, (async () => { loaded++; return fakeApi().load(); }) as never);
    await tick();
    expect(loaded).toBe(0);
    expect(Symbol.for("runic.views.calls") in host).toBe(false);
  });

  test("useView of the development build registers the plugin with @vue/devtools-api, whose kit runs it", async () => {
    // The real `@vue/devtools-api` (an optional peer dependency). Under Node it resolves a no-op entry, so the test
    // loads its browser entry, and the kit (its dependency) next to it.
    const entry = import.meta.resolve("@vue/devtools-api").replace(/index-node\.js$/, "index.js");
    const api = await import(entry) as { setupDevtoolsPlugin: unknown };
    const kit = await import(new URL("../../devtools-kit/dist/index.js", entry).href) as {
      callDevToolsPluginSetupFn(plugin: [unknown, unknown], app: unknown): void;
      devtools: { ctx: { api: { getInspectorTree(payload: { inspectorId: string; filter: string }): Promise<unknown[]> } } };
      getInspector(id: string): unknown;
    };
    // The kit's global is `window` where there is one (happy-dom's differs from `globalThis`).
    const target = (typeof window === "undefined" ? host : window) as unknown as Record<string, unknown>;
    installHook();
    const hooks = target["__VUE_DEVTOOLS_HOOK"] as { hook(name: string, run: (...args: unknown[]) => void): () => void };
    const plugins: [Record<string, unknown>, unknown][] = [];
    // What Vue DevTools does when it connects: it receives the plugin through this hook.
    const stop = hooks.hook("devtools-plugin:setup", (descriptor, setup) => { plugins.push([descriptor as Record<string, unknown>, setup]); });
    const { connect } = await notes();
    const app = createApp(defineComponent({ setup() { development.useView({ connect }); return () => h("p"); } }));
    try {
      // useView imports the package itself, which resolves the no-op under Node: attach the browser entry first.
      devtools.attachDevtools(app, async () => api as never);
      app.mount(document.body.appendChild(document.createElement("div")));
      await nextTick();
      await until(() => plugins.some(([descriptor]) => descriptor["app"] === app));
      const ours = plugins.filter(([descriptor]) => descriptor["app"] === app);
      expect(ours.map(([descriptor]) => descriptor["id"])).toEqual(["dev.runic-artifex.views"]);
      kit.callDevToolsPluginSetupFn(ours[0]!, app);
      // The kit adds the inspector through its asynchronous hooks.
      await until(() => Boolean(kit.getInspector("runic-views")));
      const read = () => kit.devtools.ctx.api.getInspectorTree({ inspectorId: "runic-views", filter: "" });
      await until(async () => (await read()).length > 0);
      const tree = await read();
      // The View connected before DevTools ran the plugin, and is open.
      expect(tree).toEqual([{ id: "notes", label: "NotesViewModel", tags: [expect.objectContaining({ label: "notes" }), expect.objectContaining({ label: expect.stringMatching(/^rev \d+$/) })] }]);
    } finally {
      stop();
      app.unmount();
    }
  });

  test("useView of the production build reads no channel, also with Vue DevTools on the page", async () => {
    installHook();
    const production = await import("../dist/index.js");
    const { connect } = await notes();
    const app = createApp(defineComponent({ setup() { production.useView({ connect }); return () => h("p"); } }));
    app.mount(document.body.appendChild(document.createElement("div")));
    await nextTick();
    await tick(50);
    expect(Symbol.for("runic.views.inspector") in host).toBe(false);
    expect(Symbol.for("runic.views.calls") in host).toBe(false);
    app.unmount();
  });

});

// The plugin API this package uses is the one `@vue/devtools-api` gives a plugin.
type RealSetup = typeof import("@vue/devtools-api").setupDevtoolsPlugin;
type OurSetup = Awaited<ReturnType<NonNullable<Parameters<typeof devtools.attachDevtools>[1]>>>["setupDevtoolsPlugin"];
export const compatible: (real: RealSetup) => OurSetup = real => real;
