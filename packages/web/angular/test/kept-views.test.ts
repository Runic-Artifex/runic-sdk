// #798: each test installs a new mock Bridge, as the Testing section of @runic-artifex/views recommends. The client
// that injectView keeps after TestBed destroys the previous test's component belongs to the previous mock, so the next
// test's component connects to its own mock: no fake timers, no waiting for `defaultKeepAlive`.
import "@angular/compiler";
import { Component } from "@angular/core";
import { TestBed, type ComponentFixture } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test, vi } from "vitest";
import { injectView, type ViewHandle } from "@runic-artifex/angular";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge, type MockBridge } from "@runic-artifex/views/mock";

type State = { readonly count: number };

// What a generated page reference is: one `connect` function, shared by every binding of the View.
const counter = {
  connect: () => connectView<State>({ contract: "mock.counter:mock", route: "counter", members: { count: wire => wire as number } }),
};

class CounterView {
  readonly view: ViewHandle<ViewConnection<State>> = injectView(counter);
}
Component({ selector: "test-counter-view", standalone: true, template: "{{ view.state()?.count }}" })(CounterView);

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
// Destroys the test's component: injectView keeps its client for `keepAlive` (2000 ms by default).
afterEach(() => TestBed.resetTestingModule());

async function mountWith(start: number): Promise<{ readonly bridge: MockBridge; readonly fixture: ComponentFixture<CounterView> }> {
  const bridge = installMockBridge(createMockBridge());
  bridge.view("counter", { state: { count: start }, commands: { bump: state => ({ count: (state["count"] as number) + 1 }) } });
  const fixture = TestBed.createComponent(CounterView);
  await vi.waitFor(() => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toBe("");
  });
  return { bridge, fixture };
}

async function bumpAndCheck(start: number): Promise<void> {
  const { bridge, fixture } = await mountWith(start);
  expect(fixture.nativeElement.textContent).toBe(String(start));
  await fixture.componentInstance.view.client()!.command("bump");
  await vi.waitFor(() => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toBe(String(start + 1));
  });
  expect(bridge.calls.filter(call => call.kind === "command")).toHaveLength(1);
}

describe("injectView with a new mock Bridge per test", () => {
  test("the first test connects to its mock", () => bumpAndCheck(1));
  test("the next test connects to its own mock, not to the client the first test's binding kept", () => bumpAndCheck(10));
  test("and so does every later test", () => bumpAndCheck(100));
});
