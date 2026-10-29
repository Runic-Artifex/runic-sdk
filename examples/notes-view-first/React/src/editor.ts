import { createElement as h, Fragment, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { useCommand, useField, useView } from "../../../../packages/web/react/src/index.js";
import { EditorFields, type EditorClient } from "../../Frontend/src/generated/editor.js";
import { describeSaveFailure } from "../../Frontend/src/save-failure.js";

// The React variant of Frontend/src/editor.ts. The rest of the page is the
// plain frontend; build.mjs swaps in this module.
export function mountEditor(host: HTMLElement, editor: EditorClient): () => void {
  const root = createRoot(host);
  root.render(h(Editor, { editor }));
  return () => {
    root.unmount();
    editor.dispose();
    host.replaceChildren();
  };
}

function Editor({ editor }: { readonly editor: EditorClient }): ReactNode {
  // The client is already connected, so useView only follows it.
  const view = useView(editor);
  const { state } = view;
  // A field is written after a pause in typing or when it loses focus, not on every keystroke.
  const title = useField(view, EditorFields.title, { commit: "pause" });
  const body = useField(view, EditorFields.body, { commit: "pause" });
  // Save waits for the fields' writes; a write that failed shows as the field's error.
  // A declared failure is save.failure; save.error holds unexpected ones.
  const save = useCommand(async () => {
    await Promise.all([title.commit(), body.commit()]);
    return editor.save();
  });
  if (!state) return h("p", null, "Connecting…");
  const issue = save.error ?? title.error ?? body.error;
  const alert = save.failure !== undefined ? describeSaveFailure(save.failure) : issue !== undefined ? String(issue) : undefined;
  return h(Fragment, null,
    h("h2", null, "Editor"),
    h("label", null, "Title ", h("input", title.inputProps)),
    h("label", null, "Body ", h("textarea", body.inputProps)),
    h("button", { "data-save": "", disabled: !state.canSave || save.pending, onClick: () => void save.run() }, "Save"),
    h("p", { "data-message": "", role: "status" }, `${state.isDirty ? "Unsaved changes. " : ""}${state.savedMessage}`),
    alert === undefined ? null : h("p", { role: "alert" }, alert));
}
