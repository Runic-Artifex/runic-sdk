import "@angular/compiler";
import { Component } from "@angular/core";
import { TestBed, type ComponentFixture } from "@angular/core/testing";
import { FormControl, ReactiveFormsModule } from "@angular/forms";
import { form, FormField } from "@angular/forms/signals";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach, beforeAll, describe, expect, test } from "vitest";
import { injectField } from "../dist/esm/index.js";
import * as forms from "../dist/esm/forms/index.js";
import { RunicFieldDirective, runicFieldModel, validateRunicField } from "../dist/esm/forms/index.js";
import { FakeEditor, Fields, settle, viewOf } from "./fields.js";

beforeAll(() => {
  TestBed.initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
});
afterEach(() => TestBed.resetTestingModule());

let editor: FakeEditor;

async function render<T>(component: new () => T): Promise<{ fixture: ComponentFixture<T>; root: HTMLElement }> {
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

async function stable(fixture: ComponentFixture<unknown>): Promise<void> {
  await settle();
  await fixture.whenStable();
}

function type(element: HTMLInputElement, value: string): void {
  element.value = value;
  element.dispatchEvent(new Event("input"));
}

class Alone {
  readonly editor = viewOf(editor);
  readonly title = injectField(this.editor, Fields.title);
  readonly paused = injectField(this.editor, Fields.title, { commit: "pause" });
  readonly done = injectField(this.editor, Fields.done);
}
Component({
  selector: "test-alone",
  standalone: true,
  imports: [RunicFieldDirective],
  template: `<input id="title" [runicField]="title"><input id="paused" [runicField]="paused">
    <input id="done" type="checkbox" [runicField]="done">`,
})(Alone);

describe("RunicFieldDirective alone", () => {
  test("writes what the user types and shows the View's value", async () => {
    editor = new FakeEditor({ title: "saved" });
    const { fixture, root } = await render(Alone);
    const input = root.querySelector<HTMLInputElement>("#title")!;
    expect(input.value).toBe("saved");
    expect(input.getAttribute("aria-invalid")).toBeNull();
    type(input, "saved! ");
    expect(editor.written).toEqual(["saved! "]);
    await stable(fixture);
    expect(input.value).toBe("saved!");
    editor.apply({ title: "remote" });
    await stable(fixture);
    expect(input.value).toBe("remote");
    editor.apply({ titleErrors: ["Too short."] });
    await stable(fixture);
    expect(input.getAttribute("aria-invalid")).toBe("true");
  });

  test("writes nothing during IME composition", async () => {
    editor = new FakeEditor();
    const { fixture, root } = await render(Alone);
    const input = root.querySelector<HTMLInputElement>("#title")!;
    input.dispatchEvent(new Event("compositionstart"));
    type(input, "か");
    await stable(fixture);
    expect(editor.written).toEqual([]);
    expect(input.value).toBe("か");
    input.value = "漢";
    input.dispatchEvent(new Event("compositionend"));
    expect(editor.written).toEqual(["漢"]);
    await stable(fixture);
    expect(input.value).toBe("漢");
  });

  test("commits a paused field on blur", async () => {
    editor = new FakeEditor();
    const { fixture, root } = await render(Alone);
    const input = root.querySelector<HTMLInputElement>("#paused")!;
    type(input, "draft");
    expect(editor.written).toEqual([]);
    input.dispatchEvent(new Event("blur"));
    expect(editor.written).toEqual(["draft"]);
    await stable(fixture);
    expect(fixture.componentInstance.paused.editing()).toBe(false);
    fixture.destroy();
  });

  test("binds a checkbox to a boolean field", async () => {
    editor = new FakeEditor();
    const { fixture, root } = await render(Alone);
    const checkbox = root.querySelector<HTMLInputElement>("#done")!;
    expect(checkbox.checked).toBe(false);
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event("input"));
    checkbox.dispatchEvent(new Event("change"));
    expect(editor.written).toEqual([true]);
    await stable(fixture);
    expect(checkbox.checked).toBe(true);
    expect(fixture.componentInstance.done.value()).toBe(true);
    editor.apply({ done: false });
    await stable(fixture);
    expect(checkbox.checked).toBe(false);
  });
});

class Reactive {
  readonly editor = viewOf(editor);
  readonly title = injectField(this.editor, Fields.title);
  readonly control = new FormControl("initial", { nonNullable: true });
}
Component({
  selector: "test-reactive",
  standalone: true,
  imports: [RunicFieldDirective, ReactiveFormsModule],
  template: `<input [runicField]="title" [formControl]="control">`,
})(Reactive);

