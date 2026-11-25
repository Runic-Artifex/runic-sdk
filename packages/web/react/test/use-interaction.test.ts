import * as React from "react";
import { createElement, StrictMode, useState, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, expectTypeOf, test } from "vitest";
import { useInteraction, type InteractionHandle, type InteractionRequest } from "../dist/index.js";
import { connectDiscard, mockDiscard, requestShown, until, type DiscardClient, type DiscardInput } from "./interactions.js";

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
  let quit!: InteractionHandle<void, boolean>;
  let setClient!: (client: DiscardClient | undefined) => void;
  function Dialog(): ReactNode {
    const [client, set] = useState<DiscardClient | undefined>(first);
    setClient = set;
    discard = useInteraction({ client }, "confirmDiscard");
    quit = useInteraction({ client }, "confirmQuit");
    const request = discard.request;
    return request
      ? createElement("button", { id: "yes", onClick: () => request.respond(true) }, request.input.title)
      : null;
  }
  await act(async () => {
    root = createRoot(document.body.appendChild(document.createElement("div")));
    root.render(createElement(StrictMode, null, createElement(Dialog)));
  });
  await actUntil(() => mock.interactions.confirmDiscard.handled && mock.interactions.confirmQuit.handled, "The handlers were not registered.");
  return {
    mock, client: first, discard: () => discard, quit: () => quit,
    setClient: (client: DiscardClient | undefined) => act(async () => { setClient(client); }),
  };
}

describe("useInteraction (#1319)", () => {
  test("request is the open request's handle until its respond answers it", async () => {
    const { mock, client, discard } = await setup();
    expectTypeOf(discard()).toEqualTypeOf<InteractionHandle<DiscardInput, boolean>>();
    expectTypeOf(discard().request).toEqualTypeOf<InteractionRequest<DiscardInput, boolean> | undefined>();
    const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
    await actUntil(() => discard().request !== undefined, "The request was not shown.");
    const button = document.querySelector<HTMLButtonElement>("#yes")!;
    expect(button.textContent).toBe("Draft");
    await act(async () => { button.click(); });
    expect(discard().request).toBeUndefined();
    expect(document.querySelector("#yes")).toBeNull();
    expect(await reply).toEqual({ kind: "answered", output: true });
    client.dispose();
  });

  test("a double click answers one request, and the next stays open", async () => {
    const { mock, client, discard } = await setup();
    const first = mock.interactions.confirmDiscard.request({ title: "First" });
    const second = mock.interactions.confirmDiscard.request({ title: "Second" });
    await actUntil(() => discard().request !== undefined, "The request was not shown.");
    const shown = discard().request!;
    expect(shown.input).toEqual({ title: "First" });
    await act(async () => { shown.respond(true); shown.respond(true); });
    expect(await first).toEqual({ kind: "answered", output: true });
    await actUntil(() => discard().request?.input.title === "Second", "The second request was not shown.");
    await act(async () => { shown.respond(true); });
    expect(discard().request?.input).toEqual({ title: "Second" });
    await act(async () => { discard().request!.respond(false); });
    expect(await second).toEqual({ kind: "answered", output: false });
    client.dispose();
  });

  test("a handle taken before an abort is ignored, and does not answer the next request", async () => {
    const { mock, client, discard } = await setup();
    const cancel = new AbortController();
    const superseded = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
    const next = mock.interactions.confirmDiscard.request({ title: "Next" });
    await actUntil(() => discard().request !== undefined, "The request was not shown.");
    const stale = discard().request!;
    expect(stale.input).toEqual({ title: "Superseded" });
    cancel.abort();
    await actUntil(() => discard().request?.input.title === "Next", "The next request was not shown.");
    await act(async () => { stale.respond(true); });
    expect(discard().request?.input).toEqual({ title: "Next" });
    expect(await superseded).toEqual({ kind: "cancelled" });
    await act(async () => { discard().request!.respond(false); });
    expect(await next).toEqual({ kind: "answered", output: false });
    client.dispose();
  });

  test("open requests are answered in order", async () => {
    const { mock, client, discard } = await setup();
    const first = mock.interactions.confirmDiscard.request({ title: "First" });
    const second = mock.interactions.confirmDiscard.request({ title: "Second" });
    await actUntil(() => discard().request !== undefined, "The request was not shown.");
    expect(discard().request!.input).toEqual({ title: "First" });
    await act(async () => { discard().request!.respond(false); });
    await actUntil(() => discard().request?.input.title === "Second", "The second request was not shown.");
    await act(async () => { discard().request!.respond(true); });
    expect(await first).toEqual({ kind: "answered", output: false });
    expect(await second).toEqual({ kind: "answered", output: true });
    client.dispose();
  });

  test("an interaction whose input is empty (Unit) has a handle while its request is open", async () => {
    const { mock, client, quit } = await setup();
    const reply = mock.interactions.confirmQuit.request();
    await actUntil(() => quit().request !== undefined, "The request was not shown.");
    expect(quit().request!.input).toBeUndefined();
    await act(async () => { quit().request!.respond(true); });
    expect(quit().request).toBeUndefined();
    expect(await reply).toEqual({ kind: "answered", output: true });
    client.dispose();
  });

  test("a client change ends the open request, and unmounting removes the handler", async () => {
    const { mock, client, discard, setClient } = await setup();
    const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
    await actUntil(() => discard().request !== undefined, "The request was not shown.");
    const next = await connectDiscard();
    await setClient(next);
    expect(discard().request).toBeUndefined();
    expect(await open).toEqual({ kind: "cancelled" });
    let pending!: Awaited<ReturnType<typeof requestShown<DiscardInput, boolean>>>;
    await act(async () => {
      pending = await requestShown(mock.interactions.confirmDiscard, { title: "Leaving" }, () => discard().request !== undefined);
    });
    await act(async () => { root!.unmount(); });
    root = undefined;
    expect(await pending.reply).toEqual({ kind: "cancelled" });
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
    next.dispose();
    client.dispose();
  });
});
