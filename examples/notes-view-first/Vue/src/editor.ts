import { createApp, defineComponent, h, onMounted, ref, shallowRef, watch, type PropType } from "vue";
import { useCommand, useView } from "../../../../packages/web/vue/src/index.js";
import type { EditorClient } from "../../Frontend/src/generated/editor.js";
import { EditorWrites } from "../../Frontend/src/editor-writes.js";
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
    const { state } = useView(() => props.editor);
    const writeError = shallowRef<unknown>();
    const writes = new EditorWrites(cause => { writeError.value = cause; });
    // A declared failure is save.failure; save.error holds unexpected ones.
    const save = useCommand(() => writes.run(() => props.editor.save()));
    const title = ref<HTMLInputElement>();
    const body = ref<HTMLTextAreaElement>();
    const sync = () => {
      const current = state.value;
      if (!current) return;
      if (title.value && document.activeElement !== title.value) title.value.value = current.title;
      if (body.value && document.activeElement !== body.value) body.value.value = current.body;
    };
    watch(state, sync, { flush: "post" });
    onMounted(sync);
    return () => {
      const current = state.value;
      if (!current) return h("p", "Connecting…");
      const issue = save.error ?? writeError.value;
      const alert = save.failure !== undefined ? describeSaveFailure(save.failure) : issue !== undefined ? String(issue) : undefined;
      return [
        h("h2", "Editor"),
        h("label", ["Title ", h("input", { ref: title,
          onChange: (event: Event) => { const value = (event.target as HTMLInputElement).value; writes.enqueue(() => props.editor.setTitle(value)); } })]),
        h("label", ["Body ", h("textarea", { ref: body,
          onChange: (event: Event) => { const value = (event.target as HTMLTextAreaElement).value; writes.enqueue(() => props.editor.setBody(value)); } })]),
        h("button", { "data-save": "", disabled: !current.canSave || save.pending, onClick: () => void save.run() }, "Save"),
        h("p", { "data-message": "", role: "status" }, `${current.isDirty ? "Unsaved changes. " : ""}${current.savedMessage}`),
        alert === undefined ? null : h("p", { role: "alert" }, alert),
      ];
    };
  },
});
