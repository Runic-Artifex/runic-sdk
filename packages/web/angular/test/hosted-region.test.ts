// Hosted regions (experimental): RunicHostedRegion is <runic-hosted-region> itself, with its projected fallback.
import "@angular/compiler";
import { Component } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, expect, test } from "vitest";
import { RunicHostedRegion, type HostedRegionStateChange } from "../dist/esm/experimental.js";

// A producer with hosted-regions/1 behind the page's Bridge: it records requests, and the test pushes.
const listeners = new Set<(message: string) => void>();
const requests: Record<string, unknown>[] = [];
(globalThis as { __runicBridge?: unknown }).__runicBridge = {
  isConnected: () => true,
  async request(text: string) {
    const message = JSON.parse(text) as Record<string, unknown>;
    requests.push(message);
    return message["kind"] === "hello"
      ? JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test-producer", version: "1.0.0" }, features: ["hosted-regions/1"] })
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
  expect(fixture.componentInstance.states).toEqual([{ state: "ended", previous: "waiting" }]);
  fixture.destroy();
  (fixture.nativeElement as HTMLElement).remove();
  await until(() => requests.some(request => request["kind"] === "hosted-region-remove"));
});
