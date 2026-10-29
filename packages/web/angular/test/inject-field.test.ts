import "@angular/compiler";
import { Injector, type Signal } from "@angular/core";
import { TestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, expectTypeOf, test, vi } from "vitest";
import { FieldError, type FieldBaseline, type FieldState } from "@runic-artifex/views";
import { injectField, type FieldHandle } from "../dist/esm/index.js";
import { checkedTitle, FakeEditor, Fields, settle, viewOf } from "./fields.js";

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => {
  TestBed.resetTestingModule();
  vi.useRealTimers();
});

describe("injectField", () => {
  test("a text field keeps the draft while writing, then shows the View's normalized value", async () => {
    const editor = new FakeEditor({ title: "saved" });
    const field = TestBed.runInInjectionContext(() => injectField(viewOf(editor), Fields.title));
    expectTypeOf(field).toEqualTypeOf<FieldHandle<string>>();
    expectTypeOf(field.conflict).toEqualTypeOf<Signal<FieldBaseline<string> | undefined>>();
    TestBed.tick();
    expect(field.value()).toBe("saved");
    expect(field.text()).toBe("saved");
    expect(field.field).toMatchObject({ kind: "text", nullable: false });
    field.inputText({ target: { value: " draft " } } as unknown as Event);
    expect(field.value()).toBe(" draft ");
    expect(field.editing()).toBe(true);
    await settle();
    TestBed.tick();
    expect(editor.written).toEqual([" draft "]);
    expect(field.value()).toBe("draft");
    expect(field.editing()).toBe(false);
  });

  test("a number field parses its text and writes nothing for a text that is not a number", async () => {
    const editor = new FakeEditor({ count: 1 });
    const field = TestBed.runInInjectionContext(() => injectField(viewOf(editor), Fields.count));
    expectTypeOf(field.value).toEqualTypeOf<Signal<number>>();
    TestBed.tick();
    expect(field.text()).toBe("1");
    field.inputText("2.");
    expect(field.value()).toBe(2);
    expect(field.text()).toBe("2.");
    field.inputText("2.5");
    expect(field.error()).toBeInstanceOf(FieldError);
    expect((field.error() as FieldError).kind).toBe("invalid-text");
    expect(field.text()).toBe("2.5");
    await settle();
    TestBed.tick();
    expect(editor.written).toEqual([2]);
    field.inputText("12");
    await settle();
    TestBed.tick();
    expect(editor.written).toEqual([2, 12]);
    expect(field.value()).toBe(12);
    expect(field.error()).toBeUndefined();
  });

  test("a boolean field writes the value it is given", async () => {
    const editor = new FakeEditor();
    const field = TestBed.runInInjectionContext(() => injectField(viewOf(editor), Fields.done));
    TestBed.tick();
    expect(field.value()).toBe(false);
    field.input(true);
    await settle();
    TestBed.tick();
    expect(editor.written).toEqual([true]);
    expect(field.value()).toBe(true);
    expect(field.text()).toBe("true");
  });

  test("commit: \"pause\" writes after the pause, or at once on commit()", async () => {
    vi.useFakeTimers();
    const editor = new FakeEditor();
    const field = TestBed.runInInjectionContext(() => injectField(viewOf(editor), Fields.title, { commit: "pause", delay: 500 }));
    TestBed.tick();
    field.inputText("a");
    field.inputText("ab");
    expect(editor.written).toEqual([]);
    const committed = field.commit();
    expectTypeOf(committed).toEqualTypeOf<Promise<FieldState<string>>>();
    expect(editor.written).toEqual(["ab"]);
    await committed;
    TestBed.tick();
    expect(field.value()).toBe("ab");
    expect(field.editing()).toBe(false);
    field.inputText("abc");
    await vi.advanceTimersByTimeAsync(499);
    expect(editor.written).toEqual(["ab"]);
    await vi.advanceTimersByTimeAsync(1);
    expect(editor.written).toEqual(["ab", "abc"]);
  });

  test("a rejected checked write keeps the draft and sets a FieldError", async () => {
    const editor = new FakeEditor({ title: "saved" });
    const field = TestBed.runInInjectionContext(() => injectField(viewOf(editor),
      checkedTitle(() => ({ kind: "rejected", message: "Titles are locked." })), { checked: true }));
    TestBed.tick();
    field.inputText("changed");
    await settle();
    TestBed.tick();
    expect(editor.written).toEqual(["changed"]);
    expect(field.value()).toBe("changed");
    expect(field.editing()).toBe(true);
    expect(field.error()).toBeInstanceOf(FieldError);
    expect((field.error() as FieldError).kind).toBe("rejected");
    field.reset();
    expect(field.value()).toBe("saved");
    expect(field.error()).toBeUndefined();
  });

  test("a conflict keeps the draft until retry() writes it over the incoming value", async () => {
    const editor = new FakeEditor({ title: "saved" });
    let answer = 0;
    const field = TestBed.runInInjectionContext(() => injectField(viewOf(editor), checkedTitle(value => answer++ === 0
      ? { kind: "conflict", incoming: { value: "theirs", version: 2 }, message: "Changed elsewhere." }
      : { kind: "applied", snapshot: { value, version: 3 } }), { checked: true }));
    TestBed.tick();
    field.inputText("mine");
    await settle();
    expect(field.conflict()).toEqual({ value: "theirs", version: 2 });
    expect((field.error() as FieldError).kind).toBe("conflict");
    expect(field.value()).toBe("mine");
    field.retry();
    await settle();
    expect(editor.written).toEqual(["mine", "mine"]);
    expect(field.conflict()).toBeUndefined();
    expect(field.error()).toBeUndefined();
  });

  test("errors follow the member's validation in the View's state", () => {
    const editor = new FakeEditor();
    const field = TestBed.runInInjectionContext(() => injectField(viewOf(editor), Fields.title));
    TestBed.tick();
    expect(field.errors()).toEqual([]);
    editor.apply({ titleErrors: ["Enter a title."] });
    TestBed.tick();
    expect(field.errors()).toEqual(["Enter a title."]);
  });

  test("requires an injection context or an injector", () => {
    const editor = new FakeEditor();
    expect(() => injectField(viewOf(editor), Fields.title)).toThrow();
    expect(() => injectField(viewOf(editor), Fields.title, { injector: TestBed.inject(Injector) })).not.toThrow();
  });
});
