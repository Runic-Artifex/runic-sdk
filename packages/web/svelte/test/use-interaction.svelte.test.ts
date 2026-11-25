// @vitest-environment happy-dom

import { flushSync, mount, unmount } from "svelte";
import { describe, expect, expectTypeOf, test } from "vitest";
import type { InteractionHandle } from "../src/views/index.js";
import InteractionOwner from "./fixtures/InteractionOwner.svelte";
import { connectDiscard, mockDiscard, until, type DiscardClient, type DiscardInput } from "./interactions.js";

async function setup() {
  const mock = mockDiscard();
  const first = await connectDiscard();
  const props = $state<{ client: DiscardClient | undefined }>({ client: first });
  const target = document.createElement("div");
  document.body.append(target);
  const owner = mount(InteractionOwner, { target, props });
  flushSync();
  const discard = owner.discard as InteractionHandle<DiscardInput, boolean>;
  await until(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
  return {
    mock, first, props, discard, target,
    destroy() { void unmount(owner); target.remove(); },
  };
}

describe("useInteraction (#1319)", () => {
  test("request is the open request's input until respond answers it", async () => {
    const { mock, first, discard, target, destroy } = await setup();
    expectTypeOf(discard.request).toEqualTypeOf<DiscardInput | undefined>();
    const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    flushSync();
    expect(target.querySelector("#prompt")?.textContent).toBe("Draft");
    expect(discard.pending).toBe(1);
    discard.respond(true);
    flushSync();
    expect(discard.request).toBeUndefined();
    expect(target.querySelector("#prompt")).toBeNull();
    expect(await reply).toEqual({ kind: "answered", output: true });
    destroy();
    first.dispose();
  });

  test("an aborted request clears, and a late answer is ignored", async () => {
    const { mock, first, discard, destroy } = await setup();
    const cancel = new AbortController();
    const reply = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
    await until(() => discard.request !== undefined, "The request was not shown.");
    cancel.abort();
    await until(() => discard.request === undefined, "The aborted request stayed.");
    discard.respond(true);
    expect(await reply).toEqual({ kind: "cancelled" });
    destroy();
    first.dispose();
  });

  test("two open requests are answered in order", async () => {
    const { mock, first, discard, destroy } = await setup();
    const one = mock.interactions.confirmDiscard.request({ title: "First" });
    const two = mock.interactions.confirmDiscard.request({ title: "Second" });
    await until(() => discard.pending === 2, "The requests were not shown.");
    expect(discard.request).toEqual({ title: "First" });
    discard.respond(false);
    expect(discard.request).toEqual({ title: "Second" });
    discard.respond(true);
    expect(await one).toEqual({ kind: "answered", output: false });
    expect(await two).toEqual({ kind: "answered", output: true });
    destroy();
    first.dispose();
  });

  test("a client change ends the open request, and destroying the component removes the handler", async () => {
    const { mock, first, props, discard, destroy } = await setup();
    const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
    await until(() => discard.request !== undefined, "The request was not shown.");
    const next = await connectDiscard();
    props.client = next;
    flushSync();
    expect(discard.request).toBeUndefined();
    expect(await open).toEqual({ kind: "cancelled" });
    const pending = mock.interactions.confirmDiscard.request({ title: "Leaving" });
    await until(() => discard.request !== undefined, "The new client's request was not shown.");
    destroy();
    expect(await pending).toEqual({ kind: "cancelled" });
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
    next.dispose();
    first.dispose();
  });
});
