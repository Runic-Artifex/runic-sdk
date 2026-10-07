import { command, createEffectAction, operation, states } from "@runic-artifex/views-effect";
import * as Effect from "effect/Effect";
import * as Schedule from "effect/Schedule";
import * as Semaphore from "effect/Semaphore";
import * as Stream from "effect/Stream";
import type { EditorClient, EditorState } from "../../Frontend/src/generated/editor.js";
import { describeSaveFailure } from "../../Frontend/src/save-failure.js";

// The Effect variant of Frontend/src/editor.ts. The rest of the page is the
// plain frontend; build.mjs swaps in this module.
export function mountEditor(host: HTMLElement, editor: EditorClient): () => void {
  host.innerHTML = `<h2>Editor</h2><label>Title <input></label><label>Body <textarea></textarea></label><button data-save>Save</button><p data-message role="status"></p><p data-error role="alert" hidden></p>`;
  const title = host.querySelector("input")!;
  const body = host.querySelector("textarea")!;
  const save = host.querySelector<HTMLButtonElement>("[data-save]")!;
  const message = host.querySelector<HTMLElement>("[data-message]")!;
  const error = host.querySelector<HTMLElement>("[data-error]")!;
  let active = true;
  let canSave = false;
  const showError = (cause: { readonly message: string } | undefined) => {
    if (!active) return;
    error.textContent = cause === undefined ? "" : cause.message;
    error.hidden = cause === undefined;
  };
  const render = (state: EditorState) => {
    if (document.activeElement !== title) title.value = state.title;
    if (document.activeElement !== body) body.value = state.body;
    canSave = state.canSave;
    save.disabled = !canSave || saving.current.pending;
    message.textContent = `${state.isDirty ? "Unsaved changes. " : ""}${state.savedMessage}`;
  };

  // One permit keeps field writes in order. A Save takes its turn only to wait
  // for the writes queued before it, so edits made while it runs are not held up.
  const writes = Semaphore.makeUnsafe(1);
  const write = (run: () => Promise<EditorState>) => Effect.runFork(writes.withPermit(command(run)).pipe(
    Effect.match({ onFailure: showError, onSuccess: () => showError(undefined) })));

  // An explicit request ID makes the retry safe: a repeated start returns the
  // operation .NET is already running. The timeout cancels it in .NET; after
  // the editor is gone, a failed observation is no longer retried.
  const saving = createEffectAction(() => writes.withPermit(Effect.void).pipe(Effect.andThen(operation(id => editor.startSaveWithRequestId(id), {
    requestId: globalThis.crypto.randomUUID(),
    retry: Schedule.recurs(2),
    while: () => active,
    timeout: "10 seconds",
  }))).pipe(Effect.tapError(failure => Effect.sync(() => showError(
    // Save declares SaveFailure, which arrives as ViewDomainFailure<SaveFailure>.
    failure._tag === "ViewDomainFailure" ? { message: describeSaveFailure(failure.failure) } : failure)))));
  const stopSaving = saving.subscribe(() => {
    save.disabled = !canSave || saving.current.pending;
    if (saving.current.status === "success") showError(undefined);
  });

  const following = Effect.runFork(Stream.runForEach(states(editor), state => Effect.sync(() => render(state))));
  const titleChanged = () => { const value = title.value; write(() => editor.setTitle(value)); };
  const bodyChanged = () => { const value = body.value; write(() => editor.setBody(value)); };
  const saveClicked = () => { void saving.run(); };
  title.addEventListener("change", titleChanged);
  body.addEventListener("change", bodyChanged);
  save.addEventListener("click", saveClicked);
  return () => {
    active = false;
    title.removeEventListener("change", titleChanged);
    body.removeEventListener("change", bodyChanged);
    save.removeEventListener("click", saveClicked);
    // Leaving the editor keeps a save running in .NET, as the plain editor does,
    // so this does not interrupt it; setting `active` stops its retries.
    stopSaving();
    following.interruptUnsafe();
    editor.dispose();
    host.replaceChildren();
  };
}
