import { createEffectAction, operation, states } from "@runic-artifex/views-effect";
import * as Effect from "effect/Effect";
import * as Schedule from "effect/Schedule";
import * as Stream from "effect/Stream";
import { bindField, commitFields, describeBridgeError } from "@runic-artifex/views";
import { EditorFields, type EditorClient, type EditorState } from "../../Frontend/src/generated/editor.js";
import { describeSaveFailure } from "../../Frontend/src/save-failure.js";

// The Effect variant of Frontend/src/editor.ts. The rest of the page is the
// plain frontend; build.mjs swaps in this module.
export function mountEditor(host: HTMLElement, editor: EditorClient): () => void {
  host.innerHTML = `<h2>Editor</h2><label>Title <input></label><label>Body <textarea></textarea></label><button data-save>Save</button><p data-message role="status"></p><p data-error role="alert" hidden></p>`;
  const titleInput = host.querySelector("input")!;
  const bodyInput = host.querySelector("textarea")!;
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
    canSave = state.canSave;
    save.disabled = !canSave || saving.current.pending;
    message.textContent = `${state.isDirty ? "Unsaved changes. " : ""}${state.savedMessage}`;
  };

  // A field is written after a pause in typing or when it loses focus. A failed write shows until the field's next
  // successful one.
  const title = bindField(titleInput, editor, EditorFields.title, { commit: "pause" });
  const body = bindField(bodyInput, editor, EditorFields.body, { commit: "pause" });
  let writeError: unknown;
  const showWriteError = () => {
    const cause = title.current.error ?? body.current.error;
    if (Object.is(cause, writeError)) return;
    writeError = cause;
    showError(cause === undefined ? undefined : { message: describeBridgeError(cause) });
  };
  const stopTitle = title.subscribe(showWriteError);
  const stopBody = body.subscribe(showWriteError);
  // Save waits for the fields' writes, and does not start after one that failed.
  const fieldsWritten = Effect.tryPromise({
    try: () => commitFields(title, body),
    catch: cause => ({ _tag: "FieldWriteFailed", message: describeBridgeError(cause) }) as const,
  });

  // An explicit request ID makes the retry safe: a repeated start returns the
  // operation .NET is already running. The timeout cancels it in .NET; after
  // the editor is gone, a failed observation is no longer retried.
  const saving = createEffectAction(() => fieldsWritten.pipe(Effect.andThen(operation(id => editor.startSave({ requestId: id }), {
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
  const saveClicked = () => { void saving.run(); };
  save.addEventListener("click", saveClicked);
  return () => {
    active = false;
    save.removeEventListener("click", saveClicked);
    // Leaving the editor keeps a save running in .NET, as the plain editor does,
    // so this does not interrupt it; setting `active` stops its retries.
    stopSaving();
    following.interruptUnsafe();
    stopTitle();
    stopBody();
    // Unwires the inputs; text that waits for its pause is written before the editor is released.
    title.dispose();
    body.dispose();
    editor.dispose();
    host.replaceChildren();
  };
}
