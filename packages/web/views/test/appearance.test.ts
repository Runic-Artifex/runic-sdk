// appearance/1 on the client (specs/application/README.md, "Appearance"): the push reader, the page's appearance store
// that reports the engine's media queries and the host's values and combines them (the latest change wins), and the mock
// producer's pushes.
import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";
import { appearance, appearanceFeature, onAppearance, type Appearance } from "../dist/index.js";
import { readHostAppearance } from "../dist/appearance.js";
import { receiveAppearancePush } from "../dist/appearance-push.js";
import { connectView } from "../dist/generated.js";
import { createMockBridge, installMockBridge } from "../dist/mock.js";
import * as bridgeWire from "../dist/wire.js";

const channelKey = Symbol.for("runic.views.appearance");
const globals = globalThis as unknown as Record<string | symbol, unknown>;

// A matchMedia whose answers the test sets, with change listeners like MediaQueryList's.
function fakeMedia(initial: Record<string, boolean>) {
  const values = { ...initial };
  const lists = new Map<string, Set<() => void>>();
  const match = (query: string) => {
    const listeners = lists.get(query) ?? new Set<() => void>();
    lists.set(query, listeners);
    return {
      get matches() { return values[query] === true; },
      addEventListener: (_: "change", listener: () => void) => { listeners.add(listener); },
      removeEventListener: (_: "change", listener: () => void) => { listeners.delete(listener); },
    };
  };
  globals["matchMedia"] = match;
  return {
    set(changes: Record<string, boolean>) {
      Object.assign(values, changes);
      for (const query of Object.keys(changes)) for (const listener of [...lists.get(query) ?? []]) listener();
    },
    listening: () => [...lists.values()].reduce((count, listeners) => count + listeners.size, 0),
  };
}

const light = { "(prefers-color-scheme: light)": true, "(forced-colors: none)": true, "(prefers-contrast: no-preference)": true,
  "(prefers-reduced-motion: no-preference)": true };

// Other suites in the process may have used the page-wide state or installed a DOM.
let saved: { readonly channel: unknown; readonly matchMedia: unknown } | undefined;
beforeEach(() => {
  saved = { channel: globals[channelKey], matchMedia: globals["matchMedia"] };
  delete globals[channelKey];
  delete globals["matchMedia"];
});
afterEach(() => {
  delete globals[channelKey];
  delete globals["matchMedia"];
  if (saved?.channel !== undefined) globals[channelKey] = saved.channel;
  if (saved?.matchMedia !== undefined) globals["matchMedia"] = saved.matchMedia;
});

test("without an engine or a host every member is null, as under server rendering", () => {
  const value = appearance();
  assert.deepEqual(JSON.parse(JSON.stringify(value)), { colorScheme: null, contrast: null, reducedMotion: null, textScale: null, accentColor: null,
    systemColors: null, engine: { colorScheme: null, contrast: null, reducedMotion: null }, host: null });
  assert.equal(appearance(), value, "the same object while nothing changed");
  assert.ok(Object.isFrozen(value));
});

test("the engine's media queries give the colour scheme, contrast and reduced motion", () => {
  const media = fakeMedia(light);
  assert.deepEqual([appearance().colorScheme, appearance().contrast, appearance().reducedMotion], ["light", "no-preference", false]);
  const seen: Appearance[] = [];
  const stop = onAppearance(next => seen.push(next));
  media.set({ "(prefers-color-scheme: light)": false, "(prefers-color-scheme: dark)": true });
  media.set({ "(forced-colors: none)": false, "(forced-colors: active)": true });
  media.set({ "(prefers-reduced-motion: no-preference)": false, "(prefers-reduced-motion: reduce)": true });
  assert.deepEqual(seen.map(value => [value.colorScheme, value.contrast, value.reducedMotion]),
    [["dark", "no-preference", false], ["dark", "forced-colors", false], ["dark", "forced-colors", true]]);
  stop();
  assert.equal(media.listening(), 0, "the last listener stops watching the queries");
  stop();
});

test("the host's values: text scale, accent and palette alone; the engine's scheme, contrast and motion until the host changes them", () => {
  const media = fakeMedia(light);
  const seen: Appearance[] = [];
  const stop = onAppearance(next => seen.push(next));
  const palette = { canvas: "#000000", canvasText: "#ffffff", linkText: "#ffff00", grayText: "#3ff23f", highlight: "#1aebff",
    highlightText: "#000000", buttonFace: "#000000", buttonText: "#ffffff" };
  receiveAppearancePush({ kind: "appearance", colorScheme: "dark", contrast: "forced-colors", reducedMotion: true,
    textScale: 1.25, accentColor: "#0078d4", systemColors: palette });
  const value = appearance();
  assert.equal(seen.length, 1);
  assert.deepEqual([value.colorScheme, value.contrast, value.reducedMotion, value.textScale, value.accentColor], ["light", "no-preference", false, 1.25, "#0078d4"],
    "the engine's values stand: a window can show a scheme of its own, and the first push changes nothing the page saw");
  assert.deepEqual(value.systemColors, palette);
  assert.deepEqual([value.engine.contrast, value.host?.contrast], ["no-preference", "forced-colors"], "each source alone");
  // The same values again change nothing.
  receiveAppearancePush({ kind: "appearance", colorScheme: "dark", contrast: "forced-colors", reducedMotion: true,
    textScale: 1.25, accentColor: "#0078d4", systemColors: palette });
  assert.equal(seen.length, 1);
  // Without the engine's queries the host's values apply.
  stop();
  delete globals["matchMedia"];
  assert.deepEqual([appearance().colorScheme, appearance().contrast, appearance().reducedMotion], ["dark", "forced-colors", true]);
  receiveAppearancePush(undefined);
  assert.equal(appearance().host, null);
});

