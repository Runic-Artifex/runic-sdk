// Runic instances: Effects provided with CurrentRunicInstance connect and read through that instance, so two guests in one
// document share no state, connection state or translated text.
import { configureTranslatedText, createRunicInstance, pageInstance, type RunicInstance, type TextReference, type ViewClient } from "@runic-artifex/views";
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { createMockBridge, type MockBridge, type MockView } from "@runic-artifex/views/mock";
import { Effect, Stream } from "effect";
import { afterEach, describe, expect, test } from "vitest";
import { connect, connectionStates, CurrentRunicInstance, texts } from "../dist/index.js";

interface CounterState { readonly count: number }
const members = { count: (value: unknown) => bridgeWire.integer(value, 0, 1000) };
const connectCounter = (): Promise<ViewClient<CounterState>> =>
  connectView<CounterState>({ contract: "Tests.Counter:fp-1", route: "counter", members });
const saved: TextReference = { version: 2, catalog: "app", contractFingerprint: "sha256:1", key: "saved", arguments: {}, fallbackText: "Saved." };

const cleanup: (() => void)[] = [];
afterEach(() => { for (const step of cleanup.splice(0).reverse()) step(); });

interface Guest { readonly bridge: MockBridge; readonly view: MockView; readonly instance: RunicInstance }
function guest(count: number): Guest {
  const bridge = createMockBridge();
  const view = bridge.view("counter", { state: { count } });
  const instance = createRunicInstance({ bridge });
  cleanup.push(() => instance.dispose());
  return { bridge, view, instance };
}

describe("CurrentRunicInstance", () => {
  test("is the page instance unless provided", async () => {
    expect(await Effect.runPromise(Effect.gen(function* () { return yield* CurrentRunicInstance; }))).toBe(pageInstance());
  });

  test("two provided instances connect the same route to their own Bridges", async () => {
    const a = guest(1), b = guest(2);
    const counts = (instance: RunicInstance) => Effect.runPromise(Effect.scoped(
      Effect.map(connect(connectCounter), client => client.snapshot.count),
    ).pipe(Effect.provideService(CurrentRunicInstance, instance)));
    expect(await counts(a.instance)).toBe(1);
    expect(await counts(b.instance)).toBe(2);
    expect(a.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
    expect(b.bridge.calls.filter(call => call.kind === "open")).toHaveLength(1);
  });

  test("connection states and texts follow the provided instance", async () => {
    const a = guest(1), b = guest(2);
    cleanup.push(a.instance.run(() => configureTranslatedText({ resolve: (reference, locale) => `${locale}:${reference.key}`, getLocale: () => "en" })));
    const first = <A>(stream: Stream.Stream<A>, instance: RunicInstance) =>
      Effect.runPromise(Stream.runHead(stream).pipe(Effect.provideService(CurrentRunicInstance, instance)));
    b.bridge.disconnect();
    expect(await first(connectionStates({ lostAfter: 0 }), a.instance)).toEqual(expect.objectContaining({ value: "connected" }));
    expect(await first(connectionStates({ lostAfter: 0 }), b.instance)).toEqual(expect.objectContaining({ value: "lost" }));
    expect(await first(texts(saved), a.instance)).toEqual(expect.objectContaining({ value: "en:saved" }));
    expect(await first(texts(saved), b.instance)).toEqual(expect.objectContaining({ value: "Saved." }));
  });
});
