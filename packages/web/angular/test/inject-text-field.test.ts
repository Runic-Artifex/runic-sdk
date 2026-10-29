import "@angular/compiler";
import { Component, signal, type Signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, expectTypeOf, test } from "vitest";
import { bridgeSuccess, type BridgeOperation, type BridgeOperationStatus, type ViewClient } from "@runic-artifex/views";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge, type MockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { injectOperation, injectTextField, injectView, type TextFieldHandle } from "../dist/esm/index.js";

type State = { readonly title: string };

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

/** A mock View whose setter trims, behind 100 ms of virtual latency. */
async function editor(): Promise<{ bridge: MockBridge; client: ViewConnection<State> }> {
  const bridge = installMockBridge(createMockBridge({ scheduling: "manual", latency: 100 }));
  bridge.view("editor", { state: { title: "" }, set: { title: (_state, value) => ({ title: (value as string).trim() }) } });
  const pending = connectView<State>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string } });
  for (let round = 0; round < 20; round++) await bridge.advance(50);
  return { bridge, client: await pending };
}

function fakeClient(title: string): ViewClient<State> {
  const client = {
    snapshot: { title } as State,
    subscribe(listener: (state: State) => void) { listener(client.snapshot); return () => {}; },
    dispose() {},
  };
  return client;
}

let source: ViewConnection<State> | undefined;
class Editor {
  readonly editor = injectView(source!);
  readonly title = injectTextField(this.editor, state => state.title, (client, value) => client.set("title", value));
}
Component({
  selector: "test-text-field",
  standalone: true,
  template: `<input [value]="title.value()" (input)="title.input($event)">`,
})(Editor);

describe("injectTextField", () => {
  test("keeps the draft while writes are in flight, then shows the View's normalized value", async () => {
    const { bridge, client } = await editor();
    source = client;
    const fixture = TestBed.createComponent(Editor);
    await fixture.whenStable();
    const field: TextFieldHandle = fixture.componentInstance.title;
    expectTypeOf(field.value).toEqualTypeOf<Signal<string>>();
    const input = (fixture.nativeElement as HTMLElement).querySelector("input")!;
    for (const typed of ["a", "ab", "ab ", "ab c "]) {
      input.value = typed;
      input.dispatchEvent(new Event("input"));
      TestBed.tick();
      expect(field.value()).toBe(typed);
      expect(field.editing()).toBe(true);
      await bridge.advance(30);
      TestBed.tick();
      expect(input.value).toBe(typed);
    }
    for (let round = 0; round < 20; round++) await bridge.advance(50);
    TestBed.tick();
    expect(field.value()).toBe("ab c");
    expect(field.editing()).toBe(false);
    expect(input.value).toBe("ab c");
    // One write in flight: "a", then the latest value typed meanwhile.
    expect(bridge.calls.filter(call => call.kind === "set").length).toBe(2);
    fixture.destroy();
    client.dispose();
  });

  test("a failed write keeps the draft and sets error; reset shows the View's value", async () => {
    const client = fakeClient("saved");
    const view = { state: signal<State | undefined>(client.snapshot), client: signal<ViewClient<State> | undefined>(client) };
    const field = TestBed.runInInjectionContext(() => injectTextField(view, state => state.title, async () => { throw new Error("rejected"); }));
    TestBed.tick();
    expect(field.value()).toBe("saved");
    field.input("saved!");
    await Promise.resolve();
    await Promise.resolve();
    expect(field.value()).toBe("saved!");
    expect(field.editing()).toBe(true);
    expect((field.error() as Error).message).toBe("rejected");
    field.reset();
    expect(field.value()).toBe("saved");
    expect(field.editing()).toBe(false);
    expect(field.error()).toBeUndefined();
  });

  test("follows the view's signals and writes nothing during IME composition", async () => {
    const view = { state: signal<State | undefined>(undefined), client: signal<ViewClient<State> | undefined>(undefined) };
    const written: string[] = [];
    const field = TestBed.runInInjectionContext(() => injectTextField(view, state => state.title, async (_client, value) => { written.push(value); }));
    TestBed.tick();
    expect(field.value()).toBe("");
    const client = fakeClient("remote");
    view.state.set(client.snapshot);
    view.client.set(client);
    TestBed.tick();
    expect(field.value()).toBe("remote");
    field.compositionStart();
    field.input({ target: { value: "か" } } as unknown as Event);
    expect(field.value()).toBe("か");
    expect(written).toEqual([]);
    field.compositionEnd("漢");
    expect(written).toEqual(["漢"]);
    await Promise.resolve();
    await Promise.resolve();
    expect(field.editing()).toBe(false);
  });

  test("requires an injection context or an injector", () => {
    const view = { state: signal<State | undefined>(undefined), client: signal<ViewClient<State> | undefined>(undefined) };
    expect(() => injectTextField(view, state => state.title, async () => {})).toThrow();
  });
});

describe("injectOperation progress", () => {
  test("follows the running operation's progress as a signal", async () => {
    const listeners = new Set<(status: BridgeOperationStatus) => void>();
    let finish!: (status: BridgeOperationStatus) => void;
    const terminal = new Promise<BridgeOperationStatus>(resolve => { finish = resolve; });
    const operation: BridgeOperation = {
      requestId: "upload", status: () => terminal, completion: terminal, wait: () => terminal,
      subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
      async cancel() { return { contract: "test", requestId: "upload", kind: "cancellation-requested" }; },
      async outcome() { await terminal; return bridgeSuccess(undefined); },
    };
    const handle = TestBed.runInInjectionContext(() => injectOperation(() => operation));
    expectTypeOf(handle.progress).toEqualTypeOf<Signal<number | undefined>>();
    const result = handle.run();
    await Promise.resolve();
    await Promise.resolve();
    for (const listener of [...listeners]) listener({ contract: "test", requestId: "upload", kind: "running", progress: 0.5 });
    expect(handle.progress()).toBe(0.5);
    finish({ contract: "test", requestId: "upload", kind: "succeeded" });
    await result;
    expect(handle.progress()).toBeUndefined();
    expect(listeners.size).toBe(0);
  });
});
