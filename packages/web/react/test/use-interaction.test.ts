import * as React from "react";
import { createElement, StrictMode, useState, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, expectTypeOf, test } from "vitest";
import { useInteraction, type InteractionHandle } from "../dist/index.js";
import { connectDiscard, mockDiscard, until, type DiscardClient, type DiscardInput } from "./interactions.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

let root: Root | undefined;
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
  document.body.innerHTML = "";
});

/** Waits in act() until `condition` holds, so React renders what the request changed. */
async function actUntil(condition: () => boolean, message: string): Promise<void> {
  await act(async () => { await until(condition, message); });
}

async function setup() {
  const mock = mockDiscard();
  const first = await connectDiscard();
  let discard!: InteractionHandle<DiscardInput, boolean>;
  let setClient!: (client: DiscardClient | undefined) => void;
  function Dialog(): ReactNode {
    const [client, set] = useState<DiscardClient | undefined>(first);
    setClient = set;
    discard = useInteraction({ client }, "confirmDiscard");
    return discard.request ? createElement("p", { id: "prompt" }, discard.request.title) : null;
  }
  await act(async () => {
    root = createRoot(document.body.appendChild(document.createElement("div")));
    root.render(createElement(StrictMode, null, createElement(Dialog)));
  });
  return { mock, client: first, handle: () => discard, setClient: (client: DiscardClient | undefined) => act(async () => { setClient(client); }) };
}

describe("useInteraction (#1319)", () => {
  test("request is the open request's input until respond answers it", async () => {
    const { mock, client, handle } = await setup();
    await actUntil(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
    expectTypeOf(handle()).toEqualTypeOf<InteractionHandle<DiscardInput, boolean>>();
    const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
    await actUntil(() => handle().request !== undefined, "The request was not shown.");
    expect(document.querySelector("#prompt")?.textContent).toBe("Draft");
    expect(handle().pending).toBe(1);
    await act(async () => { handle().respond(true); });
    expect(handle().request).toBeUndefined();
    expect(document.querySelector("#prompt")).toBeNull();
    expect(await reply).toEqual({ kind: "answered", output: true });
    client.dispose();
  });

  test("an aborted request clears, and a late answer is ignored", async () => {
    const { mock, client, handle } = await setup();
    await actUntil(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
    const cancel = new AbortController();
    const reply = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
    await actUntil(() => handle().request !== undefined, "The request was not shown.");
    cancel.abort();
    await actUntil(() => handle().request === undefined, "The aborted request stayed.");
    await act(async () => { handle().respond(true); });
    expect(await reply).toEqual({ kind: "cancelled" });
    client.dispose();
  });

  test("two open requests are answered in order", async () => {
    const { mock, client, handle } = await setup();
    await actUntil(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
    const first = mock.interactions.confirmDiscard.request({ title: "First" });
    const second = mock.interactions.confirmDiscard.request({ title: "Second" });
    await actUntil(() => handle().pending === 2, "The requests were not shown.");
    expect(handle().request).toEqual({ title: "First" });
    await act(async () => { handle().respond(false); });
    expect(handle().request).toEqual({ title: "Second" });
    await act(async () => { handle().respond(true); });
    expect(await first).toEqual({ kind: "answered", output: false });
    expect(await second).toEqual({ kind: "answered", output: true });
    client.dispose();
  });

  test("a client change ends the open request, and unmounting removes the handler", async () => {
    const { mock, client, handle, setClient } = await setup();
    await actUntil(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
    const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
    await actUntil(() => handle().request !== undefined, "The request was not shown.");
    const next = await connectDiscard();
    await setClient(next);
    expect(handle().request).toBeUndefined();
    expect(await open).toEqual({ kind: "cancelled" });
    const pending = mock.interactions.confirmDiscard.request({ title: "Leaving" });
    await actUntil(() => handle().request !== undefined, "The new client's request was not shown.");
    await act(async () => { root!.unmount(); });
    root = undefined;
    expect(await pending).toEqual({ kind: "cancelled" });
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
    next.dispose();
    client.dispose();
  });
});
