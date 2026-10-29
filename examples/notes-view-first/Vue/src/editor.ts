import { createApp, defineComponent, h, vModelText, withDirectives, type PropType } from "vue";
import { useCommand, useField, useView } from "../../../../packages/web/vue/src/index.js";
import { EditorFields, type EditorClient } from "../../Frontend/src/generated/editor.js";
import { describeSaveFailure } from "../../Frontend/src/save-failure.js";

// The Vue variant of Frontend/src/editor.ts. The rest of the page is the
// plain frontend; build.mjs swaps in this module.
export function mountEditor(host: HTMLElement, editor: EditorClient): () => void {
  const app = createApp(Editor, { editor });
  app.mount(host);
  return () => {
    app.unmount();
    editor.dispose();
    host.replaceChildren();
  };
}

const Editor = defineComponent({
  props: { editor: { type: Object as PropType<EditorClient>, required: true } },
  setup(props) {
    // The client is already connected, so useView only follows it.
    const view = useView(() => props.editor);
    const { state } = view;
    // A field is written after a pause in typing or when it loses focus, not on every keystroke.
    const title = useField(view, EditorFields.title, { commit: "pause" });
    const body = useField(view, EditorFields.body, { commit: "pause" });
    // Save waits for the fields' writes, and does not start after one that failed.
    // A declared failure is save.failure; save.error holds unexpected ones.
    const save = useCommand(async () => {
      await Promise.all([title.commit(), body.commit()]);
      return title.error === undefined && body.error === undefined ? props.editor.save() : undefined;
    });
    // The render-function form of `<input v-model="title.value" @blur="title.commit">`.
    const field = (tag: "input" | "textarea", handle: typeof title) => withDirectives(h(tag, {
      "onUpdate:modelValue": (value: string) => { handle.value = value; },
      onBlur: () => { void handle.commit(); },
      "aria-invalid": handle.errors.length > 0 || handle.error !== undefined,
    }), [[vModelText, handle.value]]);
    return () => {
      const current = state.value;
      if (!current) return h("p", "Connecting…");
      const issue = save.error.value ?? title.error ?? body.error;
      const alert = save.failure.value !== undefined ? describeSaveFailure(save.failure.value) : issue !== undefined ? String(issue) : undefined;
      return [
        h("h2", "Editor"),
        h("label", ["Title ", field("input", title)]),
        h("label", ["Body ", field("textarea", body)]),
        h("button", { "data-save": "", disabled: !current.canSave || save.pending.value, onClick: () => void save.run() }, "Save"),
        h("p", { "data-message": "", role: "status" }, `${current.isDirty ? "Unsaved changes. " : ""}${current.savedMessage}`),
        alert === undefined ? null : h("p", { role: "alert" }, alert),
      ];
    };
  },
});
