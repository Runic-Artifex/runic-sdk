import { createElement as h, Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { useCommand, useView } from "../../../../packages/web/react/src/index.js";
import type { EditorClient } from "../../Frontend/src/generated/editor.js";
import { EditorWrites } from "../../Frontend/src/editor-writes.js";
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
  const { state } = useView(editor);
  const [writeError, setWriteError] = useState<unknown>();
  const [writes] = useState(() => new EditorWrites(setWriteError));
  // A declared failure is save.failure; save.error holds unexpected ones.
  const save = useCommand(() => writes.run(() => editor.save()));
  const title = useRef<HTMLInputElement>(null);
  const body = useRef<HTMLTextAreaElement>(null);
  // Native change events, as the other variants use: a field is written once
  // it is committed, not on every keystroke.
  useEffect(() => {
    const fields = [[title.current, (value: string) => editor.setTitle(value)], [body.current, (value: string) => editor.setBody(value)]] as const;
    const stops = fields.map(([field, write]) => {
      if (!field) return () => {};
      const changed = () => { const value = field.value; writes.enqueue(() => write(value)); };
      field.addEventListener("change", changed);
      return () => field.removeEventListener("change", changed);
    });
    return () => { for (const stop of stops) stop(); };
  }, [editor, writes, state !== undefined]);
  useEffect(() => {
    if (!state) return;
    if (title.current && document.activeElement !== title.current) title.current.value = state.title;
    if (body.current && document.activeElement !== body.current) body.current.value = state.body;
  }, [state]);
  if (!state) return h("p", null, "Connecting…");
  const issue = save.error ?? writeError;
  const alert = save.failure !== undefined ? describeSaveFailure(save.failure) : issue !== undefined ? String(issue) : undefined;
  return h(Fragment, null,
    h("h2", null, "Editor"),
    h("label", null, "Title ", h("input", { ref: title, defaultValue: state.title })),
    h("label", null, "Body ", h("textarea", { ref: body, defaultValue: state.body })),
    h("button", { "data-save": "", disabled: !state.canSave || save.pending, onClick: () => void save.run() }, "Save"),
    h("p", { "data-message": "", role: "status" }, `${state.isDirty ? "Unsaved changes. " : ""}${state.savedMessage}`),
    alert === undefined ? null : h("p", { role: "alert" }, alert));
}
