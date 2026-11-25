import "@angular/compiler";
import { Component, Injector, signal, type Signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, expectTypeOf, test } from "vitest";
import { injectInteraction, type InteractionHandle, type InteractionRequest } from "@runic-artifex/angular";
import { connectDiscard, mockDiscard, requestShown, until, type DiscardClient, type DiscardInput } from "./interactions.js";

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

async function setup() {
  const mock = mockDiscard();
  const client = signal<DiscardClient | undefined>(await connectDiscard());
  const injector = Injector.create({ providers: [], parent: TestBed.inject(Injector) });
  const discard = injectInteraction({ client }, "confirmDiscard", { injector });
  const quit = injectInteraction({ client }, "confirmQuit", { injector });
  TestBed.tick();
  await until(() => mock.interactions.confirmDiscard.handled && mock.interactions.confirmQuit.handled, "The handlers were not registered.");
  return { mock, client, discard, quit };
}

describe("injectInteraction (#1319)", () => {
  test("request() is the open request's handle until its respond answers it", async () => {
    const { mock, discard } = await setup();
    expectTypeOf(discard).toEqualTypeOf<InteractionHandle<DiscardInput, boolean>>();
    expectTypeOf(discard.request).toEqualTypeOf<Signal<InteractionRequest<DiscardInput, boolean> | undefined>>();
    expect(discard.request()).toBeUndefined();
    const reply = mock.interactions.confirmDiscard.request({ title: "Draft" });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    const request = discard.request()!;
    expect(request.input).toEqual({ title: "Draft" });
    request.respond(true);
    expect(discard.request()).toBeUndefined();
    expect(await reply).toEqual({ kind: "answered", output: true });
    // @ts-expect-error The answer is the interaction's output.
    request.respond("yes");
  });

  test("a double click answers one request, and the next stays open", async () => {
    const { mock, discard } = await setup();
    const first = mock.interactions.confirmDiscard.request({ title: "First" });
    const second = mock.interactions.confirmDiscard.request({ title: "Second" });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    const shown = discard.request()!;
    expect(shown.input).toEqual({ title: "First" });
    shown.respond(true);
    shown.respond(true);
    expect(await first).toEqual({ kind: "answered", output: true });
    await until(() => discard.request()?.input.title === "Second", "The second request was not shown.");
    shown.respond(true);
    // A click on the answered request answers nothing.
    expect(discard.request()?.input).toEqual({ title: "Second" });
    discard.request()!.respond(false);
    expect(await second).toEqual({ kind: "answered", output: false });
  });

  test("a handle taken before an abort is ignored, and does not answer the next request", async () => {
    const { mock, discard } = await setup();
    const cancel = new AbortController();
    const superseded = mock.interactions.confirmDiscard.request({ title: "Superseded" }, { signal: cancel.signal });
    const next = mock.interactions.confirmDiscard.request({ title: "Next" });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    const stale = discard.request()!;
    expect(stale.input).toEqual({ title: "Superseded" });
    cancel.abort();
    await until(() => discard.request()?.input.title === "Next", "The next request was not shown.");
    stale.respond(true);
    expect(discard.request()?.input).toEqual({ title: "Next" });
    expect(await superseded).toEqual({ kind: "cancelled" });
    discard.request()!.respond(false);
    expect(await next).toEqual({ kind: "answered", output: false });
  });

  test("open requests are answered in order", async () => {
    const { mock, discard } = await setup();
    const first = mock.interactions.confirmDiscard.request({ title: "First" });
    const second = mock.interactions.confirmDiscard.request({ title: "Second" });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    expect(discard.request()!.input).toEqual({ title: "First" });
    discard.request()!.respond(false);
    await until(() => discard.request()?.input.title === "Second", "The second request was not shown.");
    discard.request()!.respond(true);
    expect(discard.request()).toBeUndefined();
    expect(await first).toEqual({ kind: "answered", output: false });
    expect(await second).toEqual({ kind: "answered", output: true });
  });

  test("an interaction whose input is empty (Unit) has a handle while its request is open", async () => {
    const { mock, quit } = await setup();
    const reply = mock.interactions.confirmQuit.request();
    await until(() => quit.request() !== undefined, "The request was not shown.");
    expect(quit.request()!.input).toBeUndefined();
    quit.request()!.respond(true);
    expect(quit.request()).toBeUndefined();
    expect(await reply).toEqual({ kind: "answered", output: true });
  });

  test("a client change ends the open request and registers with the new client", async () => {
    const { mock, client, discard } = await setup();
    const open = mock.interactions.confirmDiscard.request({ title: "Old client" });
    await until(() => discard.request() !== undefined, "The request was not shown.");
    const old = discard.request()!;
    const previous = client()!;
    client.set(await connectDiscard());
    TestBed.tick();
    expect(discard.request()).toBeUndefined();
    expect(await open).toEqual({ kind: "cancelled" });
    const { reply } = await requestShown(mock.interactions.confirmDiscard, { title: "New client" }, () => discard.request() !== undefined);
    old.respond(false);
    discard.request()!.respond(true);
    expect(await reply).toEqual({ kind: "answered", output: true });
    client.set(undefined);
    TestBed.tick();
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered without a client.");
    previous.dispose();
  });

  test("the template pattern; destroying the context removes the handler and ends the open request", async () => {
    const mock = mockDiscard();
    const editor = await connectDiscard();
    const client = signal(editor);
    class Dialog {
      readonly discard = injectInteraction({ client }, "confirmDiscard");
    }
    Component({
      selector: "test-dialog",
      standalone: true,
      template: `@if (discard.request(); as request) {
        <p id="prompt">{{ request.input.title }}</p><button id="yes" (click)="request.respond(true)">Yes</button>
      }`,
    })(Dialog);
    const fixture = TestBed.createComponent(Dialog);
    await fixture.whenStable();
    await until(() => mock.interactions.confirmDiscard.handled, "The handler was not registered.");
    const root = fixture.nativeElement as HTMLElement;
    const answered = mock.interactions.confirmDiscard.request({ title: "Shown" });
    await until(() => fixture.componentInstance.discard.request() !== undefined, "The request was not shown.");
    await fixture.whenStable();
    expect(root.querySelector("#prompt")?.textContent).toBe("Shown");
    root.querySelector<HTMLButtonElement>("#yes")!.click();
    expect(await answered).toEqual({ kind: "answered", output: true });
    const open = mock.interactions.confirmDiscard.request({ title: "Leaving" });
    await until(() => fixture.componentInstance.discard.request() !== undefined, "The request was not shown.");
    fixture.destroy();
    expect(await open).toEqual({ kind: "cancelled" });
    await until(() => !mock.interactions.confirmDiscard.handled, "The handler stayed registered.");
    editor.dispose();
  });
});