describe("RunicFieldDirective with a FormControl", () => {
  test("the View's value flows into the control without dirtying it; the initial value is not written", async () => {
    editor = new FakeEditor({ title: "saved" });
    const { fixture, root } = await render(Reactive);
    const { control } = fixture.componentInstance;
    const changes: unknown[] = [];
    control.valueChanges.subscribe(value => changes.push(value));
    await stable(fixture);
    expect(editor.written).toEqual([]);
    expect(control.value).toBe("saved");
    expect(root.querySelector("input")!.value).toBe("saved");
    editor.apply({ title: "remote" });
    await stable(fixture);
    expect(control.value).toBe("remote");
    expect(control.pristine).toBe(true);
    expect(changes).toEqual([]);
    expect(editor.written).toEqual([]);
  });

  test("user typing writes to the View and updates the control", async () => {
    editor = new FakeEditor({ title: "saved" });
    const { fixture, root } = await render(Reactive);
    const { control } = fixture.componentInstance;
    const input = root.querySelector("input")!;
    type(input, "typed");
    expect(editor.written).toEqual(["typed"]);
    expect(control.value).toBe("typed");
    expect(control.dirty).toBe(true);
    input.dispatchEvent(new Event("blur"));
    expect(control.touched).toBe(true);
  });

  test("patchValue writes through when it changes the value", async () => {
    editor = new FakeEditor({ title: "saved" });
    const { fixture, root } = await render(Reactive);
    const { control, title } = fixture.componentInstance;
    await stable(fixture);
    control.patchValue("saved");
    expect(editor.written).toEqual([]);
    control.patchValue("patched");
    expect(editor.written).toEqual(["patched"]);
    expect(title.value()).toBe("patched");
    await stable(fixture);
    expect(root.querySelector("input")!.value).toBe("patched");
    expect(editor.snapshot.title).toBe("patched");
  });

  test("the control is invalid with the View's errors", async () => {
    editor = new FakeEditor({ title: "saved" });
    const { fixture, root } = await render(Reactive);
    const { control } = fixture.componentInstance;
    expect(control.errors).toBeNull();
    editor.apply({ titleErrors: ["Enter a title."] });
    await stable(fixture);
    expect(control.errors).toEqual({ runic: ["Enter a title."] });
    expect(control.invalid).toBe(true);
    expect(root.querySelector("input")!.getAttribute("aria-invalid")).toBe("true");
    editor.apply({ titleErrors: [] });
    await stable(fixture);
    expect(control.errors).toBeNull();
  });

  test("disabling the control disables the element", async () => {
    editor = new FakeEditor();
    const { fixture, root } = await render(Reactive);
    fixture.componentInstance.control.disable();
    expect(root.querySelector("input")!.disabled).toBe(true);
    fixture.componentInstance.control.enable();
    expect(root.querySelector("input")!.disabled).toBe(false);
  });
});

class Signals {
  readonly editor = viewOf(editor);
  readonly title = injectField(this.editor, Fields.title);
  readonly titleForm = form(runicFieldModel(this.title), path => validateRunicField(path, this.title));
}
Component({
  selector: "test-signal-forms",
  standalone: true,
  imports: [FormField],
  template: `<input [formField]="titleForm">`,
})(Signals);

describe("Signal Forms", () => {
  test("a form over runicFieldModel writes through and validateRunicField surfaces the View's errors", async () => {
    editor = new FakeEditor({ title: "saved" });
    const { fixture, root } = await render(Signals);
    const { titleForm, title } = fixture.componentInstance;
    const input = root.querySelector("input")!;
    expect(input.value).toBe("saved");
    expect(titleForm().value()).toBe("saved");
    type(input, "typed ");
    await stable(fixture);
    expect(editor.written).toEqual(["typed "]);
    expect(title.value()).toBe("typed");
    expect(input.value).toBe("typed");
    expect(titleForm().valid()).toBe(true);
    editor.apply({ titleErrors: ["Enter a title."] });
    await stable(fixture);
    expect(titleForm().errors().map(({ kind, message }) => ({ kind, message }))).toEqual([{ kind: "runic", message: "Enter a title." }]);
    expect(titleForm().valid()).toBe(false);
    titleForm().value.set("set");
    expect(editor.written).toEqual(["typed ", "set"]);
  });
});

test("the forms entry exports the directive and the Signal Forms helpers", () => {
  expect(Object.keys(forms).sort()).toEqual(["RunicFieldDirective", "runicFieldModel", "validateRunicField"]);
});
