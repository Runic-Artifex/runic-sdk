// @vitest-environment happy-dom

import { createApp, defineComponent, h, nextTick, shallowRef } from "vue";
import { describe, expect, expectTypeOf, test } from "vitest";
import { useInteraction, type InteractionHandle } from "../dist/index.js";
import { connectDiscard, mockDiscard, until, type DiscardClient, type DiscardInput } from "./interactions.js";

async function setup() {
  const mock = mockDiscard();
  const first = await connectDiscard();
  const client = shallowRef<DiscardClient | undefined>(first);
  let discard!: InteractionHandle<DiscardInput, boolean>;
  // <p v-if="discard.request" id="prompt">{{ discard.request.title }}</p>
  const app = createApp(defineComponent({
    setup() {
      discard = useInteraction({ client }, "confirmDiscard");
      return () => discard.request ? h("p", { id: "prompt" }, discard.request.title) : null;
    },
  }));
  const target = document.createElement("div");
  app.mount(target);
  await until(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
  return { mock, first, client, discard, app, target };
}

describe("useInteraction (#1319)", () => {
  test("request is the open request's input until respond answers it", async () => {
    const { mock, first, discard, app, target } = await setup();
    expectTypeOf(discard.request).toEqualTypeOf<DiscardInput | undefined>();
    const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    await nextTick();
    expect(target.querySelector("#prompt")?.textContent).toBe("Draft");
    expect(discard.pending).toBe(1);
    discard.respond(true);
    expect(discard.request).toBeUndefined();
    expect(await reply).toEqual({ kind: "answered", output: true });
    await nextTick();
    expect(target.querySelector("#prompt")).toBeNull();
    app.unmount();
    first.dispose();
  });

  test("an aborted request clears, and a late answer is ignored", async () => {
    const { mock, first, discard, app } = await setup();
    const cancel = new AbortController();
    const reply = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
    await until(() => discard.request !== undefined, "The request was not shown.");
    cancel.abort();
    await until(() => discard.request === undefined, "The aborted request stayed.");
    discard.respond(true);
    expect(await reply).toEqual({ kind: "cancelled" });
    app.unmount();
    first.dispose();
  });

  test("two open requests are answered in order", async () => {
    const { mock, first, discard, app } = await setup();
    const one = mock.interactions.confirmDiscard.request({ title: "First" });
    const two = mock.interactions.confirmDiscard.request({ title: "Second" });
    await until(() => discard.pending === 2, "The requests were not shown.");
    expect(discard.request).toEqual({ title: "First" });
    discard.respond(false);
    expect(discard.request).toEqual({ title: "Second" });
    discard.respond(true);
    expect(await one).toEqual({ kind: "answered", output: false });
    expect(await two).toEqual({ kind: "answered", output: true });
    app.unmount();
    first.dispose();
  });

  test("a client change ends the open request, and unmounting removes the handler", async () => {
    const { mock, first, client, discard, app } = await setup();
    const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    const next = await connectDiscard();
    client.value = next;
    await nextTick();
    expect(discard.request).toBeUndefined();
    expect(await open).toEqual({ kind: "cancelled" });
    const pending = mock.interactions.confirmDiscard.request({ title: "Leaving" });
    await until(() => discard.request !== undefined, "The new client's request was not shown.");
    app.unmount();
    expect(await pending).toEqual({ kind: "cancelled" });
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
    next.dispose();
    first.dispose();
  });
});
