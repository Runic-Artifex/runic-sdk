// Hosted regions (experimental): RunicHostedRegion is <runic-hosted-region> itself, with its projected fallback.
import "@angular/compiler";
import { Component } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, expect, test } from "vitest";
import { RunicHostedRegion, type HostedRegionStateChange } from "@runic-artifex/angular/experimental";

// The element's shadow roots are closed: keep them, to load a region's frame and read what its slots show.
const shadows = new WeakMap<Element, ShadowRoot>();
const attachShadow = Element.prototype.attachShadow;
Element.prototype.attachShadow = function (this: Element, init: ShadowRootInit) {
  const shadow = attachShadow.call(this, init);
  shadows.set(this, shadow);
  return shadow;
};
// happy-dom loads no frame page (and logs that it does not): the frame's load is the test's.
const loadFrame = (region: Element) => shadows.get(region)!.querySelector("iframe")!.dispatchEvent(new Event("load"));
// The text of the content the element's slots show: the state's slot, else the slot nested in it as its fallback
// content, and so on (happy-dom's flattened assignment does not follow fallback content).
const shown = (region: Element) => {
  for (let slot = shadows.get(region)!.querySelector("slot"); slot; slot = slot.querySelector(":scope > slot")) {
    const assigned = slot.assignedElements();
    if (assigned.length > 0) return assigned.map(element => element.textContent).join("|");
  }
  return "";
};

// A producer with hosted-regions/1 and hosted-region-loading/1 behind the page's Bridge: it records requests, and the test
// pushes.
const listeners = new Set<(message: string) => void>();
const requests: Record<string, unknown>[] = [];
(globalThis as { __runicBridge?: unknown }).__runicBridge = {
  isConnected: () => true,
  async request(text: string) {
    const message = JSON.parse(text) as Record<string, unknown>;
    requests.push(message);
    return message["kind"] === "hello"
      ? JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-producer", version: "1.0.0" }, features: ["hosted-regions/1", "hosted-region-loading/1"] })
      : '{"ok":true}';
  },
  onMessage(listener: (message: string) => void) { listeners.add(listener); return () => listeners.delete(listener); },
  onConnectionChange(listener: (connected: boolean) => void) { listener(true); return () => {}; },
};
const push = (message: object) => { for (const listener of listeners) listener(JSON.stringify(message)); };
const until = async (condition: () => boolean) => {
  for (let attempt = 0; attempt < 100 && !condition(); attempt++) await new Promise(resolve => setTimeout(resolve, 10));
  expect(condition()).toBe(true);
};

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => {
  TestBed.resetTestingModule();
});

class Host {
  readonly states: HostedRegionStateChange[] = [];
}
Component({
  selector: "test-region-host",
  standalone: true,
  imports: [RunicHostedRegion],
  template: `<runic-hosted-region #check="runicHostedRegion" name="check" sizing="content-height" label="System check" class="step"
    (stateChange)="states.push($event)"><p>fallback {{ check.state() }}</p></runic-hosted-region>`,
})(Host);

test("is the element, declares the region and projects its fallback with the region's state", async () => {
  const fixture = TestBed.createComponent(Host);
  document.body.append(fixture.nativeElement as HTMLElement);
  fixture.detectChanges();
  const region = (fixture.nativeElement as HTMLElement).querySelector("runic-hosted-region")!;
  expect([region.getAttribute("name"), region.getAttribute("sizing"), region.getAttribute("label"), region.className]).toEqual(["check", "content-height", "System check", "step"]);
  expect(customElements.get("runic-hosted-region")).toBeDefined();
  await until(() => requests.some(request => request["kind"] === "hosted-region-declare" && request["sizing"] === "content-height"));
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback waiting");
  push({ kind: "hosted-region-empty", name: "check", reason: "ended" });
  await until(() => region.getAttribute("state") === "ended");
  fixture.detectChanges();
  expect(region.querySelector("[slot=fallback]")!.textContent).toBe("fallback ended");
  expect(fixture.componentInstance.states).toEqual([{ state: "ended", previous: "waiting", stalled: false, reason: null }]);
  fixture.destroy();
  (fixture.nativeElement as HTMLElement).remove();
  await until(() => requests.some(request => request["kind"] === "hosted-region-remove"));
});