test("the latest change wins: a host's change applies until the engine's media query changes", () => {
  // An engine that read the desktop when the page loaded and does not follow it (WebView2's animations, WebKitGTK 6.0).
  const media = fakeMedia({ ...light, "(prefers-reduced-motion: no-preference)": false, "(prefers-reduced-motion: reduce)": true });
  const seen: Appearance[] = [];
  const stop = onAppearance(next => seen.push(next));
  receiveAppearancePush({ kind: "appearance", contrast: "no-preference", reducedMotion: true });
  assert.deepEqual([appearance().contrast, appearance().reducedMotion], ["no-preference", true]);
  // The desktop turns animations back on: the host's change wins over the engine's stale reduce.
  receiveAppearancePush({ kind: "appearance", contrast: "no-preference", reducedMotion: false });
  assert.deepEqual([appearance().reducedMotion, appearance().engine.reducedMotion, appearance().host?.reducedMotion], [false, true, false]);
  // A contrast theme turned on while the page is open, then off again: the store follows the host both ways, and no
  // palette stays.
  const palette = { canvas: "#000000", canvasText: "#ffffff", linkText: "#ffff00", grayText: "#3ff23f", highlight: "#1aebff",
    highlightText: "#000000", buttonFace: "#000000", buttonText: "#ffffff" };
  receiveAppearancePush({ kind: "appearance", contrast: "forced-colors", reducedMotion: false, systemColors: palette });
  assert.deepEqual([appearance().contrast, appearance().engine.contrast], ["forced-colors", "no-preference"]);
  receiveAppearancePush({ kind: "appearance", contrast: "no-preference", reducedMotion: false });
  assert.deepEqual([appearance().contrast, appearance().systemColors], ["no-preference", null], "the switch-off direction");
  // The engine's own change is the latest again.
  media.set({ "(prefers-reduced-motion: reduce)": false, "(prefers-reduced-motion: no-preference)": true });
  media.set({ "(prefers-reduced-motion: no-preference)": false, "(prefers-reduced-motion: reduce)": true });
  assert.deepEqual([appearance().reducedMotion, appearance().host?.reducedMotion], [true, false], "the engine changed after the host");
  // The host no longer knows the member: the engine's value.
  receiveAppearancePush({ kind: "appearance", contrast: "more" });
  receiveAppearancePush({ kind: "appearance" });
  assert.deepEqual([appearance().contrast, appearance().reducedMotion], ["no-preference", true]);
  stop();
  // Without a listener the store still notices the engine's change, by reading the queries.
  receiveAppearancePush({ kind: "appearance", contrast: "more" });
  receiveAppearancePush({ kind: "appearance", contrast: "forced-colors" });
  assert.equal(appearance().contrast, "forced-colors");
  media.set({ "(prefers-contrast: no-preference)": false, "(prefers-contrast: more)": true });
  assert.equal(appearance().contrast, "more");
  assert.ok(seen.length > 0);
});

test("the reader reads a member of the wrong type, out of range or unknown as null, and the rest still applies", () => {
  assert.deepEqual({ ...readHostAppearance({ kind: "appearance", colorScheme: "sepia", contrast: "less", reducedMotion: "yes", textScale: 0,
    accentColor: "blue", systemColors: { canvas: "#000000" } }) },
  { colorScheme: null, contrast: null, reducedMotion: null, textScale: null, accentColor: null, systemColors: null });
  assert.deepEqual({ ...readHostAppearance({ kind: "appearance", colorScheme: "light", textScale: Number.POSITIVE_INFINITY, accentColor: "#ABCDEF" }) },
    { colorScheme: "light", contrast: null, reducedMotion: null, textScale: null, accentColor: null, systemColors: null },
    "colours are lowercase #rrggbb");
});

test("the mock producer pushes the window's appearance after the hello and on each change, with every member", async () => {
  const bridge = createMockBridge();
  installMockBridge(bridge);
  bridge.view("status", { state: { count: 1 } });
  bridge.setAppearance({ colorScheme: "dark", contrast: "more", reducedMotion: true, textScale: 1.5 });
  assert.deepEqual(bridge.appearance, { colorScheme: "dark", contrast: "more", reducedMotion: true, textScale: 1.5, accentColor: null, systemColors: null });
  const client = await connectView<{ readonly count: number }>({ contract: "Tests.Status:fp-1", route: "status",
    members: { count: (value: unknown) => bridgeWire.integer(value, 0, 10) } });
  await bridge.flush();
  assert.deepEqual(appearance().host, bridge.appearance, "pushed after the hello");
  assert.equal(appearance().textScale, 1.5);
  const pushes = () => bridge.calls.length;
  bridge.setAppearance({ colorScheme: "dark", contrast: "more", reducedMotion: true, textScale: 1.5 });
  await bridge.flush();
  assert.equal(bridge.calls.length, pushes(), "an equal appearance pushes nothing");
  bridge.setAppearance({ accentColor: "#3584e4" });
  await bridge.flush();
  assert.deepEqual([appearance().host?.accentColor, appearance().host?.textScale], ["#3584e4", null], "every member is pushed");
  bridge.setAppearance(null);
  await bridge.flush();
  assert.deepEqual({ ...appearance().host }, { colorScheme: null, contrast: null, reducedMotion: null, textScale: null, accentColor: null, systemColors: null });
  assert.equal(appearanceFeature, "appearance/1");
  assert.equal(client.snapshot.count, 1);
});
