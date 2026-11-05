// appearance/1 on the client (specs/application/README.md, "Appearance"): the push reader, the page's appearance store
// that combines the engine's media queries with the host's values, and the mock producer's pushes.
import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";
import { appearance, appearanceFeature, onAppearance, type Appearance } from "../dist/index.js";
import { readHostAppearance, reportHostAppearance } from "../dist/appearance.js";
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
  assert.deepEqual({ ...value }, { colorScheme: null, contrast: null, reducedMotion: null, textScale: null, accentColor: null, systemColors: null, host: null });
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

test("the host's values: text scale, accent and palette alone; contrast and motion the stronger; the scheme only without the engine's", () => {
  const media = fakeMedia(light);
  const seen: Appearance[] = [];
  const stop = onAppearance(next => seen.push(next));
  const palette = { canvas: "#000000", canvasText: "#ffffff", linkText: "#ffff00", grayText: "#3ff23f", highlight: "#1aebff",
    highlightText: "#000000", buttonFace: "#000000", buttonText: "#ffffff" };
  reportHostAppearance(readHostAppearance({ kind: "appearance", colorScheme: "dark", contrast: "forced-colors", reducedMotion: true,
    textScale: 1.25, accentColor: "#0078d4", systemColors: palette }));
  const value = appearance();
  assert.equal(seen.length, 1);
  assert.deepEqual([value.colorScheme, value.contrast, value.reducedMotion, value.textScale, value.accentColor], ["light", "forced-colors", true, 1.25, "#0078d4"],
    "the engine's scheme wins: a window can show a scheme of its own");
  assert.deepEqual(value.systemColors, palette);
  // The same values again change nothing.
  reportHostAppearance(readHostAppearance({ kind: "appearance", colorScheme: "dark", contrast: "forced-colors", reducedMotion: true,
    textScale: 1.25, accentColor: "#0078d4", systemColors: palette }));
  assert.equal(seen.length, 1);
  // The engine knows more contrast without the host.
  reportHostAppearance(readHostAppearance({ kind: "appearance", contrast: "no-preference", reducedMotion: false }));
  media.set({ "(prefers-contrast: no-preference)": false, "(prefers-contrast: more)": true });
  assert.deepEqual([appearance().contrast, appearance().reducedMotion, appearance().textScale], ["more", false, null]);
  // Without the engine's queries the host's scheme applies.
  stop();
  delete globals["matchMedia"];
  assert.equal(appearance().colorScheme, null, "the host's last push had no scheme");
  reportHostAppearance(readHostAppearance({ kind: "appearance", colorScheme: "dark" }));
  assert.equal(appearance().colorScheme, "dark");
  reportHostAppearance(null);
  assert.equal(appearance().host, null);
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