class StatusHost {
  readonly states: HostedRegionStateChange[] = [];
}
// Two regions: the first with stalled and failed content, the second with its projected fallback alone.
Component({
  selector: "test-region-status-host",
  standalone: true,
  imports: [RunicHostedRegion],
  template: `<runic-hosted-region #first="runicHostedRegion" name="with" (stateChange)="states.push($event)">
      <p>fallback {{ first.state() }}{{ first.stalled() ? " stalled" : "" }}</p>
      <p slot="stalled">taking longer</p>
      <button slot="failed" type="button" (click)="first.retry()">failed: {{ first.failureReason() }}</button>
    </runic-hosted-region>
    <runic-hosted-region #second="runicHostedRegion" name="without">
      <p>fallback {{ second.state() }}{{ second.stalled() ? " stalled" : "" }} {{ second.failureReason() }}</p>
    </runic-hosted-region>`,
})(StatusHost);

const frame = (name: string) => ({ kind: "hosted-region-content", name, embedding: "frame", src: `http://127.0.0.1:9/_runic_frame/${name}`, title: null, allow: "" });

test("projects stalled and failed content, retry asks the host, and a region without them shows its projected fallback", async () => {
  const fixture = TestBed.createComponent(StatusHost);
  document.body.append(fixture.nativeElement as HTMLElement);
  fixture.detectChanges();
  const [withContent, without] = [...(fixture.nativeElement as HTMLElement).querySelectorAll("runic-hosted-region")];
  await until(() => ["with", "without"].every(name => requests.some(request => request["kind"] === "hosted-region-declare" && request["name"] === name)));
  // Stalled and failed content stays a child of the element, with its slot.
  expect([...withContent!.children].map(child => child.getAttribute("slot"))).toEqual(["stalled", "failed", "fallback"]);
  expect([...without!.children].map(child => child.getAttribute("slot"))).toEqual(["fallback"]);

  // Stalled: the stalled content, else the projected fallback for loading.
  push(frame("with"));
  push(frame("without"));
  await until(() => withContent!.getAttribute("state") === "loading" && without!.getAttribute("state") === "loading");
  fixture.detectChanges();
  expect([shown(withContent!), shown(without!)]).toEqual(["fallback loading", "fallback loading "]);
  loadFrame(withContent!);
  loadFrame(without!);
  await until(() => withContent!.hasAttribute("stalled") && without!.hasAttribute("stalled"));
  fixture.detectChanges();
  expect([shown(withContent!), shown(without!)]).toEqual(["taking longer", "fallback loading stalled "]);
  expect(fixture.componentInstance.states.at(-1)).toEqual({ state: "loading", previous: "loading", stalled: true, reason: null });

  // Failed: the failed content with its reason, else the projected fallback for failed.
  push({ kind: "hosted-region-empty", name: "with", reason: "failed", failure: "unreachable" });
  push({ kind: "hosted-region-empty", name: "without", reason: "failed", failure: "timeout" });
  await until(() => withContent!.getAttribute("state") === "failed" && without!.getAttribute("state") === "failed");
  fixture.detectChanges();
  expect([shown(withContent!), shown(without!)]).toEqual(["failed: unreachable", "fallback failed timeout"]);
  withContent!.querySelector("button")!.click();
  await until(() => requests.some(request => request["kind"] === "hosted-region-retry" && request["name"] === "with"));
  expect(requests.find(request => request["kind"] === "hosted-region-retry")).not.toHaveProperty("src");
  fixture.destroy();
  (fixture.nativeElement as HTMLElement).remove();
});
