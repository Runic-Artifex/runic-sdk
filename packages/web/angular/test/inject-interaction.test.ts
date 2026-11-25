import "@angular/compiler";
import { Component, Injector, signal, type Signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, expectTypeOf, test } from "vitest";
import { injectInteraction, type InteractionHandle } from "@runic-artifex/angular";
import { connectDiscard, mockDiscard, until, type DiscardClient, type DiscardInput } from "./interactions.js";

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

async function setup() {
  const mock = mockDiscard();
  const client = signal<DiscardClient | undefined>(await connectDiscard());
  const injector = Injector.create({ providers: [], parent: TestBed.inject(Injector) });
  const discard = injectInteraction({ client }, "confirmDiscard", { injector });
  TestBed.tick();
  await until(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
  return { mock, client, discard };
}

describe("injectInteraction (#1319)", () => {
  test("request() is the open request's input until respond() answers it", async () => {
    const { mock, discard } = await setup();
    expectTypeOf(discard).toEqualTypeOf<InteractionHandle<DiscardInput, boolean>>();
    expectTypeOf(discard.request).toEqualTypeOf<Signal<DiscardInput | undefined>>();
    expect(discard.request()).toBeUndefined();
    const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    expect(discard.request()).toEqual({ title: "Draft" });
    expect(discard.pending()).toBe(1);
    discard.respond(true);
    expect(discard.request()).toBeUndefined();
    expect(await reply).toEqual({ kind: "answered", output: true });
    // @ts-expect-error The answer is the interaction's output.
    discard.respond("yes");
  });

  test("an aborted request clears, and a late answer is ignored", async () => {
    const { mock, discard } = await setup();
    const cancel = new AbortController();
    const reply = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    cancel.abort();
    await until(() => discard.request() === undefined, "The aborted request stayed.");
    discard.respond(true);
    expect(await reply).toEqual({ kind: "cancelled" });
    expect(discard.pending()).toBe(0);
  });

  test("two open requests are answered in order", async () => {
    const { mock, discard } = await setup();
    const first = mock.interactions.confirmDiscard.request({ title: "First" });
    const second = mock.interactions.confirmDiscard.request({ title: "Second" });
    await until(() => discard.pending() === 2, "The requests were not shown.");
    expect(discard.request()).toEqual({ title: "First" });
    discard.respond(false);
    expect(discard.request()).toEqual({ title: "Second" });
    discard.respond(true);
    expect(await first).toEqual({ kind: "answered", output: false });
    expect(await second).toEqual({ kind: "answered", output: true });
  });

  test("a client change ends the open request and registers with the new client", async () => {
    const { mock, client, discard } = await setup();
    const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    const previous = client()!;
    client.set(await connectDiscard());
    TestBed.tick();
    expect(discard.request()).toBeUndefined();
    expect(await open).toEqual({ kind: "cancelled" });
    const reply = mock.interactions.confirmDiscard.request({ title: "New client" });
    await until(() => discard.request() !== undefined, "The new client's request was not shown.");
    discard.respond(true);
    expect(await reply).toEqual({ kind: "answered", output: true });
    client.set(undefined);
    TestBed.tick();
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered without a client.");
    previous.dispose();
  });

  test("destroying the context removes the handler and ends the open request", async () => {
    const mock = mockDiscard();
    const editor = await connectDiscard();
    const client = signal(editor);
    class Dialog {
      readonly discard = injectInteraction({ client }, "confirmDiscard");
    }
    Component({
      selector: "test-dialog",
      standalone: true,
      template: `@if (discard.request(); as input) { <p id="prompt">{{ input.title }}</p> }`,
    })(Dialog);
    const fixture = TestBed.createComponent(Dialog);
    await fixture.whenStable();
    await until(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
    const open = mock.interactions.confirmDiscard.request({ title: "Leaving" });
    await until(() => fixture.componentInstance.discard.request() !== undefined, "The request was not shown.");
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector("#prompt")?.textContent).toBe("Leaving");
    fixture.destroy();
    expect(await open).toEqual({ kind: "cancelled" });
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
    editor.dispose();
  });
});
