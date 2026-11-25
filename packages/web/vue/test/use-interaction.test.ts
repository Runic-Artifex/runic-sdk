// @vitest-environment happy-dom

import { createApp, defineComponent, h, nextTick, shallowRef } from "vue";
import { describe, expect, expectTypeOf, test } from "vitest";
import { useInteraction, type InteractionHandle, type InteractionRequest } from "../dist/index.js";
import { connectDiscard, mockDiscard, requestShown, until, type DiscardClient, type DiscardInput } from "./interactions.js";

async function setup() {
  const mock = mockDiscard();
  const first = await connectDiscard();
  const client = shallowRef<DiscardClient | undefined>(first);
  let discard!: InteractionHandle<DiscardInput, boolean>;
  let quit!: InteractionHandle<void, boolean>;
  // <button v-if="discard.request" id="yes" @click="discard.request.respond(true)">{{ discard.request.input.title }}</button>
  const app = createApp(defineComponent({
    setup() {
      discard = useInteraction({ client }, "confirmDiscard");
      quit = useInteraction({ client }, "confirmQuit");
      return () => discard.request
        ? h("button", { id: "yes", onClick: () => discard.request?.respond(true) }, discard.request.input.title)
        : null;
    },
  }));
  const target = document.createElement("div");
  app.mount(target);
  await until(() => mock.interactions.confirmDiscard.handled && mock.interactions.confirmQuit.handled, "The handlers were not registered.");
  return { mock, first, client, discard, quit, app, target };
}

describe("useInteraction (#1319)", () => {
  test("request is the open request's handle until its respond answers it", async () => {
    const { mock, first, discard, app, target } = await setup();
    expectTypeOf(discard.request).toEqualTypeOf<InteractionRequest<DiscardInput, boolean> | undefined>();
    const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    await nextTick();
    const button = target.querySelector<HTMLButtonElement>("#yes")!;
    expect(button.textContent).toBe("Draft");
    button.click();
    expect(discard.request).toBeUndefined();
    expect(await reply).toEqual({ kind: "answered", output: true });
    await nextTick();
    expect(target.querySelector("#yes")).toBeNull();
    app.unmount();
    first.dispose();
  });

  test("a double click answers one request, and the next stays open", async () => {
    const { mock, first, discard, app } = await setup();
    const one = mock.interactions.confirmDiscard.request({ title: "First" });
    const two = mock.interactions.confirmDiscard.request({ title: "Second" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    const shown = discard.request!;
    expect(shown.input).toEqual({ title: "First" });
    shown.respond(true);
    shown.respond(true);
    expect(await one).toEqual({ kind: "answered", output: true });
    await until(() => discard.request?.input.title === "Second", "The second request was not shown.");
    shown.respond(true);
    expect(discard.request?.input).toEqual({ title: "Second" });
    discard.request!.respond(false);
    expect(await two).toEqual({ kind: "answered", output: false });
    app.unmount();
    first.dispose();
  });

  test("a handle taken before an abort is ignored, and does not answer the next request", async () => {
    const { mock, first, discard, app } = await setup();
    const cancel = new AbortController();
    const superseded = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
    const next = mock.interactions.confirmDiscard.request({ title: "Next" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    const stale = discard.request!;
    expect(stale.input).toEqual({ title: "Superseded" });
    cancel.abort();
    await until(() => discard.request?.input.title === "Next", "The next request was not shown.");
    stale.respond(true);
    expect(discard.request?.input).toEqual({ title: "Next" });
    expect(await superseded).toEqual({ kind: "cancelled" });
    discard.request!.respond(false);
    expect(await next).toEqual({ kind: "answered", output: false });
    app.unmount();
    first.dispose();
  });

  test("open requests are answered in order", async () => {
    const { mock, first, discard, app } = await setup();
    const one = mock.interactions.confirmDiscard.request({ title: "First" });
    const two = mock.interactions.confirmDiscard.request({ title: "Second" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    expect(discard.request!.input).toEqual({ title: "First" });
    discard.request!.respond(false);
    await until(() => discard.request?.input.title === "Second", "The second request was not shown.");
    discard.request!.respond(true);
    expect(await one).toEqual({ kind: "answered", output: false });
    expect(await two).toEqual({ kind: "answered", output: true });
    app.unmount();
    first.dispose();
  });

  test("an interaction whose input is empty (Unit) has a handle while its request is open", async () => {
    const { mock, first, quit, app } = await setup();
    const reply = mock.interactions.confirmQuit.request();
    await until(() => quit.request !== undefined, "The request was not shown.");
    expect(quit.request!.input).toBeUndefined();
    quit.request!.respond(true);
    expect(quit.request).toBeUndefined();
    expect(await reply).toEqual({ kind: "answered", output: true });
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
    const { reply } = await requestShown(mock.interactions.confirmDiscard, { title: "Leaving" }, () => discard.request !== undefined);
    app.unmount();
    expect(await reply).toEqual({ kind: "cancelled" });
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
    next.dispose();
    first.dispose();
  });
});
