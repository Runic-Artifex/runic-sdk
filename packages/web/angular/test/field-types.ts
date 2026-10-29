// Type-level checks for injectField; compiled by `tsc -p tsconfig.test.json` in the test script, never run.
import type { Injector } from "@angular/core";
import type { BridgeField, ViewClient } from "@runic-artifex/views";
import { injectField, type FieldHandle, type ViewHandle } from "../dist/esm/index.js";

// The shapes the generator emits for a View with a `title` and a `step`.
interface EditorState { readonly title: string; readonly step: number }
interface EditorClient extends ViewClient<EditorState> { setTitle(value: string): Promise<void> }
declare const EditorFields: { readonly title: BridgeField<EditorClient, EditorState, string>; readonly step: BridgeField<EditorClient, EditorState, number> };
declare const injector: Injector;

// A page reference's client, and a root client that is also Disposable.
declare const page: ViewHandle<EditorClient>;
declare const root: ViewHandle<EditorClient & Disposable>;
export const title: FieldHandle<string> = injectField(page, EditorFields.title, { injector });
export const step: FieldHandle<number> = injectField(root, EditorFields.step, { injector, commit: "pause", checked: true });
// `[runicField]` takes any field.
export const any: FieldHandle<unknown> = title;
// @ts-expect-error A field of another View's client does not fit.
injectField(page, {} as BridgeField<ViewClient<{ readonly other: string }>, { readonly other: string }, string>, { injector });
