// appearance/1: injectAppearance follows the host's pushes until its injection context is destroyed.
import "@angular/compiler";
import { PLATFORM_ID } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, expect, test } from "vitest";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { injectAppearance } from "../dist/esm/index.js";

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => {
  TestBed.resetTestingModule();
});

test("is a signal that follows the host's appearance until the injector is destroyed", async () => {
  const bridge = installMockBridge(createMockBridge());
  bridge.view("shell", { state: {} });
  await connectView({ contract: "Tests.Shell:fp-1", route: "shell", members: {} });
  const look = TestBed.runInInjectionContext(() => injectAppearance());
  bridge.setAppearance({ textScale: 1.5, accentColor: "#3584e4" });
  await bridge.flush();
  expect([look().textScale, look().accentColor]).toEqual([1.5, "#3584e4"]);
  TestBed.resetTestingModule();
  bridge.setAppearance({ textScale: 2 });
  await bridge.flush();
  expect(look().textScale, "a destroyed injector stops following").toBe(1.5);
});

test("every member is null on the server", () => {
  TestBed.configureTestingModule({ providers: [{ provide: PLATFORM_ID, useValue: "server" }] });
  const look = TestBed.runInInjectionContext(() => injectAppearance());
  const { engine, ...members } = look();
  expect([...Object.values(members), ...Object.values(engine)].every(value => value === null)).toBe(true);
});
